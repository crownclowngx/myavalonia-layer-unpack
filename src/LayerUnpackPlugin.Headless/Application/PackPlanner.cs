using System.Security.Cryptography;
using LayerUnpackPlugin.Headless.Contracts;
using LayerUnpackPlugin.Headless.Infrastructure;

namespace LayerUnpackPlugin.Headless.Application;

/// <summary>生成可检查的打包清单。准备阶段只读：规范化根、分配名字、枚举和记录内容摘要，不创建输出目录。</summary>
public sealed class PackPlanner
{
    public async Task<PackPlan> PrepareAsync(PackRequest request, IProgress<PackProgress>? progress, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Limits.Validate();
        if (request.Inputs.Count == 0 || request.Inputs.Count > request.Limits.MaxInputs)
            throw new PackValidationException("请添加输入，且输入项数量不能超过限制。");
        if (string.IsNullOrWhiteSpace(request.OutputPath) || !Path.IsPathFullyQualified(request.OutputPath))
            throw new PackValidationException("请选择完整的 ZIP 输出路径。");
        var output = Path.GetFullPath(request.OutputPath);
        var name = Path.GetFileName(output);
        if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || name.Length > 180 || name.Length <= 4)
            throw new PackValidationException("压缩包名称应以 .zip 结尾，且长度不超过 180 个字符。");
        PackPaths.CheckEntry(name, false);
        PackPaths.Check(output);
        if (Directory.Exists(output)) throw new PackValidationException("ZIP 输出路径不能是已有文件夹。");
        var sourcePaths = request.Inputs.Select(p =>
        {
            if (string.IsNullOrWhiteSpace(p) || !Path.IsPathFullyQualified(p)) throw new PackValidationException("输入必须是完整的文件或文件夹路径。");
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(p));
        }).Distinct(PackPaths.Comparer).OrderBy(p => p, PackPaths.Comparer).ToArray();
        var sources = sourcePaths.Select(p => new PackRoot(p, Path.GetFileName(p), PackPaths.IsDirectory(p))).ToArray();
        if (sources.Any(s => string.IsNullOrEmpty(s.EntryName))) throw new PackValidationException("请选具体文件夹，不支持整个磁盘根目录。");
        if (sources.Any(s => PackPaths.Equal(s.SourcePath, output))) throw new PackValidationException("源文件不能同时作为本次 ZIP 输出。");
        var roots = sources.Where(s => !sources.Any(parent => parent.IsDirectory && PathPolicy.IsWithin(parent.SourcePath, s.SourcePath))).ToArray();
        var parentPath = Path.GetDirectoryName(output)!;
        if (!Directory.Exists(parentPath) && roots.Any(r => r.IsDirectory && PathPolicy.IsWithin(r.SourcePath, parentPath)))
            throw new PackValidationException("在源文件夹内部输出时请选择已有目录，或先创建目标子目录后重新添加输入。");
        // 不依赖用户添加顺序：先按规范来源排序，再分配不区分大小写的唯一根名，兼容 Windows 接收端。
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < roots.Length; i++)
        {
            var root = roots[i];
            var candidate = root.EntryName;
            for (var suffix = 1; !used.Add(candidate); suffix++)
                candidate = root.IsDirectory ? $"{root.EntryName} ({suffix})" : $"{Path.GetFileNameWithoutExtension(root.EntryName)} ({suffix}){Path.GetExtension(root.EntryName)}";
            PackPaths.CheckEntry(candidate, root.IsDirectory);
            roots[i] = root with { EntryName = candidate };
        }
        var normalized = new PackRequest(sourcePaths, output, request.Limits);
        var inventory = Enumerate(roots, normalized, null, token, out var excluded);
        var entries = new List<PackEntry>();
        var totalBytes = inventory.Sum(e => e.Length);
        var buffer = new byte[131072];
        long read = 0;
        foreach (var entry in inventory)
        {
            token.ThrowIfCancellationRequested();
            var digest = "";
            if (!entry.IsDirectory)
            {
                await using var input = OpenSource(entry);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                long fileRead = 0;
                int count;
                while ((count = await input.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
                {
                    fileRead += count; read += count;
                    if (fileRead > entry.Length || read > request.Limits.MaxTotalBytes) SourceChanged();
                    hash.AppendData(buffer, 0, count);
                    progress?.Report(new(PackState.Scanning, entry.EntryName, entries.Count, inventory.Count, read, totalBytes));
                }
                if (fileRead != entry.Length) SourceChanged();
                CheckMetadata(entry);
                digest = Convert.ToHexString(hash.GetHashAndReset());
            }
            entries.Add(entry with { Sha256 = digest });
        }
        var plan = new PackPlan(normalized, roots, entries, request.Inputs.Count - roots.Length, excluded);
        VerifyInventory(plan, null, token);
        return plan;
    }

    /// <summary>写入前后核对来源清单与元数据。目录本身的时间不作内容凭据，避免自有临时文件改变目录时间而误判。</summary>
    internal void VerifyInventory(PackPlan plan, string? temporaryFile, CancellationToken token)
    {
        var current = Enumerate(plan.Roots, plan.Request, temporaryFile, token, out _);
        if (current.Count != plan.Entries.Count) SourceChanged();
        for (var i = 0; i < current.Count; i++)
        {
            var a = current[i]; var b = plan.Entries[i];
            if (a.SourcePath != b.SourcePath || a.EntryName != b.EntryName || a.IsDirectory != b.IsDirectory ||
                a.Length != b.Length || (!a.IsDirectory && a.LastWriteUtc != b.LastWriteUtc)) SourceChanged();
        }
    }

    private static List<PackEntry> Enumerate(IReadOnlyList<PackRoot> roots, PackRequest request, string? temporaryFile, CancellationToken token, out int excluded)
    {
        var entries = new List<PackEntry>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<(string Path, string Name, int Depth)>();
        foreach (var root in roots.Reverse()) pending.Push((root.SourcePath, root.EntryName, 0));
        long total = 0; excluded = 0;
        while (pending.TryPop(out var item))
        {
            token.ThrowIfCancellationRequested();
            if (PackPaths.Equal(item.Path, request.OutputPath) || (temporaryFile is not null && PackPaths.Equal(item.Path, temporaryFile))) { excluded++; continue; }
            var directory = PackPaths.IsDirectory(item.Path);
            PackPaths.CheckEntry(item.Name, directory);
            if (!names.Add(item.Name)) throw new PackFailureException(PackError.UnsafePath, "来源包含重复或大小写冲突的包内路径。");
            if (entries.Count >= request.Limits.MaxEntries || item.Depth > request.Limits.MaxDirectoryDepth)
                throw new PackFailureException(PackError.BudgetExceeded, "输入条目数量或目录深度超过创建上限。");
            var info = new FileInfo(item.Path);
            var length = directory ? 0 : info.Length;
            total += length;
            if (length > request.Limits.MaxFileBytes || total > request.Limits.MaxTotalBytes)
                throw new PackFailureException(PackError.BudgetExceeded, "源文件大小超过创建上限。");
            entries.Add(new(item.Path, item.Name, directory, length, directory ? default : info.LastWriteTimeUtc, ""));
            if (directory)
            {
                // 枚举数量也有边界；不先对一个无限目录 ToArray，再事后检查条目数。
                var children = new List<string>();
                foreach (var child in Directory.EnumerateFileSystemEntries(item.Path))
                {
                    token.ThrowIfCancellationRequested();
                    if (children.Count + entries.Count + pending.Count > request.Limits.MaxEntries + 2)
                        throw new PackFailureException(PackError.BudgetExceeded, "目录条目超过创建上限。");
                    children.Add(child);
                }
                foreach (var child in children.OrderByDescending(p => p, PackPaths.Comparer))
                    pending.Push((child, item.Name + "/" + Path.GetFileName(child), item.Depth + 1));
            }
        }
        return entries;
    }

    internal static FileStream OpenSource(PackEntry entry)
    {
        CheckMetadata(entry);
        // Windows 下 FileShare.Read 阻止同时写入／删除当前正在读取的文件；仍以摘要验证准备后的内容变化。
        return new FileStream(entry.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, FileOptions.Asynchronous | FileOptions.SequentialScan);
    }
    internal static void CheckMetadata(PackEntry entry)
    {
        if (PackPaths.IsDirectory(entry.SourcePath) != entry.IsDirectory) SourceChanged();
        if (entry.IsDirectory) return;
        var current = new FileInfo(entry.SourcePath);
        if (current.Length != entry.Length || current.LastWriteTimeUtc != entry.LastWriteUtc) SourceChanged();
    }
    internal static void SourceChanged() => throw new PackFailureException(PackError.InputChanged, "来源在准备后发生变化，请重新添加或检查输入后再开始。");
}

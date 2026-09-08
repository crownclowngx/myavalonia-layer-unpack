using ICSharpCode.SharpZipLib.Zip;
using LayerUnpackPlugin.Headless.Application;
using LayerUnpackPlugin.Headless.Contracts;

namespace LayerUnpackPlugin.Headless.Infrastructure;

/// <summary>ZIP 元数据到业务目录的适配。保留每一条中心目录记录，不因同名而合并文件；只补出缺失父目录。</summary>
internal static class ZipBrowseCatalog
{
    internal static ArchiveCatalog Read(ZipFile zip, string source, string hash, BrowseLimits limits,
        Action<int> progress, CancellationToken token)
    {
        var id = Guid.NewGuid(); var rows = new List<BrowseEntry>();
        var directories = new HashSet<string>(StringComparer.Ordinal);
        var parents = new HashSet<string>(StringComparer.Ordinal);
        var countedAncestors = new HashSet<string>(StringComparer.Ordinal);
        long metadataBytes = 0;
        for (var ordinal = 0; ordinal < zip.Count; ordinal++)
        {
            token.ThrowIfCancellationRequested(); var entry = zip[ordinal];
            var path = entry.Name.Replace('\\', '/').TrimEnd('/');
            if (path.Count(c => c == '/') > 128)
                throw new UnpackFailureException(UnpackError.BudgetExceeded, "条目路径层级超过浏览上限 128。", true);
            UnpackDiagnostic? problem = null;
            try
            {
                path = PathPolicy.NormalizeEntryName(entry.Name, entry.IsDirectory);
                ValidateEntry(entry);
            }
            catch (UnpackFailureException e) { problem = new(e.Code, e.Message); }
            // 不安全路径也需要有限的祖先计数来呈现选择状态，因此其字符串空间同样先计量；不为它创建可选父目录。
            foreach (var ancestor in ArchiveCatalog.Ancestors(path).Skip(1))
                if (countedAncestors.Add(ancestor)) AddMetadata(ancestor);
            if (problem is null)
            {
                if (entry.IsDirectory) directories.Add(path);
                for (var slash = path.LastIndexOf('/'); slash >= 0; slash = path.LastIndexOf('/', slash - 1))
                {
                    parents.Add(path[..slash]);
                    if (parents.Count > limits.MaxRows) TooManyRows();
                    if (slash == 0) break;
                }
            }
            AddMetadata(path);
            rows.Add(new(new(id, ordinal), path, entry.IsDirectory, false, entry.Size < 0 ? null : entry.Size,
                entry.CompressedSize < 0 ? null : entry.CompressedSize, entry.IsCrypted, entry.ExternalFileAttributes, problem, null));
            if (rows.Count > limits.MaxRows) TooManyRows();
            if (ordinal % 256 == 0) progress(ordinal + 1);
        }
        var synthetic = -1;
        foreach (var parent in parents.Except(directories).Order(StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            rows.Add(new(new(id, synthetic--), parent, true, true, null, null, false, 0, null, null));
            if (rows.Count > limits.MaxRows) TooManyRows();
        }
        // 诊断显示与执行政策分离：可以单独选中同名记录之一，但同时提取冲突集合时必须失败，绝不静默丢项。
        var conflicts = rows.GroupBy(e => e.Path, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1)
            .SelectMany(g => g.Select(e => e.Id)).ToHashSet();
        for (var i = 0; i < rows.Count; i++)
            if (conflicts.Contains(rows[i].Id)) rows[i] = rows[i] with { Warning = "存在重复路径、大小写或文件/目录冲突；身份以条目序号区分，冲突集合不可一起提取。" };
        progress((int)zip.Count);
        return new(id, source, hash, rows.OrderBy(e => e.Path, StringComparer.Ordinal).ThenBy(e => e.Id.Ordinal));

        void AddMetadata(string path)
        {
            // 将字符串和行对象的保守估算也纳入目录空间，避免大量深层路径补父目录导致内存放大。
            metadataBytes += path.Length * 2L + 256;
            if (metadataBytes > limits.MaxDirectoryBytes)
                throw new UnpackFailureException(UnpackError.BudgetExceeded, "浏览目录及隐含父目录的内存估算超过元数据预算。", true);
        }
    }

    internal static void ValidateEntry(ZipEntry entry)
    {
        var attributes = entry.ExternalFileAttributes;
        if (ArchiveEntryPolicy.Unsupported(attributes, entry.IsDirectory))
            throw new UnpackFailureException(UnpackError.UnsafePath, "ZIP 条目为链接、特殊对象或类型声明不一致，不允许提取。", true);
        if (!entry.IsDirectory && (!entry.IsCompressionMethodSupported() || (entry.Flags & (1 << 6)) != 0))
            throw new UnpackFailureException(UnpackError.UnsupportedEncryption, "此条目的压缩或强加密方式不在选择提取支持范围。");
    }

    internal static IReadOnlyList<BrowseEntry> ResolveSelection(ArchiveCatalog catalog, BrowseSelection selection)
    {
        if (selection.Entries.Count == 0) throw new UnpackValidationException("Selection", "请先选择文件或目录。");
        var entries = selection.Entries.Distinct().Select(catalog.GetEntry).OrderBy(e => e.Id.Ordinal).ToArray();
        var destinations = new Dictionary<string, (string Path, bool Directory, bool Explicit)>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            if (entry.IsSynthetic) throw new UnpackValidationException("Selection", "请选择目录明确的后代集合，不可直接提交虚拟目录身份。");
            if (entry.Problem is { } problem) throw new UnpackFailureException(problem.Code, problem.Message, true);
            var path = PathPolicy.NormalizeEntryName(entry.Path, entry.IsDirectory);
            Add(path, entry.IsDirectory, true);
            for (var slash = path.LastIndexOf('/'); slash >= 0; slash = path.LastIndexOf('/', slash - 1))
            {
                Add(path[..slash], true, false); if (slash == 0) break;
            }
        }
        return Array.AsReadOnly(entries);

        void Add(string path, bool directory, bool explicitEntry)
        {
            if (destinations.TryGetValue(path, out var previous))
            {
                if (path != previous.Path || !directory || !previous.Directory || (explicitEntry && previous.Explicit))
                    throw new UnpackFailureException(UnpackError.OutputError, "所选集合包含重复路径、大小写或文件/目录冲突；请分别选择冲突条目提取到独立产物。");
                destinations[path] = (path, directory, previous.Explicit || explicitEntry);
            }
            else destinations.Add(path, (path, directory, explicitEntry));
        }
    }
    private static void TooManyRows() => throw new UnpackFailureException(UnpackError.BudgetExceeded, "包含隐含父目录的浏览行数超过预算。", true);
}

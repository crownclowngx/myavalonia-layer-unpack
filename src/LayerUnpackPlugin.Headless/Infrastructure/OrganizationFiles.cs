using System.Security.Cryptography;
using LayerUnpackPlugin.Headless.Contracts;

namespace LayerUnpackPlugin.Headless.Infrastructure;

/// <summary>整理自己的文件验证边界。共用既有路径政策，但向调用方只报告整理诊断。</summary>
internal static class OrganizationFiles
{
    internal static StringComparer Comparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    internal static string Absolute(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new OrganizationFailureException(OrganizationError.UnsafePath, "请提供完整的本地绝对路径。");
        var full = Path.GetFullPath(path);
        Check(full);
        return Path.TrimEndingDirectorySeparator(full);
    }
    internal static void Check(string path)
    {
        try { PathPolicy.EnsureNoLinks(path); }
        catch (UnpackFailureException) { throw new OrganizationFailureException(OrganizationError.UnsafePath, "整理路径包含链接、重解析点或不安全名称。"); }
    }
    internal static string EntryPath(string root, string relative, bool directory)
    {
        try { return PathPolicy.EntryPath(root, relative, directory); }
        catch (UnpackFailureException) { throw new OrganizationFailureException(OrganizationError.UnsafePath, "整理映射包含不安全路径，请检查来源名称。"); }
    }
    internal static void Changed(string path) => throw new OrganizationFailureException(OrganizationError.InputChanged,
        $"来源已改变或消失：{path}。请重新解压并生成整理预览。");

    internal static void ValidateMetadata(OrganizationInput input)
    {
        Check(input.Path);
        if (!File.Exists(input.Path) && !Directory.Exists(input.Path)) Changed(input.Path);
        var attributes = File.GetAttributes(input.Path);
        if ((attributes & FileAttributes.Device) != 0) throw new OrganizationFailureException(OrganizationError.UnsafePath, "不能整理设备文件。");
        if (((attributes & FileAttributes.Directory) != 0) != input.Entry.IsDirectory) Changed(input.Path);
        if (!input.Entry.IsDirectory)
        {
            var info = new FileInfo(input.Path);
            if (info.Length != input.Entry.Length || info.LastWriteTimeUtc != input.Entry.LastWriteUtc || info.CreationTimeUtc != input.Entry.CreationUtc) Changed(input.Path);
        }
        // 递归解压会合法改变父目录修改时间，因此目录通过成员集合验证，不以 mtime 判定变化。
    }

    internal static async Task ValidateAsync(IReadOnlyList<OrganizationInput> inputs, CancellationToken token)
    {
        var children = inputs.GroupBy(i => Path.GetDirectoryName(i.Path)!, Comparer)
            .ToDictionary(g => g.Key, g => g.Select(i => i.Path).ToHashSet(Comparer), Comparer);
        foreach (var input in inputs)
        {
            token.ThrowIfCancellationRequested();
            ValidateMetadata(input);
            if (input.Entry.IsDirectory)
            {
                // 只核对清单中目录的直接成员；一旦出现陌生条目立即拒绝，绝不递归接管新文件。
                var expected = children.GetValueOrDefault(input.Path);
                var count = 0;
                foreach (var path in Directory.EnumerateFileSystemEntries(input.Path))
                {
                    token.ThrowIfCancellationRequested();
                    if (expected is null || !expected.Contains(path)) Changed(input.Path);
                    count++;
                }
                if (count != (expected?.Count ?? 0)) Changed(input.Path);
            }
            else
            {
                await using var stream = OpenRead(input.Path);
                if (stream.Length != input.Entry.Length || Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false)) != input.Entry.Sha256) Changed(input.Path);
                ValidateMetadata(input);
            }
        }
    }

    // 调用方的哈希与复制已有租用缓冲，普通文件句柄不再为每次验证分配 128 KiB 内部数组。
    internal static FileStream OpenRead(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, true);

    internal static bool Occupied(string target)
    {
        Check(target);
        var parent = Path.GetDirectoryName(target)!;
        if (!Directory.Exists(parent)) return File.Exists(parent);
        // 即使运行于大小写敏感系统，也不给同目录生成跨平台冲突名称。
        return Directory.EnumerateFileSystemEntries(parent).Any(p => string.Equals(Path.GetFileName(p), Path.GetFileName(target), StringComparison.OrdinalIgnoreCase));
    }

    internal static OrganizationDiagnostic Diagnostic(Exception exception) => exception switch
    {
        OrganizationFailureException e => new(e.Code, e.Message),
        UnpackFailureException e => new(e.Code == UnpackError.UnsafePath ? OrganizationError.UnsafePath : OrganizationError.OutputError, e.Message),
        FileNotFoundException or DirectoryNotFoundException => new(OrganizationError.InputUnavailable, "来源或目标目录无法访问，请重新检查并预览。"),
        UnauthorizedAccessException or IOException => new(OrganizationError.OutputError, "整理读取、写入或提交失败，请检查权限、磁盘空间和文件占用。"),
        ArgumentException or NotSupportedException => new(OrganizationError.UnsafePath, "整理路径或规则无效，请检查后重新预览。"),
        _ => new(OrganizationError.UnexpectedError, "整理未完成，请检查来源并重新预览。")
    };
}

using LayerUnpackPlugin.Headless.Contracts;

namespace LayerUnpackPlugin.Headless.Infrastructure;

/// <summary>统一 Windows 文件名与包含关系规则；路径验证独立于第三方解压器。</summary>
public static class PathPolicy
{
    public static StringComparison Comparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static bool IsWithin(string root, string path) =>
        Path.GetFullPath(path).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar, Comparison);

    public static string EntryPath(string root, string entry, bool isDirectory)
    {
        var normalized = NormalizeEntryName(entry, isDirectory);
        if (normalized.Length == 0) return Path.GetFullPath(root);
        var full = Path.GetFullPath(Path.Combine(root, normalized.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsWithin(root, full)) Unsafe();
        EnsureNoLinks(full);
        return full;
    }

    /// <summary>只校验和规范化归档内名称，供解压路径与 ZIP 创建共享；此步骤不访问假想输出路径。</summary>
    public static string NormalizeEntryName(string entry, bool isDirectory)
    {
        if (string.IsNullOrEmpty(entry) || Path.IsPathRooted(entry) || entry.StartsWith('/') || entry.StartsWith('\\')) Unsafe();
        if (entry.Contains('\uFFFD'))
            throw new UnpackFailureException(UnpackError.InvalidNameEncoding, "文件名包含无法解码的字符；旧 ZIP 可更换编码，TAR 需要 UTF-8 文件名。");
        var components = entry.Replace('\\', '/').TrimEnd('/').Split('/');
        var normalized = new List<string>();
        foreach (var component in components)
        {
            if (component == ".") continue;
            if (component is ".." or "" || component.Any(c => c < 32 || "<>:\"|?*".Contains(c)) ||
                component.EndsWith('.') || component.EndsWith(' ')) Unsafe();
            var device = component.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
            if (device is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" ||
                (device.Length == 4 && (device.StartsWith("COM") || device.StartsWith("LPT")) && (device[3] is >= '1' and <= '9' or '¹' or '²' or '³'))) Unsafe();
            normalized.Add(component);
        }
        if (normalized.Count == 0)
        {
            if (isDirectory) return "";
            Unsafe();
        }
        return string.Join('/', normalized);
    }

    public static void EnsureNoLinks(string path)
    {
        // 每次写入和提交前检查整个已存在祖先链。拒绝 reparse point，而不是依赖字符串前缀猜测链接目标。
        for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            try { if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) Unsafe(); }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    private static void Unsafe() => throw new UnpackFailureException(UnpackError.UnsafePath, "路径包含不允许的名称、目录逃逸或链接。", true);
}

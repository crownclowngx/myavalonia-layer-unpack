using LayerUnpackPlugin.Headless.Contracts;

namespace LayerUnpackPlugin.Headless.Infrastructure;

/// <summary>复用既有路径政策，将其解压故障翻译为创建领域的诊断；不让创建调用依赖解压会话。</summary>
internal static class PackPaths
{
    internal static StringComparer Comparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    internal static bool Equal(string a, string b) => Comparer.Equals(a, b);
    internal static void Check(string path)
    {
        try { PathPolicy.EnsureNoLinks(path); }
        catch (UnpackFailureException) { throw new PackFailureException(PackError.UnsafePath, "路径包含链接或重解析点，不能创建归档。"); }
    }
    internal static void CheckEntry(string name, bool directory)
    {
        try { PathPolicy.NormalizeEntryName(name, directory); }
        catch (UnpackFailureException) { throw new PackFailureException(PackError.UnsafePath, "文件名称不符合可互操作的 Windows 归档路径规则。"); }
    }
    internal static bool IsDirectory(string path)
    {
        Check(path);
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.Device) != 0)
            throw new PackFailureException(PackError.UnsafePath, "不支持设备或特殊文件。");
        return (attributes & FileAttributes.Directory) != 0;
    }
}

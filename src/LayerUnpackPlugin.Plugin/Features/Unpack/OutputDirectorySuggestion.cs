namespace LayerUnpackPlugin.Features.Unpack;

/// <summary>根据当前输入提出可解释的输出位置，不读写文件系统，也不保存上次选择。</summary>
/// <remarks>
/// 这里只识别“所有包直接位于同一目录”的简单情况。多来源时不取公共祖先，
/// 避免把盘符根或用户不容易注意到的上层目录当成输出。真正的路径与权限验证仍由 Headless 完成。
/// </remarks>
public static class OutputDirectorySuggestion
{
    public static string? ForInputs(IEnumerable<string> paths)
    {
        string? parent = null;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach (var path in paths)
        {
            if (!Path.IsPathFullyQualified(path)) return null;
            var current = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(current)) return null;
            if (parent is not null && !parent.Equals(current, comparison)) return null;
            parent = current;
        }
        return parent is null ? null : Path.Combine(parent, "解压结果");
    }
}

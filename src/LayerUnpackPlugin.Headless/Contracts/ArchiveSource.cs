namespace LayerUnpackPlugin.Headless.Contracts;

/// <summary>
/// 一个逻辑归档的不可变物理来源。单卷与分卷共享节点、深度和事务语义；
/// 这里只保存路径及发现诊断，句柄和密码属于执行操作，不能跟随结果进入 UI 或 JSON。
/// </summary>
public sealed class ArchiveSource
{
    internal ArchiveSource(string primaryPath, IEnumerable<string> members, string? splitKey = null, UnpackDiagnostic? error = null)
    {
        PrimaryPath = primaryPath;
        Members = Array.AsReadOnly(members.ToArray());
        SplitKey = splitKey;
        Error = error;
    }

    public string PrimaryPath { get; }
    public IReadOnlyList<string> Members { get; }
    public bool IsSplit => SplitKey is not null;
    public string DisplayName => Path.GetFileName(SplitKey ?? PrimaryPath);
    public UnpackDiagnostic? Error { get; }
    internal string? SplitKey { get; }
}

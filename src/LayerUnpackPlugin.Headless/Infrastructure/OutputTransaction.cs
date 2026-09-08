using LayerUnpackPlugin.Headless.Contracts;

namespace LayerUnpackPlugin.Headless.Infrastructure;

/// <summary>单包输出事务：先写私有临时目录，再以不覆盖的目录移动提交；不回滚已经成功的其他包。</summary>
public sealed class OutputTransaction
{
    private readonly string _parent;
    private bool _committed;
    public string StagingDirectory { get; }

    public OutputTransaction(string parent)
    {
        _parent = Path.GetFullPath(parent);
        PathPolicy.EnsureNoLinks(_parent);
        Directory.CreateDirectory(_parent);
        StagingDirectory = Path.Combine(_parent, ".layer-unpack-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(StagingDirectory);
        PathPolicy.EnsureNoLinks(StagingDirectory);
    }

    public string Commit(string name, CancellationToken cancellationToken)
    {
        if (_committed) throw new InvalidOperationException("输出事务已经提交。");
        if (string.IsNullOrWhiteSpace(name)) name = "解压结果";
        if (name.Length > 120) name = name[..120];
        for (var suffix = 0; suffix < 10_000; suffix++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = PathPolicy.EntryPath(_parent, suffix == 0 ? name : $"{name} ({suffix})", true);
            if (File.Exists(target) || Directory.Exists(target)) continue;
            try
            {
                // Directory.Move 是最终仲裁：两个批次即使同时看见目标不存在，也只有一个可以提交。
                PathPolicy.EnsureNoLinks(StagingDirectory);
                Directory.Move(StagingDirectory, target);
                _committed = true;
                return target;
            }
            catch (IOException) when (Directory.Exists(target) || File.Exists(target)) { }
        }
        throw new UnpackFailureException(UnpackError.OutputError, "无法分配独立输出目录。");
    }

    public string? Rollback()
    {
        if (_committed || !Directory.Exists(StagingDirectory)) return null;
        try
        {
            if (!PathPolicy.IsWithin(_parent, StagingDirectory)) throw new IOException();
            DeleteOwnedDirectory(StagingDirectory);
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or UnpackFailureException)
        {
            // 不抛出第二个错误覆盖原始失败。仅返回本事务拥有的残留路径，留给用户检查。
            return StagingDirectory;
        }
    }

    /// <summary>整理预览已经约定精确名称，提交时不再自动编号。目标被占用必须重新预览，
    /// 以保证用户检查的映射就是最终映射；Directory.Move 仍负责最后的不覆盖仲裁。</summary>
    public string CommitExact(string name, CancellationToken cancellationToken)
    {
        if (_committed) throw new InvalidOperationException("输出事务已经提交。");
        cancellationToken.ThrowIfCancellationRequested();
        var target = PathPolicy.EntryPath(_parent, name, true);
        PathPolicy.EnsureNoLinks(StagingDirectory);
        Directory.Move(StagingDirectory, target);
        _committed = true;
        return target;
    }

    private static void DeleteOwnedDirectory(string path)
    {
        PathPolicy.EnsureNoLinks(path);
        foreach (var entry in Directory.EnumerateFileSystemEntries(path))
        {
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                // 删除链接本身，不遍历目标；临时目录之外的用户内容永远不属于本事务。
                if ((attributes & FileAttributes.Directory) != 0) Directory.Delete(entry);
                else File.Delete(entry);
            }
            else if ((attributes & FileAttributes.Directory) != 0) DeleteOwnedDirectory(entry);
            else File.Delete(entry);
        }
        Directory.Delete(path);
    }
}

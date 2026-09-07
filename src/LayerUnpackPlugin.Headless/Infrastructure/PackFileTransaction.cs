using LayerUnpackPlugin.Headless.Contracts;

namespace LayerUnpackPlugin.Headless.Infrastructure;

/// <summary>只拥有一个随机临时文件；创建采用排他模式，最终 File.Move 的不覆盖语义仲裁并发命名。</summary>
internal sealed class PackFileTransaction
{
    private readonly string _outputPath;
    private bool _owned;
    private bool _committed;
    internal string TemporaryPath { get; }

    internal PackFileTransaction(string outputPath)
    {
        _outputPath = outputPath;
        TemporaryPath = Path.Combine(Path.GetDirectoryName(outputPath)!, ".layer-pack-" + Guid.NewGuid().ToString("N") + ".tmp");
    }
    internal FileStream Open()
    {
        var parent = Path.GetDirectoryName(_outputPath)!;
        PackPaths.Check(parent);
        Directory.CreateDirectory(parent);
        PackPaths.Check(TemporaryPath);
        var stream = new FileStream(TemporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, FileOptions.Asynchronous);
        _owned = true;
        return stream;
    }
    internal string Commit(CancellationToken token)
    {
        if (!_owned || _committed) throw new InvalidOperationException("文件事务状态无效。");
        var parent = Path.GetDirectoryName(_outputPath)!;
        var stem = Path.GetFileNameWithoutExtension(_outputPath);
        for (var i = 0; i < 10_000; i++)
        {
            token.ThrowIfCancellationRequested();
            var target = i == 0 ? _outputPath : Path.Combine(parent, $"{stem} ({i}).zip");
            PackPaths.Check(TemporaryPath); PackPaths.Check(target);
            if (File.Exists(target) || Directory.Exists(target)) continue;
            try
            {
                File.Move(TemporaryPath, target, overwrite: false);
                _committed = true;
                return target;
            }
            catch (IOException) when (File.Exists(target) || Directory.Exists(target)) { }
        }
        throw new PackFailureException(PackError.OutputError, "无法为 ZIP 分配不冲突的输出名称。");
    }
    internal string? Rollback()
    {
        if (!_owned || _committed) return null;
        try { PackPaths.Check(Path.GetDirectoryName(TemporaryPath)!); File.Delete(TemporaryPath); return null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PackFailureException)
        { return TemporaryPath; }
    }
}

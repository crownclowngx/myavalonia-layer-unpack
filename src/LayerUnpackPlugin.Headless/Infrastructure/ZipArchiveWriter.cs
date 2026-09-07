using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using LayerUnpackPlugin.Headless.Application;
using LayerUnpackPlugin.Headless.Contracts;

namespace LayerUnpackPlugin.Headless.Infrastructure;

/// <summary>使用 .NET 10 的异步 ZIP 写入。逐文件验证准备摘要；UTF-8 名称与普通 Deflate 构成 R02 的明确格式范围。</summary>
public sealed class ZipArchiveWriter : IArchiveWriter
{
    public async Task WriteAsync(PackPlan plan, Stream output, IProgress<PackProgress>? progress, CancellationToken cancellationToken)
    {
        await using var zip = await ZipArchive.CreateAsync(output, ZipArchiveMode.Create, true, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        var buffer = new byte[131072];
        long read = 0;
        var total = plan.TotalBytes;
        var done = 0;
        foreach (var source in plan.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = zip.CreateEntry(source.EntryName + (source.IsDirectory ? "/" : ""), CompressionLevel.Optimal);
            // ZIP 的 DOS 时间只支持 1980–2107、精度两秒；超界采用端点，不能声称保留所有文件系统元数据。
            if (!source.IsDirectory)
            {
                var time = source.LastWriteUtc.ToLocalTime();
                entry.LastWriteTime = new DateTimeOffset(time.Year < 1980 ? new DateTime(1980, 1, 1) : time.Year > 2107 ? new DateTime(2107, 12, 31, 23, 59, 58) : time);
                await using var input = Open(source);
                await using var destination = await entry.OpenAsync(cancellationToken).ConfigureAwait(false);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                long length = 0;
                while (true)
                {
                    int count;
                    try { count = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false); }
                    catch (IOException) { throw new PackFailureException(PackError.InputUnavailable, "无法读取源文件，请检查占用、权限和存储设备。"); }
                    if (count == 0) break;
                    length += count; read += count;
                    if (length > source.Length || read > plan.Request.Limits.MaxTotalBytes) PackPlanner.SourceChanged();
                    hash.AppendData(buffer, 0, count);
                    await destination.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                    progress?.Report(new(PackState.Writing, source.EntryName, done, plan.Entries.Count, read, total));
                }
                if (length != source.Length || Convert.ToHexString(hash.GetHashAndReset()) != source.Sha256) PackPlanner.SourceChanged();
                PackPlanner.CheckMetadata(source);
            }
            progress?.Report(new(PackState.Writing, source.EntryName, ++done, plan.Entries.Count, read, total));
        }
        // DisposeAsync 写完中央目录才算归档收尾；输出包装流仍检查取消和预算，外层尚未提交文件。
        progress?.Report(new(PackState.Finalizing, null, done, plan.Entries.Count, read, total));
    }

    private static FileStream Open(PackEntry source)
    {
        try { return PackPlanner.OpenSource(source); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { throw new PackFailureException(PackError.InputUnavailable, "源文件不可用，请检查路径、权限或占用后重新准备。"); }
    }
}

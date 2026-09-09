using System.IO.Compression;
using System.Text;
using LayerUnpackPlugin.Headless.Application;
using LayerUnpackPlugin.Headless.Contracts;

namespace LayerUnpackPlugin.Headless.Infrastructure;

/// <summary>使用 .NET 10 的异步 ZIP 写入。逐文件验证准备摘要；UTF-8 名称与普通 Deflate 构成 R02 的明确格式范围。</summary>
public sealed class ZipArchiveWriter : IArchiveWriter
{
    public async Task WriteAsync(PackPlan plan, Stream output, IProgress<PackProgress>? progress, CancellationToken cancellationToken, PackSecret? secret = null)
    {
        if (plan.Request.Options.Format != PackFormat.Zip) throw new PackValidationException("ZIP 写入器只能接收 ZIP 清单。");
        if (plan.Request.Options.Encrypt)
        {
            if (secret is null) throw new PackValidationException("本次加密输出缺少目标密码。");
            await AesZipArchiveWriter.WriteAsync(plan, output, secret, progress, cancellationToken).ConfigureAwait(false);
            return;
        }
        var level = plan.Request.Options.Compression switch
        {
            PackCompression.Fast => CompressionLevel.Fastest,
            PackCompression.High => CompressionLevel.SmallestSize,
            PackCompression.Store => CompressionLevel.NoCompression,
            _ => CompressionLevel.Optimal
        };
        await using var zip = await ZipArchive.CreateAsync(output, ZipArchiveMode.Create, true, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        var buffer = new byte[131072];
        long read = 0;
        var total = plan.TotalBytes;
        var done = 0;
        foreach (var source in plan.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = zip.CreateEntry(source.EntryName + (source.IsDirectory ? "/" : ""), level);
            // ZIP 的 DOS 时间只支持 1980–2107、精度两秒；超界采用端点，不能声称保留所有文件系统元数据。
            if (!source.IsDirectory)
            {
                entry.LastWriteTime = new DateTimeOffset(PackEntryCopier.ZipTime(source.LastWriteUtc));
                await using var destination = await entry.OpenAsync(cancellationToken).ConfigureAwait(false);
                await PackEntryCopier.CopyAsync(source, destination, buffer, count =>
                {
                    read += count;
                    progress?.Report(new(PackState.Writing, source.EntryName, done, plan.Entries.Count, read, total));
                }, cancellationToken).ConfigureAwait(false);
            }
            progress?.Report(new(PackState.Writing, source.EntryName, ++done, plan.Entries.Count, read, total));
        }
        // DisposeAsync 写完中央目录才算归档收尾；输出包装流仍检查取消和预算，外层尚未提交文件。
        progress?.Report(new(PackState.Finalizing, null, done, plan.Entries.Count, read, total));
    }

}

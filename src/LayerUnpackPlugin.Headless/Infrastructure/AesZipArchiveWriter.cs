using ICSharpCode.SharpZipLib.Zip;
using LayerUnpackPlugin.Headless.Contracts;

namespace LayerUnpackPlugin.Headless.Infrastructure;

/// <summary>
/// ZIP AES-256 适配只消费单包清单。每个文件（包括零字节文件）使用 AE-2 认证加密；目录和名称仍可见。
/// 不使用解压候选池，不保存密码，不吞掉收尾错误；完整中央目录写完后才交还外层事务提交。
/// </summary>
internal static class AesZipArchiveWriter
{
    internal static async Task WriteAsync(PackPlan plan, Stream output, PackSecret secret, IProgress<PackProgress>? progress, CancellationToken token)
    {
        var zip = new ZipOutputStream(output) { IsStreamOwner = false, NameTransform = null, UseZip64 = UseZip64.Dynamic };
        try
        {
            zip.SetLevel(plan.Request.Options.Compression switch { PackCompression.Fast => 1, PackCompression.High => 9, PackCompression.Store => 0, _ => 6 });
            var buffer = new byte[131072];
            long read = 0;
            var done = 0;
            foreach (var source in plan.Entries)
            {
                token.ThrowIfCancellationRequested();
                zip.Password = source.IsDirectory ? null : secret.Password;
                var entry = new ZipEntry(source.EntryName + (source.IsDirectory ? "/" : ""))
                {
                    Size = source.Length,
                    IsUnicodeText = true,
                    AESKeySize = source.IsDirectory ? 0 : 256,
                    // AES 的“仅打包”使用 Deflate 0 的不压缩块。SharpCompress 0.50.4 读取非空 Stored AES 时
                    // 会把额外 18 字节当成内容；此映射保持不压缩语义，同时通过独立读取器的完整长度与摘要验证。
                    CompressionMethod = source.IsDirectory ? CompressionMethod.Stored : CompressionMethod.Deflated,
                    DateTime = source.IsDirectory ? new DateTime(1980, 1, 1) : PackEntryCopier.ZipTime(source.LastWriteUtc)
                };
                await zip.PutNextEntryAsync(entry, token).ConfigureAwait(false);
                if (!source.IsDirectory)
                    await PackEntryCopier.CopyAsync(source, zip, buffer, count =>
                    {
                        read += count;
                        progress?.Report(new(PackState.Writing, source.EntryName, done, plan.Entries.Count, read, plan.TotalBytes));
                    }, token).ConfigureAwait(false);
                await zip.CloseEntryAsync(token).ConfigureAwait(false);
                progress?.Report(new(PackState.Writing, source.EntryName, ++done, plan.Entries.Count, read, plan.TotalBytes));
            }
            progress?.Report(new(PackState.Finalizing, null, done, plan.Entries.Count, read, plan.TotalBytes));
            await zip.FinishAsync(token).ConfigureAwait(false);
        }
        catch
        {
            // 写头失败时，库的 Dispose 会再次尝试收尾，可能抛出缺少 AES 状态的次生异常。
            // 保留首次写入／预算／来源故障，外层事务仍负责关闭文件及删除暂存；清理不能把它改成未知错误。
            try { zip.Dispose(); } catch (Exception) { }
            throw;
        }
        zip.Dispose();
    }
}

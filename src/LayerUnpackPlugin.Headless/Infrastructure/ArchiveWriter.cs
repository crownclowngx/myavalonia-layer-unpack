using System.Formats.Tar;
using System.IO.Compression;
using LayerUnpackPlugin.Headless.Application;
using LayerUnpackPlugin.Headless.Contracts;

namespace LayerUnpackPlugin.Headless.Infrastructure;

/// <summary>按有限能力表选择已有写入适配器。格式选择不影响上层清单、预算、取消和不覆盖事务，
/// 不引入可动态注册的引擎框架；ZIP 仍使用原实现，保证默认操作和 AES 行为一致。</summary>
public sealed class ArchiveWriter : IArchiveWriter
{
    public async Task WriteAsync(PackPlan plan, Stream output, IProgress<PackProgress>? progress, CancellationToken cancellationToken, PackSecret? secret = null)
    {
        plan.Request.Options.Validate();
        if (plan.Request.Options.Format == PackFormat.Zip)
        {
            await new ZipArchiveWriter().WriteAsync(plan, output, progress, cancellationToken, secret).ConfigureAwait(false);
            return;
        }
        if (secret is not null) throw new PackValidationException("此格式不支持目标密码。");
        await WriteTarAsync(plan, output, progress, cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteTarAsync(PackPlan plan, Stream output, IProgress<PackProgress>? progress, CancellationToken token)
    {
        var level = plan.Request.Options.Compression switch
        { PackCompression.Fast => CompressionLevel.Fastest, PackCompression.High => CompressionLevel.SmallestSize, _ => CompressionLevel.Optimal };
        await using var gzip = plan.Request.Options.Format == PackFormat.TarGZip ? new GZipStream(output, level, true) : null;
        await using var writer = new TarWriter(gzip ?? output, TarEntryFormat.Pax, leaveOpen: true);
        long read = 0; var done = 0;
        foreach (var entry in plan.Entries)
        {
            token.ThrowIfCancellationRequested();
            var tar = new PaxTarEntry(entry.IsDirectory ? TarEntryType.Directory : TarEntryType.RegularFile, entry.EntryName)
            { ModificationTime = entry.IsDirectory ? DateTimeOffset.UnixEpoch : new DateTimeOffset(entry.LastWriteUtc, TimeSpan.Zero) };
            using var input = entry.IsDirectory ? null : new VerifiedPackReadStream(entry, n =>
            { read += n; progress?.Report(new(PackState.Writing, entry.EntryName, done, plan.Entries.Count, read, plan.TotalBytes)); }, token);
            if (input is not null) tar.DataStream = input;
            await writer.WriteEntryAsync(tar, token).ConfigureAwait(false);
            input?.Verify();
            progress?.Report(new(PackState.Writing, entry.EntryName, ++done, plan.Entries.Count, read, plan.TotalBytes));
        }
        progress?.Report(new(PackState.Finalizing, null, done, plan.Entries.Count, read, plan.TotalBytes));
    }

}

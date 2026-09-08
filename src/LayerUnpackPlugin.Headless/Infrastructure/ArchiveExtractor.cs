using System.IO.Compression;
using System.Formats.Tar;
using System.Text;
using LayerUnpackPlugin.Headless.Application;
using LayerUnpackPlugin.Headless.Contracts;
using LayerUnpackPlugin.Headless.Domain;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Common.Rar;
using SharpCompress.Compressors.BZip2;
using SharpCompress.Compressors.Xz;
using SharpCompress.Readers;

namespace LayerUnpackPlugin.Headless.Infrastructure;

/// <summary>按已验证的格式选择解码器，只解一个逻辑包。逐条目检查路径、链接和大小，绝不调用自动落盘 API。</summary>
public sealed class ArchiveExtractor : IArchiveExtractor
{
    public async Task<ExtractedArchive> ExtractAsync(string source, string destination, string? password, LegacyNameEncoding legacyNameEncoding,
        ExecutionBudget budget, Action<long> progress, CancellationToken cancellationToken)
    {
        var encrypted = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            PathPolicy.EnsureNoLinks(source);
            PathPolicy.EnsureNoLinks(destination);
            if (new FileInfo(source).Length > budget.Limits.MaxInputBytes)
                throw new UnpackFailureException(UnpackError.BudgetExceeded, "输入文件超过单包读取上限。", true);
            var kind = await ArchiveProbe.DetectAsync(source, cancellationToken).ConfigureAwait(false);
            if (kind == ArchiveKind.Unknown)
                throw new UnpackFailureException(UnpackError.UnsupportedFormat, "无法识别此格式，或压缩包头已损坏。");
            if (kind == ArchiveKind.Zip)
                return await ZipArchiveExtractor.ExtractAsync(source, destination, password, legacyNameEncoding, budget, progress, cancellationToken).ConfigureAwait(false);
            if (kind is ArchiveKind.GZip or ArchiveKind.BZip2 or ArchiveKind.Xz)
                return await ExtractCompressionStreamAsync(source, destination, kind, budget, progress, cancellationToken).ConfigureAwait(false);
            if (kind == ArchiveKind.Tar)
                return await ExtractTarAsync(source, destination, budget, progress, cancellationToken).ConfigureAwait(false);

            // 使用单一外部文件流，禁止引擎通过文件名自动发现并打开额外卷。
            await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true);
            var isRar5 = false;
            if (kind == ArchiveKind.Rar) { input.Position = 6; isRar5 = input.ReadByte() == 1; input.Position = 0; }
            var options = new ReaderOptions
            {
                Password = password,
                LeaveStreamOpen = true,
                LookForHeader = false,
                ArchiveEncoding = new ArchiveEncoding { Default = new UTF8Encoding(false, true) }
            };
            await using var reader = await EntryCursor.CreateAsync(input, options, kind, cancellationToken).ConfigureAwait(false);
            var files = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                while (await reader.MoveToNextEntryAsync(cancellationToken).ConfigureAwait(false))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    budget.AddEntry();
                    var entry = reader.Entry;
                    encrypted |= entry.IsEncrypted;
                    if (entry.IsSplitAfter || entry.VolumeIndexFirst != entry.VolumeIndexLast || entry is IArchiveEntry { IsComplete: false })
                        throw new UnpackFailureException(UnpackError.MissingVolume, "首版不支持分卷包，请提供完整的单文件压缩包。");
                    // TAR 没有实现 Attrib；RAR5 的重定向也不会出现在 LinkTarget 中，必须检查专用属性。
                    if (entry.LinkTarget is not null || entry is RarEntry { IsRedir: true } ||
                        (kind != ArchiveKind.Tar && ArchiveEntryPolicy.Unsupported(entry.Attrib, entry.IsDirectory)))
                        throw new UnpackFailureException(UnpackError.UnsafePath, "压缩包包含链接、特殊对象或不一致的条目类型，已停止写入。", true);
                    var target = PathPolicy.EntryPath(destination, entry.Key ?? "", entry.IsDirectory);
                    if (target.Equals(Path.GetFullPath(destination), PathPolicy.Comparison) && entry.IsDirectory) continue;
                    if (!seen.Add(target)) throw new UnpackFailureException(UnpackError.OutputError, "压缩包包含重复或大小写冲突的路径。");
                    if (entry.IsDirectory) { Directory.CreateDirectory(target); continue; }
                    if (entry.IsEncrypted && password is null) PasswordFailure();
                    if (entry.Size > budget.Limits.MaxFileBytes)
                        throw new UnpackFailureException(UnpackError.BudgetExceeded, "条目声明的大小超过单文件预算。", true);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    PathPolicy.EnsureNoLinks(target);
                    await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true);
                    await using var entryStream = await reader.OpenEntryStreamAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        var copied = await StreamCopy.CopyAsync(entryStream, output, budget, progress, cancellationToken).ConfigureAwait(false);
                        if ((entry.Size > 0 && copied.Length != entry.Size) ||
                            (!(isRar5 && entry.IsEncrypted) && copied.Crc != (uint)entry.Crc))
                        {
                            if (entry.IsEncrypted) PasswordFailure();
                            throw new UnpackFailureException(UnpackError.CorruptArchive, "条目长度或校验和不匹配，文件可能损坏。");
                        }
                    }
                    catch
                    {
                        // EntryStream 默认在 Dispose 时继续跳过剩余数据。先取消 reader，避免取消/预算失败后仍解完整个大文件。
                        reader.Cancel();
                        throw;
                    }
                    files.Add(Path.GetRelativePath(destination, target));
                }
            }
            catch { reader.Cancel(); throw; }
            // 上游 0.50.4 对加密 RAR 关闭 CRC；RAR4 由本适配器补验，RAR5 的 MAC 不可当作普通 CRC。
            // 保留已验证的解码能力，同时把实际校验限制随节点呈现；不自行实现密码学或声称完整性已验证。
            return new ExtractedArchive(isRar5 ? "Rar5" : kind.ToString(), files.AsReadOnly(), password is not null,
                isRar5 && encrypted ? "RAR5 加密内容已解码，但当前引擎未验证加密内容的校验值；请保留原包。" : null);
        }
        catch (UnpackFailureException) { throw; }
        catch (Exception) when (cancellationToken.IsCancellationRequested) { throw new OperationCanceledException(cancellationToken); }
        catch (SharpCompress.Common.CryptographicException) { PasswordFailure(); throw; }
        catch (System.Security.Cryptography.CryptographicException) { PasswordFailure(); throw; }
        catch (Exception e) when (e is IncompleteArchiveException or MultipartStreamRequiredException or MultiVolumeExtractionException)
        { throw new UnpackFailureException(UnpackError.MissingVolume, "压缩包不完整或需要其他分卷。"); }
        catch (NotSupportedException)
        { throw new UnpackFailureException(UnpackError.UnsupportedEncryption, "当前引擎不支持此压缩或加密变体。"); }
        catch (DecoderFallbackException)
        { throw new UnpackFailureException(UnpackError.InvalidNameEncoding, "文件名不符合所选编码，请调整旧 ZIP 编码后创建新批次。"); }
        catch (Exception e) when (e is SharpCompressException or InvalidDataException or EndOfStreamException or IndexOutOfRangeException or ArgumentException)
        {
            // 加密头解码失败也可能发生在取得 Entry 之前。为所有已提供候选的失败保留“密码或损坏”的歧义。
            if (encrypted || password is not null) PasswordFailure();
            throw new UnpackFailureException(UnpackError.CorruptArchive, "无法完整读取压缩包，文件可能损坏或密码不可用。");
        }
    }

    /// <summary>TAR 使用运行时提供的读取器，能明确区分普通文件、目录和链接/设备等特殊条目。</summary>
    private static async Task<ExtractedArchive> ExtractTarAsync(string source, string destination, ExecutionBudget budget,
        Action<long> progress, CancellationToken token)
    {
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true);
        await using var reader = new TarReader(input, leaveOpen: true);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = new List<string>();
        while (await reader.GetNextEntryAsync(copyData: false, cancellationToken: token).ConfigureAwait(false) is { } entry)
        {
            token.ThrowIfCancellationRequested();
            budget.AddEntry();
            var directory = entry.EntryType == TarEntryType.Directory;
            if (!directory && entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile or TarEntryType.ContiguousFile))
                throw new UnpackFailureException(UnpackError.UnsafePath, "TAR 包含链接或特殊设备条目，已停止写入。", true);
            var target = PathPolicy.EntryPath(destination, entry.Name, directory);
            if (target.Equals(Path.GetFullPath(destination), PathPolicy.Comparison) && directory) continue;
            if (!seen.Add(target)) throw new UnpackFailureException(UnpackError.OutputError, "TAR 包含重复或大小写冲突的路径。");
            if (directory) { Directory.CreateDirectory(target); continue; }
            if (entry.Length > budget.Limits.MaxFileBytes)
                throw new UnpackFailureException(UnpackError.BudgetExceeded, "TAR 条目大小超过单文件预算。", true);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            PathPolicy.EnsureNoLinks(target);
            await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true);
            var copied = await StreamCopy.CopyAsync(entry.DataStream ?? Stream.Null, output, budget, progress, token).ConfigureAwait(false);
            if (copied.Length != entry.Length) throw new UnpackFailureException(UnpackError.CorruptArchive, "TAR 条目内容不完整。");
            files.Add(Path.GetRelativePath(destination, target));
        }
        return new ExtractedArchive("Tar", files.AsReadOnly(), false);
    }

    private async Task<ExtractedArchive> ExtractCompressionStreamAsync(string source, string destination, ArchiveKind kind,
        ExecutionBudget budget, Action<long> progress, CancellationToken cancellationToken)
    {
        // 先将压缩流展开到独立中间文件；它位于单包临时目录内，失败时由同一事务回滚。
        var isTarName = source.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) ||
            source.EndsWith(".tar.bz2", StringComparison.OrdinalIgnoreCase) || source.EndsWith(".tar.xz", StringComparison.OrdinalIgnoreCase) ||
            source.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase);
        var intermediate = Path.Combine(destination, ".stream-" + Guid.NewGuid().ToString("N") + (isTarName ? ".tar" : ""));
        await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true))
        await using (Stream decoded = kind switch
        {
            ArchiveKind.GZip => new GZipStream(input, CompressionMode.Decompress, true),
            ArchiveKind.BZip2 => BZip2Stream.Create(input, SharpCompress.Compressors.CompressionMode.Decompress, true, true),
            ArchiveKind.Xz => new XZStream(input),
            _ => throw new InvalidOperationException()
        })
        await using (var output = new FileStream(intermediate, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true))
        {
            budget.AddEntry();
            await StreamCopy.CopyAsync(decoded, output, budget, progress, cancellationToken).ConfigureAwait(false);
        }
        if (await ArchiveProbe.DetectAsync(intermediate, cancellationToken).ConfigureAwait(false) == ArchiveKind.Tar)
        {
            var tarResult = await ExtractAsync(intermediate, destination, null, LegacyNameEncoding.Utf8, budget, progress, cancellationToken).ConfigureAwait(false);
            File.Delete(intermediate);
            return tarResult with { Format = "Tar+" + kind };
        }
        var name = Path.GetFileNameWithoutExtension(source);
        if (string.IsNullOrWhiteSpace(name)) name = "内容";
        var target = PathPolicy.EntryPath(destination, name, false);
        File.Move(intermediate, target);
        return new ExtractedArchive(kind.ToString(), Array.AsReadOnly(new[] { name }), false);
    }

    private static void PasswordFailure() => throw new UnpackFailureException(UnpackError.PasswordRequiredOrInvalid,
        "没有可用密码，或加密内容已经损坏；可以补充候选密码后重试。");

    /// <summary>7z 与 RAR 保持适合固实数据的顺序读取。</summary>
    private sealed class EntryCursor : IAsyncDisposable
    {
        private readonly IAsyncReader? _reader;
        private readonly IAsyncArchive? _archive;
        private EntryCursor(IAsyncReader reader, IAsyncArchive? archive = null) { _reader = reader; _archive = archive; }
        public IEntry Entry => _reader!.Entry;

        public static async Task<EntryCursor> CreateAsync(Stream input, ReaderOptions options, ArchiveKind kind, CancellationToken token)
        {
            if (kind != ArchiveKind.SevenZip)
                return new EntryCursor(await ReaderFactory.OpenAsyncReader(input, options, token).ConfigureAwait(false));
            var archive = await ArchiveFactory.OpenAsyncArchive(input, options, token).ConfigureAwait(false);
            try { return new EntryCursor(await archive.ExtractAllEntriesAsync().ConfigureAwait(false), archive); }
            catch { await archive.DisposeAsync().ConfigureAwait(false); throw; }
        }
        public async ValueTask<bool> MoveToNextEntryAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return await _reader!.MoveToNextEntryAsync(token).ConfigureAwait(false);
        }
        public async ValueTask<Stream> OpenEntryStreamAsync(CancellationToken token) =>
            await _reader!.OpenEntryStreamAsync(token).ConfigureAwait(false);
        public void Cancel() => _reader?.Cancel();
        public async ValueTask DisposeAsync()
        {
            if (_reader is not null) await _reader.DisposeAsync().ConfigureAwait(false);
            if (_archive is not null) await _archive.DisposeAsync().ConfigureAwait(false);
        }
    }
}

using System.IO.Compression;
using System.Buffers.Binary;
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
    /// <summary>保留单路径便利入口，但所有调用都经过同一分卷解析规则。</summary>
    public Task<ExtractedArchive> ExtractAsync(string source, string destination, string? password, LegacyNameEncoding legacyNameEncoding,
        ExecutionBudget budget, Action<long> progress, CancellationToken cancellationToken) =>
        ExtractAsync(ArchiveSourceResolver.ResolveInputs([source], budget.Limits, cancellationToken).Single(), destination,
            password, legacyNameEncoding, budget, progress, cancellationToken);

    public async Task<ExtractedArchive> ExtractAsync(ArchiveSource logicalSource, string destination, string? password, LegacyNameEncoding legacyNameEncoding,
        ExecutionBudget budget, Action<long> progress, CancellationToken cancellationToken)
    {
        var source = logicalSource.PrimaryPath;
        var encrypted = false;
        var possibleSplit = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            PathPolicy.EnsureNoLinks(source);
            PathPolicy.EnsureNoLinks(destination);
            await using var lease = await ArchiveSourceReader.OpenAsync(logicalSource, budget.Limits, cancellationToken).ConfigureAwait(false);
            var kind = await lease.ProbeAsync(cancellationToken).ConfigureAwait(false);
            possibleSplit = logicalSource.IsSplit || (kind == ArchiveKind.SevenZip && Path.GetExtension(source) is { Length: 4 } extension && extension[1..].All(char.IsDigit));
            if (logicalSource.IsSplit && kind != ArchiveKind.SevenZip)
                throw new UnpackFailureException(UnpackError.UnsupportedFormat, "标准 .7z 数字卷的逻辑签名不是 7z，不能按其他格式解码。");
            if (kind == ArchiveKind.Unknown)
                throw new UnpackFailureException(UnpackError.UnsupportedFormat, "无法识别此格式，或压缩包头已损坏。");
            if (kind == ArchiveKind.Zip)
                return await ZipArchiveExtractor.ExtractAsync(source, destination, password, legacyNameEncoding, budget, progress, cancellationToken).ConfigureAwait(false);
            if (kind is ArchiveKind.GZip or ArchiveKind.BZip2 or ArchiveKind.Xz)
                return await ExtractCompressionStreamAsync(source, destination, kind, budget, progress, cancellationToken).ConfigureAwait(false);
            if (kind == ArchiveKind.Tar)
                return await ExtractTarAsync(source, destination, budget, progress, cancellationToken).ConfigureAwait(false);

            // 只交付已审计的有序流；解码器拿不到目录发现权限，也不拥有底层句柄。
            var input = lease.Streams[0];
            var isRar5 = false;
            if (kind == ArchiveKind.Rar) { input.Position = 6; isRar5 = input.ReadByte() == 1; input.Position = 0; }
            var options = new ReaderOptions
            {
                Password = password,
                LeaveStreamOpen = true,
                LookForHeader = false,
                ArchiveEncoding = new ArchiveEncoding { Default = new UTF8Encoding(false, true) }
            };
            await using var reader = await EntryCursor.CreateAsync(lease.Streams, options, kind, cancellationToken).ConfigureAwait(false);
            var files = new List<string>();
            var crcFiles = 0; var lengthFiles = 0;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                while (await reader.MoveToNextEntryAsync(cancellationToken).ConfigureAwait(false))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    budget.AddEntry();
                    var entry = reader.Entry;
                    // SharpCompress 0.50.4 的 7z 空文件没有 Folder，但可空 FindIndex != -1 会误报加密。
                    // 空正文不需要密码；加密头仍由打开容器时验证，有正文的条目仍严格检查密码与 CRC。
                    var entryEncrypted = entry.IsEncrypted && !(kind == ArchiveKind.SevenZip && entry.Size == 0);
                    encrypted |= entryEncrypted;
                    if (entry.IsSplitAfter || entry.VolumeIndexFirst != entry.VolumeIndexLast || entry is IArchiveEntry { IsComplete: false })
                        throw new UnpackFailureException(UnpackError.MissingVolume, "此格式的多卷结构尚不支持，或所需卷不完整。");
                    // TAR 没有实现 Attrib；RAR5 的重定向也不会出现在 LinkTarget 中，必须检查专用属性。
                    if (entry.LinkTarget is not null || entry is RarEntry { IsRedir: true } ||
                        (kind != ArchiveKind.Tar && ArchiveEntryPolicy.Unsupported(entry.Attrib, entry.IsDirectory)))
                        throw new UnpackFailureException(UnpackError.UnsafePath, "压缩包包含链接、特殊对象或不一致的条目类型，已停止写入。", true);
                    var target = PathPolicy.EntryPath(destination, entry.Key ?? "", entry.IsDirectory);
                    if (target.Equals(Path.GetFullPath(destination), PathPolicy.Comparison) && entry.IsDirectory) continue;
                    if (!seen.Add(target)) throw new UnpackFailureException(UnpackError.OutputError, "压缩包包含重复或大小写冲突的路径。");
                    if (entry.IsDirectory) { Directory.CreateDirectory(target); continue; }
                    if (entryEncrypted && password is null) PasswordFailure();
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
                            (!(isRar5 && entryEncrypted) && copied.Crc != (uint)entry.Crc))
                        {
                            if (entryEncrypted) PasswordFailure();
                            throw new UnpackFailureException(UnpackError.CorruptArchive, "条目长度或校验和不匹配，文件可能损坏。");
                        }
                        if (!(isRar5 && entryEncrypted)) crcFiles++;
                        if (entry.Size > 0) lengthFiles++;
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
                isRar5 && encrypted ? "RAR5 加密内容已解码，但当前引擎未验证加密内容的校验值；请保留原包。" : null)
            {
                Evidence = Array.AsReadOnly(new[] { new ArchiveCheckEvidence("条目长度（非零声明）", lengthFiles),
                    new ArchiveCheckEvidence("CRC32", crcFiles) }.Where(e => e.Files > 0).ToArray())
            };
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
            if (possibleSplit) throw new UnpackFailureException(UnpackError.MissingVolumeOrCorruptArchive, "7z 数字卷来源不完整；可能缺卷，也可能已经截断损坏。");
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
        return new ExtractedArchive("Tar", files.AsReadOnly(), false)
        {
            Evidence = files.Count == 0 ? [] : Array.AsReadOnly(new[] { new ArchiveCheckEvidence("条目长度", files.Count) }),
            CheckLimitations = Array.AsReadOnly(new[] { "TAR 没有正文校验和；长度正确不能发现等长内容篡改。" })
        };
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
            var copied = await StreamCopy.CopyAsync(decoded, output, budget, progress, cancellationToken).ConfigureAwait(false);
            if (kind == ArchiveKind.GZip)
            {
                // GZipStream 在部分截断尾部上可能直接返回 EOF。单成员流必须另外核对尾部 CRC 和 ISIZE，
                // 不能把读取结束当作完整容器；多成员拼接未声明支持，合计内容不能冒充最后成员的校验值。
                if (input.Length < 18) throw new UnpackFailureException(UnpackError.CorruptArchive, "GZip 尾部不完整。");
                input.Position = input.Length - 8; var footer = new byte[8];
                await input.ReadExactlyAsync(footer, cancellationToken).ConfigureAwait(false);
                if (BinaryPrimitives.ReadUInt32LittleEndian(footer) != copied.Crc ||
                    BinaryPrimitives.ReadUInt32LittleEndian(footer.AsSpan(4)) != unchecked((uint)copied.Length))
                    throw new UnpackFailureException(UnpackError.CorruptArchive, "GZip 长度或 CRC 不符：可能截断、损坏或属于未支持的多成员拼接流。");
            }
        }
        if (await ArchiveProbe.DetectAsync(intermediate, cancellationToken).ConfigureAwait(false) == ArchiveKind.Tar)
        {
            var tarResult = await ExtractAsync(intermediate, destination, null, LegacyNameEncoding.Utf8, budget, progress, cancellationToken).ConfigureAwait(false);
            File.Delete(intermediate);
            return tarResult with
            {
                Format = "Tar+" + kind,
                Evidence = kind == ArchiveKind.GZip ? Array.AsReadOnly(tarResult.Evidence.Append(new ArchiveCheckEvidence("外层 GZip CRC32 与长度（1 个 TAR 流）", 1)).ToArray()) : tarResult.Evidence,
                CheckLimitations = kind == ArchiveKind.GZip ? tarResult.CheckLimitations :
                    Array.AsReadOnly(tarResult.CheckLimitations.Append("外层压缩流已完整解码；未单独报告容器校验算法，不能据此声明认证通过。").ToArray())
            };
        }
        var name = Path.GetFileNameWithoutExtension(source);
        if (string.IsNullOrWhiteSpace(name)) name = "内容";
        var target = PathPolicy.EntryPath(destination, name, false);
        File.Move(intermediate, target);
        return new ExtractedArchive(kind.ToString(), Array.AsReadOnly(new[] { name }), false)
        {
            Evidence = Array.AsReadOnly(new[] { new ArchiveCheckEvidence(kind == ArchiveKind.GZip ? "GZip CRC32 与长度" : "完整压缩流解码", 1) }),
            CheckLimitations = kind == ArchiveKind.GZip ? [] : Array.AsReadOnly(new[] { "未单独报告容器校验算法；XZ 等无校验变体不能据完整解码声明校验和或认证通过。" })
        };
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

        public static async Task<EntryCursor> CreateAsync(IReadOnlyList<Stream> inputs, ReaderOptions options, ArchiveKind kind, CancellationToken token)
        {
            if (kind != ArchiveKind.SevenZip)
                return new EntryCursor(await ReaderFactory.OpenAsyncReader(inputs[0], options, token).ConfigureAwait(false));
            var archive = await SharpCompress.Archives.SevenZip.SevenZipArchive.OpenAsyncArchive(inputs, options, token).ConfigureAwait(false);
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

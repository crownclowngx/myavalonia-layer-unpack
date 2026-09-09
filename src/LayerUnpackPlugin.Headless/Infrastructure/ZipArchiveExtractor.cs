using System.Text;
using ICSharpCode.SharpZipLib.Zip;
using LayerUnpackPlugin.Headless.Application;
using LayerUnpackPlugin.Headless.Contracts;
using LayerUnpackPlugin.Headless.Domain;

namespace LayerUnpackPlugin.Headless.Infrastructure;

/// <summary>ZIP 专用适配：从中心目录获取属性，使用 SharpZipLib 验证 AES 认证码，并逐块补验普通 ZIP 的 CRC。</summary>
/// <remarks>不修改 ZipStrings 全局编码；每次打开独占 StringCodec 和密码，避免多个 Document 互相影响。</remarks>
internal static class ZipArchiveExtractor
{
    internal static async Task<ExtractedArchive> ExtractAsync(string source, string destination, string? password,
        LegacyNameEncoding encoding, ExecutionBudget budget, Action<long> progress, CancellationToken token)
    {
        var encrypted = false;
        try
        {
            await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true);
            // 在引擎分配完整目录前检查卷号和有限条目/目录空间，正文检查与浏览保持同一分卷边界。
            ZipDirectoryGuard.Validate(input, new BrowseLimits { Extraction = budget.Limits, MaxRows = budget.Limits.MaxEntries }, token);
            input.Position = 0;
            using var archive = new ZipFile(input, leaveOpen: true, StringCodec.FromEncoding(GetEncoding(encoding)).WithZipCryptoEncoding(Encoding.UTF8));
            archive.Password = password;
            var files = new List<string>();
            var crcFiles = 0; var authenticatedFiles = 0;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ZipEntry entry in archive)
            {
                token.ThrowIfCancellationRequested();
                budget.AddEntry(); encrypted |= entry.IsCrypted;
                var attributes = entry.ExternalFileAttributes;
                if (ArchiveEntryPolicy.Unsupported(attributes, entry.IsDirectory))
                    throw new UnpackFailureException(UnpackError.UnsafePath, "ZIP 包含链接、特殊对象或不一致的条目类型，已停止写入。", true);
                var target = PathPolicy.EntryPath(destination, entry.Name, entry.IsDirectory);
                if (!seen.Add(target)) throw new UnpackFailureException(UnpackError.OutputError, "ZIP 包含重复或大小写冲突的路径。");
                if (entry.IsDirectory) { Directory.CreateDirectory(target); continue; }
                await ExtractEntryAsync(archive, entry, target, password, budget, progress, token).ConfigureAwait(false);
                if (entry.AESKeySize == 0) crcFiles++; else authenticatedFiles++;
                files.Add(Path.GetRelativePath(destination, target));
            }
            return new ExtractedArchive("Zip", files.AsReadOnly(), password is not null)
            {
                Evidence = Array.AsReadOnly(new[] { new ArchiveCheckEvidence("条目长度", files.Count),
                    new ArchiveCheckEvidence("CRC32", crcFiles), new ArchiveCheckEvidence("AES 认证码", authenticatedFiles) }.Where(e => e.Files > 0).ToArray())
            };
        }
        catch (ICSharpCode.SharpZipLib.SharpZipBaseException)
        {
            if (token.IsCancellationRequested) throw new OperationCanceledException(token);
            if (encrypted) throw PasswordFailure();
            throw new UnpackFailureException(UnpackError.CorruptArchive, "ZIP 目录或内容不完整，无法验证解压结果。");
        }
    }

    /// <summary>全量解压与选择提取共用同一正文校验边界，确保 AES 认证尾部和 CRC 不因入口不同而绕过。</summary>
    internal static async Task ExtractEntryAsync(ZipFile archive, ZipEntry entry, string target, string? password,
        ExecutionBudget budget, Action<long> progress, CancellationToken token)
    {
        if (!entry.IsCompressionMethodSupported() || (entry.Flags & (1 << 6)) != 0)
            throw new UnpackFailureException(UnpackError.UnsupportedEncryption, "此 ZIP 使用首版不支持的压缩或强加密方式。");
        if (entry.IsCrypted && password is null) throw PasswordFailure();
        if (entry.Size > budget.Limits.MaxFileBytes)
            throw new UnpackFailureException(UnpackError.BudgetExceeded, "ZIP 条目大小超过单文件预算。", true);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!); PathPolicy.EnsureNoLinks(target);
        using var decoded = archive.GetInputStream(entry);
        await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true);
        var copied = await StreamCopy.CopyAsync(decoded, output, budget, progress, token).ConfigureAwait(false);
        // AE-2 的 CRC 按规范为零，库验证认证码；普通 ZIP 和 ZipCrypto 额外验证长度及 CRC，包括 CRC=0。
        if (copied.Length != entry.Size || (entry.AESKeySize == 0 && copied.Crc != (uint)entry.Crc))
        {
            if (entry.IsCrypted) throw PasswordFailure();
            throw new UnpackFailureException(UnpackError.CorruptArchive, "ZIP 条目长度或校验和不匹配。");
        }
    }

    private static UnpackFailureException PasswordFailure() => new(UnpackError.PasswordRequiredOrInvalid,
        "没有可用密码，或加密内容校验失败；可以补充候选密码后重试。");
    internal static Encoding GetEncoding(LegacyNameEncoding value)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var codePage = value switch
        {
            LegacyNameEncoding.Gb18030 => 54936,
            LegacyNameEncoding.Utf8 => 65001,
            LegacyNameEncoding.Cp437 => 437,
            LegacyNameEncoding.Cp866 => 866,
            _ => throw new ArgumentOutOfRangeException(nameof(value))
        };
        return Encoding.GetEncoding(codePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
    }
}

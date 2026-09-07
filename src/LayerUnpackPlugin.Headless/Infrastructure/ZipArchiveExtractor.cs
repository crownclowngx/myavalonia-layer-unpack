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
            using var archive = new ZipFile(input, leaveOpen: true, StringCodec.FromEncoding(GetEncoding(encoding)).WithZipCryptoEncoding(Encoding.UTF8));
            archive.Password = password;
            var files = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ZipEntry entry in archive)
            {
                token.ThrowIfCancellationRequested();
                budget.AddEntry(); encrypted |= entry.IsCrypted;
                var attributes = entry.ExternalFileAttributes;
                if (attributes != -1 && ((attributes & (int)FileAttributes.ReparsePoint) != 0 || ((attributes >> 16) & 0xF000) == 0xA000))
                    throw new UnpackFailureException(UnpackError.UnsafePath, "ZIP 包含链接条目，已停止写入。", true);
                var target = PathPolicy.EntryPath(destination, entry.Name, entry.IsDirectory);
                if (!seen.Add(target)) throw new UnpackFailureException(UnpackError.OutputError, "ZIP 包含重复或大小写冲突的路径。");
                if (entry.IsDirectory) { Directory.CreateDirectory(target); continue; }
                if (!entry.IsCompressionMethodSupported() || (entry.Flags & (1 << 6)) != 0)
                    throw new UnpackFailureException(UnpackError.UnsupportedEncryption, "此 ZIP 使用首版不支持的压缩或强加密方式。");
                if (entry.IsCrypted && password is null) throw PasswordFailure();
                if (entry.Size > budget.Limits.MaxFileBytes)
                    throw new UnpackFailureException(UnpackError.BudgetExceeded, "ZIP 条目大小超过单文件预算。", true);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!); PathPolicy.EnsureNoLinks(target);
                using var decoded = archive.GetInputStream(entry);
                await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true);
                var copied = await StreamCopy.CopyAsync(decoded, output, budget, progress, token).ConfigureAwait(false);
                // AES 条目由库验证认证码；AE-2 的 CRC 字段按规范为零，不能当普通 CRC 比较。
                if (copied.Length != entry.Size || (entry.AESKeySize == 0 && copied.Crc != (uint)entry.Crc))
                    throw new UnpackFailureException(UnpackError.CorruptArchive, "ZIP 条目长度或校验和不匹配。");
                files.Add(Path.GetRelativePath(destination, target));
            }
            return new ExtractedArchive("Zip", files.AsReadOnly(), password is not null);
        }
        catch (ICSharpCode.SharpZipLib.SharpZipBaseException)
        {
            if (token.IsCancellationRequested) throw new OperationCanceledException(token);
            if (encrypted) throw PasswordFailure();
            throw new UnpackFailureException(UnpackError.CorruptArchive, "ZIP 目录或内容不完整，无法验证解压结果。");
        }
    }

    private static UnpackFailureException PasswordFailure() => new(UnpackError.PasswordRequiredOrInvalid,
        "没有可用密码，或加密内容校验失败；可以补充候选密码后重试。");
    private static Encoding GetEncoding(LegacyNameEncoding value)
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

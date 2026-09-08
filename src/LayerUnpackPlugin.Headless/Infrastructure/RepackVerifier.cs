using System.Security.Cryptography;
using ICSharpCode.SharpZipLib.Zip;
using LayerUnpackPlugin.Headless.Contracts;

namespace LayerUnpackPlugin.Headless.Infrastructure;

/// <summary>目标关闭写句柄后逐项回读，以冻结的预期路径、类型、长度和 SHA-256 验证。
/// 只解码到固定缓冲，不另写磁盘；不把来源受限的内容摘要误作来源真实性证明。</summary>
internal static class RepackVerifier
{
    internal static async Task VerifyAsync(string path, PackPlan plan, PackSecret? secret, CancellationToken token)
    {
        using var zip = new ZipFile(File.OpenRead(path));
        if (secret is not null) zip.Password = secret.Password;
        if (zip.Count != plan.Entries.Count) Invalid();
        var expected = plan.Entries.ToDictionary(e => e.EntryName + (e.IsDirectory ? "/" : ""), StringComparer.Ordinal);
        var buffer = new byte[131072];
        foreach (ZipEntry entry in zip)
        {
            token.ThrowIfCancellationRequested();
            if (!expected.Remove(entry.Name, out var source)) { Invalid(); return; }
            if (entry.IsDirectory != source.IsDirectory || entry.Size != source.Length ||
                entry.IsCrypted != (!source.IsDirectory && plan.Request.Options.Encrypt)) Invalid();
            if (source.IsDirectory) continue;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using var stream = zip.GetInputStream(entry);
            long length = 0; int count;
            while ((count = await stream.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
            {
                if ((length += count) > source.Length) Invalid();
                hash.AppendData(buffer, 0, count);
            }
            if (length != source.Length || Convert.ToHexString(hash.GetHashAndReset()) != source.Sha256) Invalid();
        }
    }
    private static void Invalid() => throw new PackFailureException(PackError.OutputError, "新 ZIP 回读内容与预期清单不一致，已拒绝提交。");
}

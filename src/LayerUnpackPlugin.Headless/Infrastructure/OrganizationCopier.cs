using System.Buffers;
using System.Security.Cryptography;
using LayerUnpackPlugin.Headless.Contracts;

namespace LayerUnpackPlugin.Headless.Infrastructure;

/// <summary>普通复制的窄端口，便于注入读取错误和取消；不承担来源归属、命名或提交策略。</summary>
public interface IOrganizationCopier
{
    Task CopyAsync(OrganizationMapping mapping, string destination, Action<long> progress, CancellationToken cancellationToken);
}

public sealed class OrganizationCopier : IOrganizationCopier
{
    public async Task CopyAsync(OrganizationMapping mapping, string destination, Action<long> progress, CancellationToken cancellationToken)
    {
        OrganizationFiles.Check(mapping.SourcePath); OrganizationFiles.Check(destination);
        await using var source = OrganizationFiles.OpenRead(mapping.SourcePath);
        if (source.Length != mapping.Length) OrganizationFiles.Changed(mapping.SourcePath);
        await using var target = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, true);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(131072); long length = 0;
        try
        {
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
            {
                if ((length += read) > mapping.Length) OrganizationFiles.Changed(mapping.SourcePath);
                hash.AppendData(buffer, 0, read);
                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                progress(read);
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
        if (length != mapping.Length || Convert.ToHexString(hash.GetHashAndReset()) != mapping.Sha256) OrganizationFiles.Changed(mapping.SourcePath);
        await target.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}

using System.Security.Cryptography;
using LayerUnpackPlugin.Headless.Application;
using LayerUnpackPlugin.Headless.Contracts;

namespace LayerUnpackPlugin.Headless.Infrastructure;

/// <summary>普通 ZIP 与 AES 写入共享来源验证，避免新增格式遗漏摘要、读取故障或取消边界。</summary>
internal static class PackEntryCopier
{
    internal static async Task CopyAsync(PackEntry source, Stream destination, byte[] buffer, Action<int> copied, CancellationToken token)
    {
        await using var input = Open(source);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long length = 0;
        while (true)
        {
            int count;
            try { count = await input.ReadAsync(buffer, token).ConfigureAwait(false); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            { throw new PackFailureException(PackError.InputUnavailable, "无法读取源文件，请检查占用、权限和存储设备。"); }
            if (count == 0) break;
            length += count;
            if (length > source.Length) PackPlanner.SourceChanged();
            hash.AppendData(buffer, 0, count);
            await destination.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
            copied(count);
        }
        if (length != source.Length || Convert.ToHexString(hash.GetHashAndReset()) != source.Sha256) PackPlanner.SourceChanged();
        PackPlanner.CheckMetadata(source);
    }

    internal static DateTime ZipTime(DateTime utc)
    {
        var time = utc.ToLocalTime();
        return time.Year < 1980 ? new(1980, 1, 1) : time.Year > 2107 ? new(2107, 12, 31, 23, 59, 58) : time;
    }

    private static FileStream Open(PackEntry source)
    {
        try { return PackPlanner.OpenSource(source); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { throw new PackFailureException(PackError.InputUnavailable, "源文件不可用，请检查路径、权限或占用后重新准备。"); }
    }
}

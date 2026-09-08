using LayerUnpackPlugin.Headless.Domain;

namespace LayerUnpackPlugin.Headless.Infrastructure;

/// <summary>各格式共用的流写入边界：先检查实际预算，再写入固定大小的数据块并增量计算 CRC32。</summary>
internal static class StreamCopy
{
    private static readonly uint[] CrcTable = BuildTable();
    internal static async Task<(long Length, uint Crc)> CopyAsync(Stream source, Stream target, ExecutionBudget budget,
        Action<long> progress, CancellationToken cancellationToken)
    {
        var buffer = new byte[131072];
        long length = 0;
        uint crc = uint.MaxValue;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // SharpZipLib 1.4.2 的 ZipAESStream 在 byte[] 重载中验证认证尾部。
            // Memory 重载可能分派到基类 CryptoStream，绕开 Stored AES（包括零字节条目）的认证逻辑。
            // 显式使用该公开重载，确保读到 EOF 的同时完成认证，不能仅凭明文长度认定成功。
            var count = await source.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false);
            if (count == 0) break;
            length += count;
            budget.AddBytes(count, length);
            for (var i = 0; i < count; i++) crc = (crc >> 8) ^ CrcTable[(crc ^ buffer[i]) & 255];
            await target.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
            progress(count);
        }
        await target.FlushAsync(cancellationToken).ConfigureAwait(false);
        return (length, ~crc);
    }

    /// <summary>标准 CRC32 的 IEEE 多项式表；常量表全局只读，不承载任何批次状态。</summary>
    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < table.Length; i++)
        {
            var crc = i;
            for (var bit = 0; bit < 8; bit++) crc = (crc & 1) != 0 ? 0xedb88320u ^ (crc >> 1) : crc >> 1;
            table[i] = crc;
        }
        return table;
    }
}

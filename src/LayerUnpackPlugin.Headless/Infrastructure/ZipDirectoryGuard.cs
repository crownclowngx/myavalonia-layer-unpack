using System.Buffers.Binary;
using LayerUnpackPlugin.Headless.Contracts;

namespace LayerUnpackPlugin.Headless.Infrastructure;

/// <summary>先验证中心目录规模，再让 ZipFile 建立对象数组，避免仅在枚举之后才检查数量造成提前分配。</summary>
/// <remarks>仅接受单卷、无前置自解压程序的 ZIP/ZIP64。扫描元数据，不解码正文，不创建临时文件。</remarks>
internal static class ZipDirectoryGuard
{
    internal static int Validate(Stream source, BrowseLimits limits, CancellationToken token)
    {
        var tail = new byte[(int)Math.Min(source.Length, 65557)];
        source.Position = source.Length - tail.Length; source.ReadExactly(tail);
        var end = -1;
        for (var i = tail.Length - 22; i >= 0; i--)
        {
            token.ThrowIfCancellationRequested();
            if (U32(tail, i) == 0x06054b50 && i + 22 + U16(tail, i + 20) == tail.Length) { end = i; break; }
        }
        if (end < 0) Corrupt();
        if (U16(tail, end + 4) != 0 || U16(tail, end + 6) != 0) MultiVolume();
        long count = U16(tail, end + 10), size = U32(tail, end + 12), offset = U32(tail, end + 16);
        var directoryEnd = source.Length - tail.Length + end;
        if (count == ushort.MaxValue || size == uint.MaxValue || offset == uint.MaxValue)
        {
            if (end < 20 || U32(tail, end - 20) != 0x07064b50) Corrupt();
            if (U32(tail, end - 16) != 0 || U32(tail, end - 4) != 1) MultiVolume();
            var zip64Offset = U64(tail, end - 12);
            if (zip64Offset > source.Length - 56) Corrupt();
            source.Position = zip64Offset;
            var record = new byte[56]; source.ReadExactly(record);
            if (U32(record, 0) != 0x06064b50 || U64(record, 4) < 44) Corrupt();
            if (U32(record, 16) != 0 || U32(record, 20) != 0 || U64(record, 24) != U64(record, 32)) MultiVolume();
            count = U64(record, 32); size = U64(record, 40); offset = U64(record, 48); directoryEnd = zip64Offset;
        }
        else if (U16(tail, end + 8) != count) MultiVolume();
        if (count > limits.Extraction.MaxEntries || size > limits.MaxDirectoryBytes)
            throw new UnpackFailureException(UnpackError.BudgetExceeded, "ZIP 中心目录条目数或元数据空间超过浏览预算。", true);
        if (offset > directoryEnd || size > directoryEnd - offset || size < count * 46) Corrupt();
        source.Position = offset;
        var header = new byte[46];
        for (var i = 0; i < count; i++)
        {
            token.ThrowIfCancellationRequested();
            if (source.Position > offset + size - header.Length) Corrupt();
            source.ReadExactly(header);
            if (U32(header, 0) != 0x02014b50) Corrupt();
            if (U16(header, 34) != 0) MultiVolume();
            var variableLength = U16(header, 28) + U16(header, 30) + U16(header, 32);
            if (source.Position + variableLength > offset + size) Corrupt();
            source.Seek(variableLength, SeekOrigin.Current);
        }
        // 数量与长度必须相互吻合，不能用虚报少量条目的尾记录绕过对象数门禁。
        if (source.Position != offset + size) Corrupt();
        source.Position = 0;
        return (int)count;
    }

    private static ushort U16(byte[] data, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset));
    private static uint U32(byte[] data, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset));
    private static long U64(byte[] data, int offset)
    {
        var value = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(offset));
        if (value > long.MaxValue) Corrupt();
        return (long)value;
    }
    private static void Corrupt() => throw new UnpackFailureException(UnpackError.CorruptArchive,
        "ZIP 中心目录不完整或不符合本期可浏览结构；加密目录、附加目录记录及自解压包未声明支持。");
    private static void MultiVolume() => throw new UnpackFailureException(UnpackError.MissingVolume, "浏览首版不支持分卷 ZIP，请检查完整来源并进入解压任务。");
}

namespace LayerUnpackPlugin.Headless.Infrastructure;

/// <summary>识别 ZIP 风格外部属性中声明的特殊对象，禁止把 FIFO、设备或套接字降格为普通空文件。
/// 属性未声明类型时继续由条目目录标志判断；声明了类型则只接受一致的普通文件／目录。
/// 这只判断读取器公开的属性，不推断未公开的文件系统元数据。</summary>
internal static class ArchiveEntryPolicy
{
    internal static bool Unsupported(int? attributes, bool directory)
    {
        if (attributes is null or -1) return false;
        if ((attributes.Value & (int)FileAttributes.ReparsePoint) != 0) return true;
        var type = (attributes.Value >> 16) & 0xF000;
        return type != 0 && type != (directory ? 0x4000 : 0x8000);
    }
}

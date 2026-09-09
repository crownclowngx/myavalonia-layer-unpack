namespace LayerUnpackPlugin.Headless.Contracts;

public enum PackFormat { Zip, Tar, TarGZip, SevenZip }

/// <summary>创建能力独立于读取能力；选项列表与 Headless 验证共用此表，避免 UI 开放引擎未验证的组合。</summary>
public sealed record ArchiveCreationCapability(PackFormat Format, string Name, string Extension,
    IReadOnlyList<PackCompression> Compressions, bool CanEncrypt, string Description);

/// <summary>读取矩阵描述插件已经接入的操作，不把依赖库有 API 等同于插件支持。</summary>
public sealed record ArchiveReadCapability(string Format, bool CanRecognize, bool CanList,
    bool CanExtractAll, bool CanExtractSelection, string Passwords, string Validation, string Limitations);

public static class ArchiveCapabilities
{
    public static IReadOnlyList<ArchiveCreationCapability> Creation { get; } = Array.AsReadOnly(new[]
    {
        new ArchiveCreationCapability(PackFormat.Zip, "ZIP", ".zip", Array.AsReadOnly(Enum.GetValues<PackCompression>()), true,
            "默认 ZIP；支持空目录、UTF-8、自动 ZIP64 和可选 AES-256，文件名仍可见。"),
        new ArchiveCreationCapability(PackFormat.Tar, "TAR", ".tar", Array.AsReadOnly(new[] { PackCompression.Standard }), false,
            "仅打包、不压缩；PAX UTF-8，保留文件与空目录；不支持密码。"),
        new ArchiveCreationCapability(PackFormat.TarGZip, "TAR.GZ", ".tar.gz", Array.AsReadOnly(new[] { PackCompression.Standard, PackCompression.Fast, PackCompression.High }), false,
            "PAX TAR 加 GZip；保留文件与空目录；不支持密码。"),

    });

    public static IReadOnlyList<ArchiveReadCapability> Reading { get; } = Array.AsReadOnly(new[]
    {
        new ArchiveReadCapability("ZIP", true, true, true, true, "ZipCrypto、WinZip AES；名称可见",
            "长度；普通/ZipCrypto CRC；AES 认证", "单卷；旧名称需显式编码；拒绝链接、危险及冲突路径；ZIP64 大目录已测，大于 4 GiB 正文未实测"),
        new ArchiveReadCapability("7z", true, false, true, false, "已测 LZMA/LZMA2 AES 与加密头",
            "长度与条目 CRC", "已测固实读取；不支持分卷；不保留链接和完整元数据"),
        new ArchiveReadCapability("RAR4 / RAR5", true, false, true, false, "已测内容与头部加密",
            "长度与 CRC；RAR5 加密 MAC 未验证", "已测固实读取；不支持分卷与创建；拒绝重定向条目"),
        new ArchiveReadCapability("TAR", true, false, true, false, "无密码",
            "条目长度；无正文校验和", "UTF-8 普通文件与目录；拒绝链接、设备；不保留所有权、ACL 等元数据"),
        new ArchiveReadCapability("GZip / BZip2 / XZ（含 TAR 组合）", true, false, true, false, "无密码",
            "GZip 补验 CRC32 与长度；BZip2/XZ 报告完整解码，未报告的容器校验算法不作承诺",
            "单压缩流或 TAR 组合；不递归检查内嵌归档；临时展开也消耗预算")
    });

    public static ArchiveCreationCapability For(PackFormat format) => Creation.FirstOrDefault(c => c.Format == format)
        ?? throw new PackValidationException(format == PackFormat.SevenZip ? "7z 创建尚未通过锁定版本的真实样本验证，当前未开放。" : "创建格式无效。");

    internal static void Validate(PackOptions options)
    {
        var capability = For(options.Format);
        if (!capability.Compressions.Contains(options.Compression) || (options.Encrypt && !capability.CanEncrypt))
            throw new PackValidationException($"{capability.Name} 不支持所选压缩偏好或密码；{capability.Description}");
    }

    /// <summary>复合扩展名优先匹配，冲突编号放在 .tar.gz 之前，不能生成伪装格式的 .zip 名称。</summary>
    public static string ExtensionOf(string path) => Creation.Select(c => c.Extension).OrderByDescending(e => e.Length)
        .FirstOrDefault(e => path.EndsWith(e, StringComparison.OrdinalIgnoreCase)) ?? Path.GetExtension(path);
}

namespace LayerUnpackPlugin.Headless.Contracts;

/// <summary>浏览预算将压缩源读取与内容展开分开。哈希、目录扫描、失败提取均计入同一会话，重试不退款。</summary>
public sealed record BrowseLimits
{
    public UnpackLimits Extraction { get; init; } = new();
    public long MaxReadBytes { get; init; } = 32L * 1024 * 1024 * 1024;
    public long MaxDirectoryBytes { get; init; } = 64L * 1024 * 1024;
    public int MaxRows { get; init; } = 200_000;

    public void Validate()
    {
        Extraction.Validate();
        if (MaxReadBytes is <= 0 or > 1024L * 1024 * 1024 * 1024 ||
            MaxDirectoryBytes is <= 0 or > 256L * 1024 * 1024 || MaxRows is <= 0 or > 1_000_000)
            throw new UnpackValidationException("BrowseLimits", "浏览读取、目录空间和显示条目预算必须是有限的正值。");
    }
}

/// <summary>枚举不接收输出目录或密码：本期仅支持目录可读的 ZIP，加密内容在提取时单独补密。</summary>
public sealed record BrowseRequest(string SourcePath, LegacyNameEncoding NameEncoding = LegacyNameEncoding.Gb18030,
    BrowseLimits? Limits = null);

/// <summary>正序号对应 ZIP 中心目录位置，负序号仅用于补出的父目录。身份只在此快照内有效。</summary>
public readonly record struct ArchiveEntryId(Guid CatalogId, int Ordinal);

public sealed record BrowseEntry(ArchiveEntryId Id, string Path, bool IsDirectory, bool IsSynthetic,
    long? Size, long? CompressedSize, bool IsEncrypted, int Attributes, UnpackDiagnostic? Problem, string? Warning);

/// <summary>只含显式条目身份的不可变选择，不携带搜索条件；调用方改变集合不能改变正在提取的内容。</summary>
public sealed class BrowseSelection(IEnumerable<ArchiveEntryId> entries)
{
    public IReadOnlyList<ArchiveEntryId> Entries { get; } = Array.AsReadOnly(entries.ToArray());
}

public sealed record BrowsePage(IReadOnlyList<BrowseEntry> Entries, int Offset, int TotalMatches)
{
    public bool HasNext => Offset + Entries.Count < TotalMatches;
}

public enum BrowseOperation { VerifyingSource, ReadingDirectory, Extracting, Committing }
public sealed record BrowseProgress(BrowseOperation Operation, int Entries, long ReadBytes, long ExpandedBytes,
    string? CurrentPath = null);
public enum BrowseExtractState { Completed, Failed, Cancelled }

/// <summary>提交清单只在成功时返回；失败结果仍公开已消耗资源和本事务清理残留，不包含第三方异常或密码。</summary>
public sealed record BrowseExtractResult(BrowseExtractState State, string? OutputDirectory,
    IReadOnlyList<string> Files, long ReadBytes, long ExpandedBytes, UnpackDiagnostic? Error, string? CleanupWarning);

public sealed record BrowseCapability(string Format, bool CanList, bool CanExtractSelection, string Description);

public static class BrowseCapabilities
{
    public static IReadOnlyList<BrowseCapability> All { get; } = Array.AsReadOnly(new[]
    {
        new BrowseCapability("ZIP", true, true, "可读中心目录；普通、ZipCrypto 与 WinZip AES 内容按条目提取，保留相对路径。"),
        new BrowseCapability("7z / RAR", false, false, "本期未接入浏览；加密头、固实和分卷请进入解压任务，按解压支持矩阵处理。"),
        new BrowseCapability("TAR / GZip / BZip2 / XZ", false, false, "本期不提供顺序格式浏览与选择提取；可进入解压任务，不承诺仅读取所选文件。")
    });
}

namespace LayerUnpackPlugin.Headless.Contracts;

/// <summary>有限的扩展名规则，不代表内容识别。全文件模式保留嵌套归档及空目录；筛选模式只保留匹配文件及父目录。</summary>
public enum OrganizationFileTypes { All, Pdf, Images, PdfAndImages }
public sealed record OrganizationRules(OrganizationFileTypes FileTypes = OrganizationFileTypes.All, bool FlattenWrappingDirectories = false);

/// <summary>整理独立计量文件、字节和时间；验证也受时间与条目预算约束，避免普通复制成为无界任务。</summary>
public sealed record OrganizationLimits
{
    public int MaxEntries { get; init; } = 100_000;
    public long MaxTotalBytes { get; init; } = 10L * 1024 * 1024 * 1024;
    public long MaxFileBytes { get; init; } = 2L * 1024 * 1024 * 1024;
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(10);
    public void Validate()
    {
        if (MaxEntries is <= 0 or > 1_000_000 || MaxTotalBytes is <= 0 or > 1_099_511_627_776 ||
            MaxFileBytes <= 0 || MaxFileBytes > MaxTotalBytes || Timeout < TimeSpan.FromMilliseconds(10) || Timeout > TimeSpan.FromHours(1))
            throw new OrganizationFailureException(OrganizationError.BudgetExceeded, "整理预算无效，请检查条目、字节和时间限制。");
    }
}

public enum OrganizationError { MissingManifest, InputChanged, InputUnavailable, UnsafePath, OutputConflict, OutputError, BudgetExceeded, Timeout, UnexpectedError }
public enum OrganizationState { Completed, NoMatches, Failed, Cancelled }
public sealed record OrganizationDiagnostic(OrganizationError Code, string Message);
public sealed class OrganizationFailureException(OrganizationError code, string message) : Exception(message)
{
    public OrganizationError Code { get; } = code;
}

/// <summary>来源始终是顶层归档；子归档展开结果仍归该来源。RootDirectory 是真实已提交目录，不是 UI 猜出的路径。</summary>
public sealed record OrganizationSource(Guid Id, string ArchivePath, string RootDirectory, string TargetName, string RemovedPrefix);
public sealed record OrganizationMapping(Guid SourceId, string SourcePath, string TargetRelativePath, bool IsDirectory, long Length, string Sha256);
public sealed record OrganizationConflict(string OriginalPath, string SuggestedPath, string Reason);

/// <summary>只有规划器能够创建的不可变计划；映射与来源验证凭据一起冻结，执行不接受临时改名或追加条目。</summary>
public sealed class OrganizationPlan
{
    internal OrganizationPlan(Guid batchId, string outputDirectory, OrganizationRules rules, OrganizationLimits limits,
        IEnumerable<OrganizationSource> sources, IEnumerable<OrganizationMapping> mappings,
        IEnumerable<OrganizationConflict> conflicts, IEnumerable<OrganizationInput> inputs)
    {
        BatchId = batchId; OutputDirectory = outputDirectory; Rules = rules; Limits = limits;
        Sources = Array.AsReadOnly(sources.ToArray()); Mappings = Array.AsReadOnly(mappings.ToArray());
        Conflicts = Array.AsReadOnly(conflicts.ToArray()); Inputs = Array.AsReadOnly(inputs.ToArray());
        FileCount = Mappings.Count(m => !m.IsDirectory); TotalBytes = Mappings.Sum(m => m.Length);
    }
    public Guid BatchId { get; }
    public string OutputDirectory { get; }
    public OrganizationRules Rules { get; }
    public OrganizationLimits Limits { get; }
    public IReadOnlyList<OrganizationSource> Sources { get; }
    public IReadOnlyList<OrganizationMapping> Mappings { get; }
    public IReadOnlyList<OrganizationConflict> Conflicts { get; }
    public int FileCount { get; }
    public long TotalBytes { get; }
    internal IReadOnlyList<OrganizationInput> Inputs { get; }
    public IReadOnlyList<string> SourceWarnings { get; internal init; } = [];
}

/// <summary>内部源凭据与公开目标映射分离，UI 只呈现映射，不负责推断或验证磁盘身份。</summary>
internal sealed record OrganizationInput(Guid SourceId, string Path, CommittedEntry Entry);
public sealed record OrganizationProgress(string Phase, int FilesDone, int TotalFiles, long CopiedBytes, long TotalBytes, string? CurrentPath);
public sealed record OrganizationResult(OrganizationState State, string? OutputDirectory,
    IReadOnlyList<CommittedEntry> Entries, OrganizationDiagnostic? Error, string? CleanupWarning)
{
    /// <summary>成功复制时冻结的来源边界，供结果分别打包；不靠事后扫描目录恢复来源。</summary>
    public IReadOnlyList<OrganizationOutputSource> SourceGroups { get; init; } = [];
    public IReadOnlyList<string> SourceWarnings { get; init; } = [];
}
public sealed record OrganizationOutputSource(string Source, string RelativeRoot);

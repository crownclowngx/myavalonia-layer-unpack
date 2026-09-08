namespace LayerUnpackPlugin.Headless.Contracts;

/// <summary>普通转换固定一层且不探测内嵌包；展开整理必须显式选择，并沿用 R04/R05 的层数与规则。</summary>
public enum ConversionMode { FormatOnly, ExpandAndOrganize }
public enum RepackState { Completed, CompletedWithWarnings, PartiallyCompleted, Failed, Cancelled, Skipped, NotRun }
public enum RepackPhase { Reading, Planning, Writing, Verifying, Committed, Cleaning }

/// <summary>整项任务的累计写入上限覆盖所有来源、密码尝试、中间流和 ZIP 临时文件。
/// 它是累计工作量的保守磁盘上界，失败和清理均不退款；阶段限制仍约束单文件、条目及引擎尝试。
/// 整理直接使用映射，不另建复制目录，因此整理阶段没有额外落盘字节。</summary>
public sealed record RepackLimits
{
    public long MaxWrittenBytes { get; init; } = 24L * 1024 * 1024 * 1024;
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(30);
    public UnpackLimits Unpack { get; init; } = new();
    public PackLimits Pack { get; init; } = new();
    public void Validate()
    {
        if (MaxWrittenBytes is <= 0 or > 1_099_511_627_776 || Timeout < TimeSpan.FromMilliseconds(10) ||
            Timeout > TimeSpan.FromHours(1) || Unpack is null || Pack is null)
            throw new PackValidationException("复合任务必须设置有限的累计写入量和总时间上限。");
        Unpack.Validate(); Pack.Validate();
    }
}

/// <summary>公开请求没有秘密。来源密码和目标秘密仅经 Execute 的两个独立参数进入当前调用。</summary>
public sealed class ConversionRequest
{
    public ConversionRequest(IEnumerable<string> sources, string outputDirectory, ConversionMode mode = ConversionMode.FormatOnly,
        int maxDepth = 2, OrganizationRules? rules = null, PackOptions? options = null, RepackLimits? limits = null,
        LegacyNameEncoding legacyNameEncoding = LegacyNameEncoding.Gb18030)
    {
        Sources = Array.AsReadOnly(sources.ToArray()); OutputDirectory = outputDirectory; Mode = mode;
        MaxDepth = maxDepth; Rules = rules ?? new(); Options = options ?? new(); Limits = limits ?? new(); LegacyNameEncoding = legacyNameEncoding;
    }
    public IReadOnlyList<string> Sources { get; }
    public string OutputDirectory { get; }
    public ConversionMode Mode { get; }
    public int MaxDepth { get; }
    public OrganizationRules Rules { get; }
    public PackOptions Options { get; }
    public RepackLimits Limits { get; }
    public LegacyNameEncoding LegacyNameEncoding { get; }
}

/// <summary>从现有结果带入的不可变计划。只有提交清单规划器能构造；进入页面或生成计划均不写文件。</summary>
public sealed class RepackPlan
{
    internal RepackPlan(IEnumerable<RepackGroupPlan> groups, RepackLimits limits)
    { Groups = Array.AsReadOnly(groups.ToArray()); Limits = limits; }
    public IReadOnlyList<RepackGroupPlan> Groups { get; }
    public RepackLimits Limits { get; }
}
public sealed record RepackGroupPlan(string Source, PackPlan Plan, IReadOnlyList<string> Warnings);
public sealed record RepackDiagnostic(string Stage, string Code, string Message);
public sealed record RepackGroupResult(string Source, string PlannedOutput, RepackState State, string? OutputPath,
    int FileCount, long ArchiveBytes, IReadOnlyList<string> Warnings, RepackDiagnostic? Error);
public sealed record RepackUsage(long ExpandedBytes, long ArchiveBytes, int PackedEntries)
{
    public long WrittenBytes => ExpandedBytes + ArchiveBytes;
}
public sealed record RepackProgress(int SourceIndex, int SourceCount, string Source, RepackPhase Phase,
    RepackUsage Usage, string? CurrentEntry = null);

/// <summary>成功包和受限包分别计数；目标回读通过不能提升来源的认证状态。清理残留属于整项任务结果。</summary>
public sealed class RepackResult
{
    internal RepackResult(IEnumerable<RepackGroupResult> groups, RepackUsage usage, IEnumerable<string> cleanupWarnings)
    {
        Groups = Array.AsReadOnly(groups.ToArray()); Usage = usage; CleanupWarnings = Array.AsReadOnly(cleanupWarnings.Distinct().ToArray());
        State = Groups.Any(g => g.State == RepackState.Cancelled) ? RepackState.Cancelled :
            Groups.Any(g => g.State is RepackState.Failed or RepackState.NotRun) ? (CommittedCount > 0 ? RepackState.PartiallyCompleted : RepackState.Failed) :
            Groups.Any(g => g.State == RepackState.CompletedWithWarnings) || CleanupWarnings.Count > 0 ? RepackState.CompletedWithWarnings :
            CommittedCount > 0 ? RepackState.Completed : RepackState.Skipped;
    }
    public IReadOnlyList<RepackGroupResult> Groups { get; }
    public RepackUsage Usage { get; }
    public IReadOnlyList<string> CleanupWarnings { get; }
    public RepackState State { get; }
    public int CommittedCount => Groups.Count(g => g.OutputPath is not null);
    public int RestrictedCount => Groups.Count(g => g.State == RepackState.CompletedWithWarnings);
}

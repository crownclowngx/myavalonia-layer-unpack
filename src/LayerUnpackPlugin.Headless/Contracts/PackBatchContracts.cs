namespace LayerUnpackPlugin.Headless.Contracts;

/// <summary>批次公开描述不携带秘密。分别模式的名称由规划器生成，合并模式才使用 ArchiveName。</summary>
public sealed class PackBatchRequest
{
    public PackBatchRequest(IEnumerable<string> inputs, string outputDirectory, string archiveName = "资料.zip",
        PackGrouping grouping = PackGrouping.Combined, PackOptions? options = null, PackLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        Inputs = Array.AsReadOnly(inputs.ToArray());
        OutputDirectory = outputDirectory; ArchiveName = archiveName; Grouping = grouping;
        Options = options ?? new(); Limits = limits ?? new();
    }
    public IReadOnlyList<string> Inputs { get; }
    public string OutputDirectory { get; }
    public string ArchiveName { get; }
    public PackGrouping Grouping { get; }
    public PackOptions Options { get; }
    public PackLimits Limits { get; }
}

public sealed record PackGroupPlan(int Index, string SourceLabel, string OutputPath, PackPlan? Plan, PackDiagnostic? PreparationError);

/// <summary>全部组均准备完才允许执行；准备失败保留为独立结果，不静默丢组，也不以失败来源的猜测清单重试。</summary>
public sealed class PackBatchPlan
{
    internal PackBatchPlan(PackBatchRequest request, IEnumerable<PackGroupPlan> groups, int mergedInputs)
    { Request = request; Groups = Array.AsReadOnly(groups.ToArray()); MergedInputs = mergedInputs; }
    public PackBatchRequest Request { get; }
    public IReadOnlyList<PackGroupPlan> Groups { get; }
    public int MergedInputs { get; }
    public int FileCount => Groups.Sum(g => g.Plan?.FileCount ?? 0);
    public long TotalBytes => Groups.Sum(g => g.Plan?.TotalBytes ?? 0);
    public int ExcludedCount => Groups.Sum(g => g.Plan?.ExcludedItems.Count ?? 0);
}

public enum PackBatchState { Ready, Completed, PartiallyCompleted, Failed, Cancelled, Skipped }

public sealed record PackGroupResult(int Index, string SourceLabel, string PlannedOutputPath, PackResult Result, bool CanRetry);

/// <summary>只累加已提交的产物字节；空内容与取消不算成功，也不推算节省空间。</summary>
public sealed class PackBatchResult
{
    internal PackBatchResult(IEnumerable<PackGroupResult> groups)
    {
        Groups = Array.AsReadOnly(groups.ToArray());
        State = Groups.Any(g => g.Result.State == PackState.Cancelled) ? PackBatchState.Cancelled :
            Groups.Any(g => g.Result.State == PackState.Pending) ? PackBatchState.Ready :
            FailedCount > 0 ? (CompletedCount > 0 ? PackBatchState.PartiallyCompleted : PackBatchState.Failed) :
            CompletedCount > 0 ? PackBatchState.Completed : PackBatchState.Skipped;
    }
    public IReadOnlyList<PackGroupResult> Groups { get; }
    public PackBatchState State { get; }
    public int CompletedCount => Groups.Count(g => g.Result.State == PackState.Completed);
    public int FailedCount => Groups.Count(g => g.Result.State == PackState.Failed);
    public int CancelledCount => Groups.Count(g => g.Result.State == PackState.Cancelled);
    public int SkippedCount => Groups.Count(g => g.Result.State == PackState.Skipped);
    public long SourceBytes => Groups.Where(g => g.Result.State == PackState.Completed).Sum(g => g.Result.SourceBytes);
    public long ArchiveBytes => Groups.Where(g => g.Result.State == PackState.Completed).Sum(g => g.Result.ArchiveBytes);
    public bool CanRetry => Groups.Any(g => g.CanRetry);
}

public sealed record PackBatchProgress(int GroupIndex, int TotalGroups, string SourceLabel, PackProgress Progress);

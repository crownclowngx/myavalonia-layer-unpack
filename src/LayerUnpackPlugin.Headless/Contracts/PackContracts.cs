namespace LayerUnpackPlugin.Headless.Contracts;

/// <summary>创建归档的独立资源边界：读取源文件与生成 ZIP 分别计量，不复用解压膨胀预算。</summary>
public sealed record PackLimits
{
    public long MaxTotalBytes { get; init; } = 10L * 1024 * 1024 * 1024;
    public long MaxFileBytes { get; init; } = 2L * 1024 * 1024 * 1024;
    public long MaxArchiveBytes { get; init; } = 12L * 1024 * 1024 * 1024;
    public int MaxEntries { get; init; } = 100_000;
    public int MaxInputs { get; init; } = 1_000;
    public int MaxDirectoryDepth { get; init; } = 128;
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(10);

    public void Validate()
    {
        const long ceiling = 1024L * 1024 * 1024 * 1024;
        if (MaxTotalBytes is <= 0 or > ceiling || MaxFileBytes is <= 0 or > ceiling || MaxFileBytes > MaxTotalBytes ||
            MaxArchiveBytes is <= 0 or > ceiling || MaxEntries is <= 0 or > 1_000_000 || MaxInputs is <= 0 or > 10_000 ||
            MaxDirectoryDepth is <= 0 or > 256 || Timeout < TimeSpan.FromMilliseconds(10) || Timeout > TimeSpan.FromHours(1))
            throw new PackValidationException("资源限制必须为有效的有限值，单文件上限不能超过总源字节上限。");
    }
}

/// <summary>一次全部合包请求。复制输入，避免调用方编辑集合改变后台任务；R02 不含密码和格式开关。</summary>
public sealed class PackRequest
{
    public PackRequest(IEnumerable<string> inputs, string outputPath, PackLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        Inputs = Array.AsReadOnly(inputs.ToArray());
        OutputPath = outputPath;
        Limits = limits ?? new PackLimits();
    }
    public IReadOnlyList<string> Inputs { get; }
    public string OutputPath { get; }
    public PackLimits Limits { get; }
}

public enum PackState { Scanning, Writing, Finalizing, Completed, Failed, Cancelled }
public enum PackError { InputUnavailable, InputChanged, UnsafePath, OutputError, BudgetExceeded, Timeout, UnexpectedError }
public sealed record PackDiagnostic(PackError Code, string Message);
public sealed record PackRoot(string SourcePath, string EntryName, bool IsDirectory);
public sealed record PackEntry(string SourcePath, string EntryName, bool IsDirectory, long Length, DateTime LastWriteUtc, string Sha256);

/// <summary>只由规划器生成的不可变清单。摘要是源内容验证依据，路径映射是 UI 可检查的产品结果。</summary>
public sealed class PackPlan
{
    internal PackPlan(PackRequest request, IEnumerable<PackRoot> roots, IEnumerable<PackEntry> entries, int mergedInputs, int excludedOutputs)
    {
        Request = request;
        Roots = Array.AsReadOnly(roots.ToArray());
        Entries = Array.AsReadOnly(entries.ToArray());
        MergedInputs = mergedInputs;
        ExcludedOutputs = excludedOutputs;
    }
    public PackRequest Request { get; }
    public IReadOnlyList<PackRoot> Roots { get; }
    public IReadOnlyList<PackEntry> Entries { get; }
    public int MergedInputs { get; }
    public int ExcludedOutputs { get; }
    public long TotalBytes => Entries.Sum(e => e.Length);
    public int FileCount => Entries.Count(e => !e.IsDirectory);
}

public sealed record PackProgress(PackState State, string? CurrentEntry, int EntriesDone, int TotalEntries, long ReadBytes, long TotalBytes);
public sealed record PackResult(PackState State, string? OutputPath, long SourceBytes, long ArchiveBytes, int FileCount,
    TimeSpan Elapsed, PackDiagnostic? Error, string? CleanupWarning);
public sealed class PackValidationException(string message) : ArgumentException(message);

/// <summary>可公开呈现的业务故障，不携带第三方异常链或不受控的系统错误文本。</summary>
public sealed class PackFailureException(PackError code, string message) : Exception(message)
{
    public PackError Code { get; } = code;
}

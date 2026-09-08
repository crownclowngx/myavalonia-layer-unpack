using System.Text.Json.Serialization;

namespace LayerUnpackPlugin.Headless.Contracts;

/// <summary>一次批次的资源上限。重试继续消耗同一账本，防止换密码或换层绕过限制。</summary>
public sealed record UnpackLimits
{
    public const int DepthCeiling = 16;
    public const int PasswordCountCeiling = 64;
    public long MaxTotalBytes { get; init; } = 10L * 1024 * 1024 * 1024;
    public long MaxFileBytes { get; init; } = 2L * 1024 * 1024 * 1024;
    public long MaxInputBytes { get; init; } = 4L * 1024 * 1024 * 1024;
    public int MaxEntries { get; init; } = 100_000;
    public int MaxArchives { get; init; } = 1_000;
    public int MaxAttempts { get; init; } = 2_048;
    public TimeSpan ArchiveTimeout { get; init; } = TimeSpan.FromMinutes(5);

    public void Validate()
    {
        const long maximum = 1024L * 1024 * 1024 * 1024;
        if (MaxTotalBytes is <= 0 or > maximum || MaxFileBytes is <= 0 or > maximum ||
            MaxInputBytes is <= 0 or > maximum || MaxFileBytes > MaxTotalBytes ||
            MaxEntries is <= 0 or > 1_000_000 || MaxArchives is <= 0 or > 10_000 ||
            MaxAttempts is <= 0 or > 100_000 || ArchiveTimeout < TimeSpan.FromMilliseconds(10) ||
            ArchiveTimeout > TimeSpan.FromHours(1))
            throw new UnpackValidationException("Limits", "资源预算必须为有效的有限值，且单文件上限不能超过批次上限。");
    }
}

/// <summary>不可变的执行输入。密码不参与 JSON 序列化或对象字符串表示。</summary>
/// <remarks>构造时复制调用方集合，后台执行不再读取 UI 正在编辑的参数。</remarks>
public sealed class UnpackRequest
{
    public UnpackRequest(IEnumerable<string> inputs, string outputDirectory, int maxDepth = 1,
        IEnumerable<string>? passwords = null, UnpackLimits? limits = null, LegacyNameEncoding legacyNameEncoding = LegacyNameEncoding.Gb18030)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        Inputs = Array.AsReadOnly(inputs.ToArray());
        OutputDirectory = outputDirectory;
        MaxDepth = maxDepth;
        Passwords = Array.AsReadOnly((passwords ?? []).ToArray());
        Limits = limits ?? new UnpackLimits();
        LegacyNameEncoding = legacyNameEncoding;
    }

    public IReadOnlyList<string> Inputs { get; }
    public string OutputDirectory { get; }
    public int MaxDepth { get; }
    [JsonIgnore] public IReadOnlyList<string> Passwords { get; }
    public UnpackLimits Limits { get; }
    public LegacyNameEncoding LegacyNameEncoding { get; }
    public override string ToString() => $"解压请求：{Inputs.Count} 项，深度 {MaxDepth}";
}

public enum BatchState { Ready, Running, Completed, PartialFailure, Failed, Cancelled }
/// <summary>只影响没有 Unicode 标志的旧 ZIP 文件名。显式选择，避免凭乱码猜测而改变文件身份。</summary>
public enum LegacyNameEncoding { Gb18030, Utf8, Cp437, Cp866 }
public enum NodeState { Queued, Probing, Extracting, Extracted, Failed, Cancelled, DepthLimit, NotRun }
public enum UnpackError
{
    PasswordRequiredOrInvalid, CorruptArchive, UnsupportedFormat, UnsupportedEncryption,
    MissingVolume, UnsafePath, BudgetExceeded, InputChanged, InputUnavailable, OutputError, Timeout, InvalidNameEncoding, UnexpectedError
}

/// <summary>仅包含经过归一化的诊断；不保留引擎异常对象，避免密码通过异常链泄漏。</summary>
public sealed record UnpackDiagnostic(UnpackError Code, string Message);

public sealed record ArchiveNodeResult(Guid Id, Guid? ParentId, string SourcePath, int Depth,
    NodeState State, string? Format, string? OutputDirectory, UnpackDiagnostic? Error,
    IReadOnlyList<string> CleanupWarnings, long WrittenBytes, string? Warning = null)
{
    /// <summary>本节点提交时捕获的完整条目清单，路径相对 OutputDirectory；不包含后来展开的子归档内容。
    /// null 表示旧调用方未提供清单，不能据输出目录推测。空集合表示已确认的空归档。</summary>
    public IReadOnlyList<CommittedEntry>? CommittedEntries { get; init; }
    /// <summary>重试保持原输入、编码和预算，只适用于补密或访问条件恢复；跨层和 UI 共用同一判断。</summary>
    public bool CanRetry => State == NodeState.Failed && CleanupWarnings.Count == 0 &&
        Error?.Code is not (UnpackError.UnsafePath or UnpackError.InputChanged or UnpackError.BudgetExceeded or
            UnpackError.InvalidNameEncoding or UnpackError.UnsupportedFormat or UnpackError.UnsupportedEncryption or UnpackError.MissingVolume);
}

/// <summary>已提交普通条目的身份与内容凭据。目录也保留，用于空目录和精确包装层判断。
/// 创建时间与修改时间辅助发现替换，SHA-256 验证文件内容；它不是跨平台文件系统对象标识。</summary>
public sealed record CommittedEntry(string RelativePath, bool IsDirectory, long Length,
    DateTime LastWriteUtc, DateTime CreationUtc, string Sha256);

/// <summary>运行结果是独立快照，不暴露调度器、密码池和可变节点。成功数只统计本包提交。</summary>
public sealed record UnpackResult(Guid BatchId, BatchState State, IReadOnlyList<ArchiveNodeResult> Nodes,
    long TotalWrittenBytes, int AttemptCount, bool RetryBlocked = false)
{
    public int Succeeded => Nodes.Count(n => n.State == NodeState.Extracted);
    public int Failed => Nodes.Count(n => n.State == NodeState.Failed);
    public int StoppedByDepth => Nodes.Count(n => n.State == NodeState.DepthLimit);
    public int DiscoveryFailures => Nodes.Count(n => n.State == NodeState.Extracted && n.Error is not null);
}

public sealed record UnpackProgress(Guid OperationId, UnpackResult Snapshot);

public sealed class UnpackValidationException(string field, string message) : ArgumentException(message)
{
    public string Field { get; } = field;
}

/// <summary>统一业务故障，不携带第三方异常或密码；批次级故障阻止继续向后调度。</summary>
public sealed class UnpackFailureException(UnpackError code, string message, bool stopBatch = false) : Exception(message)
{
    public UnpackError Code { get; } = code;
    public bool StopBatch { get; } = stopBatch;
}

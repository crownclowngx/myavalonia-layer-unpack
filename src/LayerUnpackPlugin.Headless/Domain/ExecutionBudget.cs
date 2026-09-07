using LayerUnpackPlugin.Headless.Contracts;

namespace LayerUnpackPlugin.Headless.Domain;

/// <summary>串行批次的累计资源账本。失败、回滚和重试都不返还已经发生的实际工作量。</summary>
public sealed class ExecutionBudget(UnpackLimits limits)
{
    public UnpackLimits Limits { get; } = limits;
    public long Bytes { get; private set; }
    public int Entries { get; private set; }
    public int Archives { get; private set; }
    public int Attempts { get; private set; }

    public void AddBytes(int count, long fileBytes)
    {
        if (count < 0 || count > Limits.MaxTotalBytes - Bytes) Throw("批次累计展开量超过预算。");
        Bytes += count;
        if (fileBytes > Limits.MaxFileBytes) Throw("单文件展开量超过预算。");
    }

    public void AddEntry() { if (++Entries > Limits.MaxEntries) Throw("条目总数超过预算。"); }
    public void AddArchive() { if (++Archives > Limits.MaxArchives) Throw("压缩包节点总数超过预算。"); }
    public void AddAttempt() { if (++Attempts > Limits.MaxAttempts) Throw("解压尝试总数超过预算。"); }
    private static void Throw(string message) => throw new UnpackFailureException(UnpackError.BudgetExceeded, message, true);
}

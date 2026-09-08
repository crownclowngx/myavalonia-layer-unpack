using LayerUnpackPlugin.Headless.Contracts;

namespace LayerUnpackPlugin.Headless.Domain;

/// <summary>由一次执行独占的共享账本。写入前扣额，回滚不返还；不依赖会丢失或被 UI 节流的进度回调。</summary>
public sealed class RepackBudget(RepackLimits limits)
{
    private long _expanded;
    private long _archives;
    private int _entries;
    public RepackUsage Usage => new(_expanded, _archives, _entries);
    public bool Exhausted { get; private set; }
    public void AddExpandedBytes(long count) { CheckBytes(count); _expanded += count; }
    public void AddArchiveBytes(long count) { CheckBytes(count); _archives += count; }
    public void AddPackedEntries(int count)
    {
        if (count < 0 || count > limits.Pack.MaxEntries - _entries) Fail();
        _entries += count;
    }
    private void CheckBytes(long count)
    {
        if (count < 0 || count > limits.MaxWrittenBytes - _expanded - _archives) Fail();
    }
    private void Fail()
    {
        Exhausted = true;
        throw new PackFailureException(PackError.BudgetExceeded, "整项转换／打包任务的累计资源预算已耗尽，已提交 ZIP 保留。");
    }
}

using LayerUnpackPlugin.Headless.Contracts;

namespace LayerUnpackPlugin.Headless.Application;

/// <summary>
/// 只拥有本次冻结计划、逐组结果和一个目标秘密。执行与重试互斥，成功组永远不再执行。
/// 重试只接纳有完整计划的读取、输出、超时故障；源变化、取消、准备失败必须由用户另建任务确认。
/// </summary>
public sealed class PackBatchSession : IAsyncDisposable
{
    private readonly IPackService _service;
    private readonly PackBatchPlan _plan;
    private readonly PackSecret? _secret;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _closing = new();
    private Task<PackBatchResult>? _active;
    private Task? _dispose;
    private bool _closed;
    private bool _started;
    public PackBatchResult CurrentResult { get; private set; }

    internal PackBatchSession(IPackService service, PackBatchPlan plan, PackSecret? secret)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.Request.Options.Encrypt != (secret is not null))
            throw new PackValidationException("加密开关与本次目标密码必须一致。");
        if (secret is not null) _ = secret.Password;
        _service = service; _plan = plan; _secret = secret;
        CurrentResult = new(plan.Groups.Select(g => new PackGroupResult(g.Index, g.SourceLabel, g.OutputPath,
            new(g.PreparationError is not null ? PackState.Failed : g.Plan!.Entries.Count == 0 ? PackState.Skipped : PackState.Pending,
                null, g.Plan?.TotalBytes ?? 0, 0, g.Plan?.FileCount ?? 0, TimeSpan.Zero, g.PreparationError, null), false)));
    }

    public Task<PackBatchResult> ExecuteAsync(IProgress<PackBatchProgress>? progress = null, CancellationToken cancellationToken = default)
        => Start(false, progress, cancellationToken);
    public Task<PackBatchResult> RetryFailedAsync(IProgress<PackBatchProgress>? progress = null, CancellationToken cancellationToken = default)
        => Start(true, progress, cancellationToken);

    private Task<PackBatchResult> Start(bool retry, IProgress<PackBatchProgress>? progress, CancellationToken token)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            if (_active is { IsCompleted: false }) throw new InvalidOperationException("本批次正在执行，请等待当前操作退出。");
            if (retry && !_started) throw new InvalidOperationException("批次尚未执行，不能重试。");
            if (!retry && _started) throw new InvalidOperationException("批次已执行，请仅重试允许恢复的失败组。");
            _started = true;
            return _active = RunAsync(retry, progress, token);
        }
    }

    private async Task<PackBatchResult> RunAsync(bool retry, IProgress<PackBatchProgress>? progress, CancellationToken cancellationToken)
    {
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(_closing.Token, cancellationToken);
        var results = CurrentResult.Groups.ToArray();
        foreach (var group in _plan.Groups)
        {
            var previous = results[group.Index];
            if (retry ? !previous.CanRetry : previous.Result.State != PackState.Pending) continue;
            PackResult result;
            if (operation.IsCancellationRequested)
                result = new(PackState.Cancelled, null, group.Plan!.TotalBytes, 0, group.Plan.FileCount, TimeSpan.Zero, null, null);
            else
            {
                var relay = new PackProgressRelay(p => progress?.Report(new(group.Index, results.Length, group.SourceLabel, p)));
                result = await _service.ExecuteAsync(group.Plan!, relay, operation.Token, _secret).ConfigureAwait(false);
            }
            var canRetry = result.State == PackState.Failed && result.CleanupWarning is null &&
                result.Error?.Code is PackError.InputUnavailable or PackError.OutputError or PackError.Timeout;
            results[group.Index] = previous with { Result = result, CanRetry = canRetry };
            CurrentResult = new(results);
        }
        return CurrentResult;
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_dispose is not null) return new(_dispose);
            _closed = true; _closing.Cancel();
            return new(_dispose = CloseAsync());
        }
    }
    private async Task CloseAsync()
    {
        try { if (_active is not null) await _active.ConfigureAwait(false); }
        finally { _secret?.Dispose(); _closing.Dispose(); }
    }
}

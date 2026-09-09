using CommunityToolkit.Mvvm.Input;
using LayerUnpackPlugin.Headless.Contracts;

namespace LayerUnpackPlugin.Features.Pack;

/// <summary>异步呈现与快照审阅分离；重试只使用原会话，当前表单的修改不能改写原批次计划或目标密码。</summary>
public sealed partial class PackDocument
{
    [RelayCommand(CanExecute = nameof(CanStart))]
    private Task PreviewAsync() { DetailsExpanded = true; return RunAsync(previewOnly: true); }

    private bool CanRetryFailed() => CanEdit && BatchResult?.CanRetry == true && _session is not null;
    [RelayCommand(CanExecute = nameof(CanRetryFailed))]
    private Task RetryFailedAsync() => RunAsync(previewOnly: false, retry: true);

    private bool CanPrepareUnfinished() => CanEdit && HasUnfinishedGroups && _executedPlan is not null;
    [RelayCommand(CanExecute = nameof(CanPrepareUnfinished))]
    private async Task PrepareUnfinishedAsync()
    {
        if (!CanPrepareUnfinished()) return;
        var previousPlan = _executedPlan!;
        var unfinished = BatchResult!.Groups.Where(g => g.Result.State is PackState.Failed or PackState.Cancelled).ToArray();
        var inputs = previousPlan.Request.Grouping == PackGrouping.Combined ? previousPlan.Request.Inputs : unfinished.Select(g => g.SourceLabel).ToArray();
        foreach (var path in BatchResult.Groups.Select(g => g.Result.OutputPath).OfType<string>())
            if (!PreviousOutputs.Contains(path)) PreviousOutputs.Add(path);
        // 这是用户明确发起的新任务：只重新准备未完成来源，成功包保留入口，目标秘密要求重新输入。
        ReleaseIdleSession(); ClearSecretFields(); Inputs.Clear();
        foreach (var path in inputs) Inputs.Add(new(path));
        DetailsExpanded = true;
        await RunAsync(previewOnly: true);
        if (!IsClosed) Message += "已为未完成项建立新清单，请确认后开始；加密任务请重新输入目标密码。";
    }

    private async Task RunAsync(bool previewOnly, bool retry = false)
    {
        if (!CanEdit) return;
        IsBusy = true; IsCancelling = false; IsIndeterminate = true; ProgressValue = 0;
        var generation = ++_generation;
        var revision = _revision;
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(_closing.Token);
        _operation = operation;
        PackSecret? pendingSecret = null;
        try
        {
            var progress = new Progress<PackBatchProgress>(p =>
            {
                if (IsClosed || generation != _generation || !IsBusy) return;
                CurrentEntry = $"第 {p.GroupIndex + 1}/{p.TotalGroups} 组 · {p.Progress.CurrentEntry ?? "正在完成归档"}";
                IsIndeterminate = p.Progress.State != PackState.Writing || p.Progress.TotalBytes == 0;
                // 百分比明确属于当前组，不用尚未读取的组推算整批完成时间。
                ProgressValue = p.Progress.TotalBytes == 0 ? 0 : Math.Clamp(p.Progress.ReadBytes * 100.0 / p.Progress.TotalBytes, 0, 100);
                Summary = p.Progress.State switch { PackState.Scanning => "正在读取输入清单", PackState.Finalizing => "正在完成当前归档并提交", _ => $"当前组已处理 {p.Progress.EntriesDone} / {p.Progress.TotalEntries} 项" };
            });
            CurrentEntry = "";
            if (retry)
            {
                var session = _session!;
                var work = Task.Run(() => session.RetryFailedAsync(progress, operation.Token));
                _background = work;
                var result = await work;
                if (IsClosed || generation != _generation) return;
                PresentResult(result);
                ClearSecretFields();
                if (!result.CanRetry) ReleaseIdleSession();
                return;
            }

            ReleaseIdleSession(); BatchResult = null; GroupResults.Clear();
            Summary = previewOnly ? "正在准备清单" : "正在创建归档";
            var request = CaptureRequest();
            var cachedPlan = !previewOnly ? _plan : null;
            // 秘密在 UI 线程复制后才进入后台。预览不索取密码；真正执行时两次输入必须完全一致，不做 Trim。
            if (!previewOnly) pendingSecret = CaptureSecret();
            var secret = pendingSecret;
            var workTask = Task.Run(async () =>
            {
                var plan = cachedPlan ?? await _service.PrepareAsync(request, progress, operation.Token).ConfigureAwait(false);
                var needsReview = cachedPlan is null && (request.Grouping == PackGrouping.Separate || plan.ExcludedCount > 0 ||
                    plan.MergedInputs > 0 || plan.Groups.Any(g => g.PreparationError is not null ||
                        g.Plan!.Roots.Any(r => r.EntryName != Path.GetFileName(r.SourcePath))));
                if (previewOnly || needsReview) return (plan, result: (PackBatchResult?)null);
                var session = _service.CreateSession(plan, secret);
                _session = session;
                var result = await session.ExecuteAsync(progress, operation.Token).ConfigureAwait(false);
                return (plan, result: (PackBatchResult?)result);
            });
            _background = workTask;
            var outcome = await workTask;
            // 后台若已移交密码，其寿命由会话控制。关闭排空后台时也能看到并释放该会话。
            if (_session is not null) pendingSecret = null;
            if (IsClosed || generation != _generation) return;
            _plan = revision == _revision ? outcome.plan : null;
            PresentPlan(outcome.plan);
            if (outcome.result is null)
            {
                Summary = "清单已准备，可以开始压缩";
                Message = $"共 {outcome.plan.Groups.Count} 个输出组。请查看来源到归档 的对应关系。";
                if (outcome.plan.MergedInputs > 0) Message += $"合并 {outcome.plan.MergedInputs} 个重复或已包含的输入。";
                if (outcome.plan.ExcludedCount > 0) Message += $"排除 {outcome.plan.ExcludedCount} 项（目录包含后代）。";
                if (request.Grouping == PackGrouping.Separate || outcome.plan.ExcludedCount > 0 || outcome.plan.MergedInputs > 0)
                    DetailsExpanded = true;
                if (outcome.plan.Groups.Any(g => g.PreparationError is not null)) Message += "部分组无法准备，其他独立组仍可开始；原因见清单。";
            }
            else
            {
                _executedPlan = outcome.plan;
                PresentResult(outcome.result);
                ClearSecretFields();
                if (!outcome.result.CanRetry) ReleaseIdleSession();
                // 再次开始属于新任务；失败来源必须重新准备，重试按钮则保留原会话快照。
                if (outcome.result.State != PackBatchState.Completed) _plan = null;
            }
        }
        catch (OperationCanceledException) { if (!IsClosed) { Summary = "已取消"; Message = "输入准备已取消。"; } }
        catch (Exception e) when (e is PackValidationException or PackFailureException or ArgumentException)
        { if (!IsClosed) { _plan = null; Summary = "尚未创建"; Message = e.Message; } }
        catch (Exception) { if (!IsClosed) { _plan = null; Summary = "创建未完成"; Message = "请检查输入和输出位置后重试。"; } }
        finally
        {
            pendingSecret?.Dispose(); ++_generation; _operation = null;
            if (!IsClosed) { IsCancelling = false; IsBusy = false; }
        }
    }

    private void PresentPlan(PackBatchPlan plan)
    {
        RootMappings.Clear(); EntryMappings.Clear(); ExcludedMappings.Clear();
        foreach (var group in plan.Groups)
        {
            if (group.Plan is null) { RootMappings.Add($"{group.SourceLabel} → {group.OutputPath}：{group.PreparationError?.Message}"); continue; }
            foreach (var root in group.Plan.Roots) RootMappings.Add($"{root.SourcePath} → {group.OutputPath} 内 {root.EntryName}{(root.IsDirectory ? "/" : "")}");
            if (group.Plan.Entries.Count == 0) RootMappings.Add($"{group.SourceLabel}：无可打包内容，将跳过");
            foreach (var entry in group.Plan.Entries) EntryMappings.Add($"{Path.GetFileName(group.OutputPath)}：{entry.EntryName}{(entry.IsDirectory ? "/" : "")}");
            foreach (var item in group.Plan.ExcludedItems) ExcludedMappings.Add($"{item.SourcePath} · {item.Reason}");
        }
        OnPropertyChanged(nameof(InputSummary)); OnPropertyChanged(nameof(ExclusionSummary));
    }

    private void PresentResult(PackBatchResult result)
    {
        BatchResult = result; CurrentEntry = ""; GroupResults.Clear();
        Summary = $"成功 {result.CompletedCount} 组 · 失败 {result.FailedCount} 组 · 跳过 {result.SkippedCount} 组 · 取消 {result.CancelledCount} 组";
        Message = $"已提交源内容 {result.SourceBytes:N0} 字节 · 归档 {result.ArchiveBytes:N0} 字节。";
        foreach (var group in result.Groups)
        {
            var item = group.Result;
            var status = item.State switch { PackState.Completed => "已完成", PackState.Skipped => "无可打包内容／已跳过", PackState.Cancelled => "已取消", _ => "失败" };
            var detail = item.Error?.Message ?? (item.State == PackState.Completed ? $"{item.FileCount} 个文件 · {item.ArchiveBytes:N0} 字节" : "未生成归档。");
            if (group.CanRetry) detail += "可重试；沿用原输入、目标和密码。";
            else if (item.State is PackState.Failed or PackState.Cancelled) detail += "请重新选择此来源建立新任务。";
            if (item.CleanupWarning is not null) detail += "临时文件未能清理：" + item.CleanupWarning;
            GroupResults.Add(new(group.SourceLabel, status, detail, item.OutputPath));
        }
    }

    private void ReleaseIdleSession()
    {
        // 仅在无后台操作或后台已完成时调用，因此同步排空不会依赖 UI 线程，也不会丢失成功产物。
        if (_session is null) return;
        _session.DisposeAsync().AsTask().GetAwaiter().GetResult(); _session = null;
        NotifyCommands();
    }
}

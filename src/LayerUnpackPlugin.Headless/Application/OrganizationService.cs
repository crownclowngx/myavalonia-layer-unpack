using System.Diagnostics;
using LayerUnpackPlugin.Headless.Contracts;
using LayerUnpackPlugin.Headless.Infrastructure;

namespace LayerUnpackPlugin.Headless.Application;

/// <summary>两个窄用例足以表达整理闭环；服务无会话状态，取消与并发由调用方各自持有。</summary>
public interface IOrganizationService
{
    Task<OrganizationPlan> PlanAsync(UnpackResult result, string outputParent, OrganizationRules rules, CancellationToken cancellationToken);
    Task<OrganizationResult> ExecuteAsync(OrganizationPlan plan, IProgress<OrganizationProgress>? progress, CancellationToken cancellationToken);
}

public sealed class OrganizationService(OrganizationPlanner planner, IOrganizationCopier copier) : IOrganizationService
{
    public OrganizationService() : this(new OrganizationPlanner(), new OrganizationCopier()) { }
    public Task<OrganizationPlan> PlanAsync(UnpackResult result, string outputParent, OrganizationRules rules, CancellationToken cancellationToken) =>
        planner.CreateAsync(result, outputParent, rules, cancellationToken: cancellationToken);

    public async Task<OrganizationResult> ExecuteAsync(OrganizationPlan plan, IProgress<OrganizationProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(plan.Limits.Timeout);
        var token = timeout.Token;
        OutputTransaction? transaction = null;
        OrganizationResult result;
        long copied = 0; var done = 0; var watch = Stopwatch.StartNew();
        try
        {
            token.ThrowIfCancellationRequested();
            if (plan.FileCount == 0) return new(OrganizationState.NoMatches, null, [], null, null);
            Report("验证来源", null, true);
            await OrganizationFiles.ValidateAsync(plan.Inputs, token).ConfigureAwait(false);
            if (OrganizationFiles.Occupied(plan.OutputDirectory))
                throw new OrganizationFailureException(OrganizationError.OutputConflict, "预览目标已被占用，请重新生成预览；已有文件不会覆盖。");
            var parent = Path.GetDirectoryName(plan.OutputDirectory)!;
            OrganizationFiles.Check(parent);
            var drive = new DriveInfo(Path.GetPathRoot(parent)!);
            if (drive.AvailableFreeSpace < plan.TotalBytes)
                throw new OrganizationFailureException(OrganizationError.OutputError, "目标磁盘可用空间不足，请更换位置后重新预览。");
            transaction = new OutputTransaction(parent);
            foreach (var mapping in plan.Mappings)
            {
                token.ThrowIfCancellationRequested();
                var target = OrganizationFiles.EntryPath(transaction.StagingDirectory, mapping.TargetRelativePath, mapping.IsDirectory);
                if (mapping.IsDirectory) Directory.CreateDirectory(target);
                else
                {
                    await copier.CopyAsync(mapping, target, bytes => { copied += bytes; Report("复制文件", mapping.TargetRelativePath, false); }, token).ConfigureAwait(false);
                    done++; Report("复制文件", mapping.TargetRelativePath, false);
                }
            }
            Report("核对并提交", null, true);
            // 提交前重新核对所有源条目，包括筛选外条目与目录成员，避免压平依据在复制期间失效。
            await OrganizationFiles.ValidateAsync(plan.Inputs, token).ConfigureAwait(false);
            var entries = await CommittedManifest.CaptureAsync(transaction.StagingDirectory, plan.Limits.MaxEntries, token).ConfigureAwait(false);
            var actual = entries.ToDictionary(e => e.RelativePath, StringComparer.Ordinal);
            if (actual.Count != plan.Mappings.Count || plan.Mappings.Any(m => !actual.TryGetValue(m.TargetRelativePath, out var e) ||
                    e.IsDirectory != m.IsDirectory || e.Length != m.Length || e.Sha256 != m.Sha256))
                throw new OrganizationFailureException(OrganizationError.OutputError, "暂存结果与预览映射不一致，已拒绝提交。");
            if (OrganizationFiles.Occupied(plan.OutputDirectory))
                throw new OrganizationFailureException(OrganizationError.OutputConflict, "预览目标在复制期间被占用，请重新预览。");
            var output = transaction.CommitExact(Path.GetFileName(plan.OutputDirectory), token);
            result = new(OrganizationState.Completed, output, entries, null, null)
            {
                SourceGroups = Array.AsReadOnly(plan.Sources.Select(s => new OrganizationOutputSource(s.ArchivePath, s.TargetName)).ToArray()),
                SourceWarnings = plan.SourceWarnings
            };
            Report("已完成", null, true);
        }
        catch (OperationCanceledException)
        {
            result = cancellationToken.IsCancellationRequested ? new(OrganizationState.Cancelled, null, [], null, null)
                : new(OrganizationState.Failed, null, [], new(OrganizationError.Timeout, "整理超过时间预算，请减少内容后重新预览。"), null);
        }
        catch (Exception e) { result = new(OrganizationState.Failed, null, [], OrganizationFiles.Diagnostic(e), null); }
        // 所有出口都等待同步清理完成才返回；已提交目录不会回滚，清理失败明确保留所属暂存路径。
        return result with { CleanupWarning = transaction?.Rollback() };

        void Report(string phase, string? path, bool force)
        {
            if (!force && watch.ElapsedMilliseconds < 100) return;
            watch.Restart();
            try { progress?.Report(new(phase, done, plan.FileCount, copied, plan.TotalBytes, path)); } catch { /* 观察者不参与复制事务。 */ }
        }
    }
}

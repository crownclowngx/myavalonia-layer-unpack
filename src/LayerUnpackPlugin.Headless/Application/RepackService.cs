using LayerUnpackPlugin.Headless.Contracts;
using LayerUnpackPlugin.Headless.Domain;
using LayerUnpackPlugin.Headless.Infrastructure;

namespace LayerUnpackPlugin.Headless.Application;

public interface IRepackService
{
    Task<RepackPlan> PrepareAsync(UnpackResult input, string outputDirectory, PackGrouping grouping, PackOptions options,
        CancellationToken cancellationToken);
    Task<RepackPlan> PrepareAsync(OrganizationResult input, string outputDirectory, PackGrouping grouping, PackOptions options,
        CancellationToken cancellationToken);
    Task<RepackResult> ConvertAsync(ConversionRequest request, IEnumerable<string>? sourcePasswords = null, PackSecret? targetSecret = null,
        IProgress<RepackProgress>? progress = null, CancellationToken cancellationToken = default);
    Task<RepackResult> ExecuteAsync(RepackPlan plan, PackSecret? targetSecret = null,
        IProgress<RepackProgress>? progress = null, CancellationToken cancellationToken = default);
}

/// <summary>有限复合用例：每个来源依次读取、生成映射、写包并独立提交；服务不持有会话状态。
/// 来源失败保留独立结果，预算耗尽和取消停止后续调度。只有本次创建的暂存事务可被清理。</summary>
public sealed class RepackService(IArchiveExtractor extractor, CommittedPackPlanner planner, IRepackWriter writer) : IRepackService
{
    public RepackService() : this(new ArchiveExtractor(), new CommittedPackPlanner(), new RepackWriter(new PackPlanner(), new ZipArchiveWriter())) { }
    public Task<RepackPlan> PrepareAsync(UnpackResult input, string outputDirectory, PackGrouping grouping, PackOptions options, CancellationToken cancellationToken) =>
        planner.PrepareAsync(input, outputDirectory, grouping, options, cancellationToken: cancellationToken);
    public Task<RepackPlan> PrepareAsync(OrganizationResult input, string outputDirectory, PackGrouping grouping, PackOptions options, CancellationToken cancellationToken) =>
        planner.PrepareAsync(input, outputDirectory, grouping, options, cancellationToken: cancellationToken);

    public async Task<RepackResult> ConvertAsync(ConversionRequest request, IEnumerable<string>? sourcePasswords = null,
        PackSecret? targetSecret = null, IProgress<RepackProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request); request.Limits.Validate(); CommittedPackPlanner.ValidateOptions(request.Options);
        ValidateSecret(request.Options, targetSecret);
        if (!Enum.IsDefined(request.Mode) || !Enum.IsDefined(request.LegacyNameEncoding) || !Enum.IsDefined(request.Rules.FileTypes) ||
            request.MaxDepth is < 1 or > UnpackLimits.DepthCeiling)
            throw new PackValidationException("转换模式、层数、编码或整理规则无效。");
        if (request.Mode == ConversionMode.FormatOnly && request.Rules != new OrganizationRules())
            throw new PackValidationException("普通格式转换保留全部结构；使用整理规则时请显式选择展开整理模式。");
        var output = OrganizationFiles.Absolute(request.OutputDirectory);
        if (File.Exists(output)) throw new PackValidationException("目标位置必须是目录。");
        var sources = request.Sources.Select(OrganizationFiles.Absolute).Distinct(OrganizationFiles.Comparer).ToArray();
        if (sources.Length == 0 || sources.Length > request.Limits.Unpack.MaxArchives || sources.Length > request.Limits.Pack.MaxInputs)
            throw new PackValidationException("请添加来源，且来源数量不能超过整项任务上限。");
        var passwords = (sourcePasswords ?? []).ToArray();
        using var passwordValidation = new PasswordPool(); passwordValidation.Add(passwords);
        var budget = new RepackBudget(request.Limits);
        // 所有来源及递归节点共用同一个解压账本，不能按来源重置字节、条目、尝试和节点预算。
        var unpackBudget = new ExecutionBudget(request.Limits.Unpack, budget.AddExpandedBytes);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(request.Limits.Timeout);
        var groups = new List<RepackGroupResult>(); var cleanup = new List<string>(); var stopped = false;
        for (var index = 0; index < sources.Length; index++)
        {
            var source = sources[index]; var target = Path.Combine(output, ArchiveProbe.OutputName(source) + ".zip");
            if (stopped || timeout.IsCancellationRequested)
            { groups.Add(Stopped(source, target, cancellationToken, timeout.Token)); continue; }
            OutputTransaction? workspace = null;
            try
            {
                Report(RepackPhase.Reading);
                timeout.Token.ThrowIfCancellationRequested();
                workspace = new OutputTransaction(output);
                var unpackRequest = new UnpackRequest([source], workspace.StagingDirectory,
                    request.Mode == ConversionMode.FormatOnly ? 1 : request.MaxDepth, passwords, request.Limits.Unpack, request.LegacyNameEncoding);
                await using var session = new UnpackSession(unpackRequest, extractor, unpackBudget, request.Mode != ConversionMode.FormatOnly);
                var unpacked = await session.ExecuteAsync(new Relay<UnpackProgress>(_ => Report(RepackPhase.Reading)), timeout.Token).ConfigureAwait(false);
                stopped |= unpacked.RetryBlocked;
                timeout.Token.ThrowIfCancellationRequested();
                if (unpacked.State != BatchState.Completed)
                {
                    var error = unpacked.Nodes.Select(n => n.Error).OfType<UnpackDiagnostic>().FirstOrDefault();
                    groups.Add(new(source, target, RepackState.Failed, null, 0, 0, [],
                        new("读取来源", error?.Code.ToString() ?? "IncompleteSource", error?.Message ?? "来源未完整完成选定层数，不生成表面完整的新包。")));
                    continue;
                }
                Report(RepackPhase.Planning);
                var plan = await planner.PrepareAsync(unpacked, output, PackGrouping.Separate, request.Options, request.Limits,
                    request.Mode == ConversionMode.FormatOnly ? new() : request.Rules, timeout.Token).ConfigureAwait(false);
                if (plan.Groups.Count == 0)
                { groups.Add(new(source, target, RepackState.Skipped, null, 0, 0, [], null)); continue; }
                var group = plan.Groups.Single();
                var result = await WriteAsync(group, budget, targetSecret, index, sources.Length, progress, timeout.Token).ConfigureAwait(false);
                groups.Add(NormalizeCancellation(result.Group, cancellationToken, timeout.Token));
                if (result.Cleanup is not null) cleanup.Add(result.Cleanup);
                stopped |= budget.Exhausted;
            }
            catch (Exception e)
            {
                groups.Add(Failure(source, target, e, cancellationToken, timeout.Token));
                stopped |= budget.Exhausted || e is UnpackFailureException { StopBatch: true };
            }
            finally
            {
                Report(RepackPhase.Cleaning);
                // 内层“提交”只落到本事务私有目录；外层从不提交工作区，所以成功、失败和取消都清理中间内容。
                // ZIP 最终产物位于工作区之外，回滚不会碰到它们，也不会碰用户已有解压目录。
                var residue = workspace?.Rollback();
                if (residue is not null) { cleanup.Add(residue); stopped = true; }
            }
            void Report(RepackPhase phase) => Publish(progress, new(index, sources.Length, source, phase, budget.Usage));
        }
        return new(groups, budget.Usage, cleanup);
    }

    public async Task<RepackResult> ExecuteAsync(RepackPlan plan, PackSecret? targetSecret = null,
        IProgress<RepackProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan); plan.Limits.Validate();
        foreach (var group in plan.Groups) ValidateSecret(group.Plan.Request.Options, targetSecret);
        var budget = new RepackBudget(plan.Limits);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(plan.Limits.Timeout);
        var groups = new List<RepackGroupResult>(); var cleanup = new List<string>();
        for (var index = 0; index < plan.Groups.Count; index++)
        {
            var group = plan.Groups[index];
            if (budget.Exhausted || timeout.IsCancellationRequested || cleanup.Count > 0)
            { groups.Add(Stopped(group.Source, group.Plan.Request.OutputPath, cancellationToken, timeout.Token)); continue; }
            try
            {
                var result = await WriteAsync(group, budget, targetSecret, index, plan.Groups.Count, progress, timeout.Token).ConfigureAwait(false);
                groups.Add(NormalizeCancellation(result.Group, cancellationToken, timeout.Token));
                if (result.Cleanup is not null) cleanup.Add(result.Cleanup);
            }
            catch (Exception e) { groups.Add(Failure(group.Source, group.Plan.Request.OutputPath, e, cancellationToken, timeout.Token)); }
        }
        return new(groups, budget.Usage, cleanup);
    }

    private async Task<(RepackGroupResult Group, string? Cleanup)> WriteAsync(RepackGroupPlan group, RepackBudget budget,
        PackSecret? secret, int index, int count, IProgress<RepackProgress>? progress, CancellationToken token)
    {
        var result = await writer.ExecuteAsync(group.Plan, budget, secret,
            new Relay<PackProgress>(p => Report(RepackPhase.Writing, p.CurrentEntry)), () => Report(RepackPhase.Verifying), token).ConfigureAwait(false);
        var state = result.State switch
        {
            PackState.Completed => group.Warnings.Count > 0 ? RepackState.CompletedWithWarnings : RepackState.Completed,
            PackState.Cancelled => RepackState.Cancelled,
            PackState.Skipped => RepackState.Skipped,
            _ => RepackState.Failed
        };
        if (result.State == PackState.Completed) Report(RepackPhase.Committed);
        return (new(group.Source, group.Plan.Request.OutputPath, state, result.OutputPath, result.FileCount, result.ArchiveBytes,
            group.Warnings, result.Error is null ? null : new("创建目标", result.Error.Code.ToString(), result.Error.Message)), result.CleanupWarning);
        void Report(RepackPhase phase, string? entry = null) => Publish(progress, new(index, count, group.Source, phase, budget.Usage, entry));
    }

    private static void ValidateSecret(PackOptions options, PackSecret? secret)
    {
        if (options.Encrypt != (secret is not null)) throw new PackValidationException("目标加密开关与目标密码必须一致；来源密码不能代替目标密码。");
        if (secret is not null) _ = secret.Password;
    }
    private static RepackGroupResult NormalizeCancellation(RepackGroupResult group, CancellationToken caller, CancellationToken timeout) =>
        group.State == RepackState.Cancelled && !caller.IsCancellationRequested && timeout.IsCancellationRequested
            ? group with { State = RepackState.Failed, Error = new("复合任务", "Timeout", "整项任务超过时间预算，已提交 ZIP 保留。") } : group;
    private static RepackGroupResult Stopped(string source, string target, CancellationToken caller, CancellationToken timeout) =>
        caller.IsCancellationRequested ? new(source, target, RepackState.Cancelled, null, 0, 0, [], null) :
            new(source, target, RepackState.NotRun, null, 0, 0, [], new("复合任务", timeout.IsCancellationRequested ? "Timeout" : "Stopped", "整项任务已停止，此来源未执行。"));
    private static RepackGroupResult Failure(string source, string target, Exception e, CancellationToken caller, CancellationToken timeout)
    {
        if (caller.IsCancellationRequested) return new(source, target, RepackState.Cancelled, null, 0, 0, [], null);
        var diagnostic = timeout.IsCancellationRequested ? new RepackDiagnostic("复合任务", "Timeout", "整项任务超过时间预算，已提交 ZIP 保留。") : e switch
        {
            UnpackFailureException f => new("读取来源", f.Code.ToString(), f.Message),
            OrganizationFailureException f => new("生成清单", f.Code.ToString(), f.Message),
            PackFailureException f => new("创建目标", f.Code.ToString(), f.Message),
            _ => new("复合任务", "OutputError", "转换或打包未完成，请检查来源、输出位置和访问权限。")
        };
        return new(source, target, RepackState.Failed, null, 0, 0, [], diagnostic);
    }
    private static void Publish(IProgress<RepackProgress>? progress, RepackProgress value)
    { try { progress?.Report(value); } catch { /* 观察者不属于事务；异常不能改变提交事实。 */ } }
    private sealed class Relay<T>(Action<T> action) : IProgress<T> { public void Report(T value) => action(value); }
}

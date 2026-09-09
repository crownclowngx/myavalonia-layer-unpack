using LayerUnpackPlugin.Headless.Contracts;
using LayerUnpackPlugin.Headless.Infrastructure;

namespace LayerUnpackPlugin.Headless.Application;

public interface IPackBatchService
{
    Task<PackBatchPlan> PrepareAsync(PackBatchRequest request, IProgress<PackBatchProgress>? progress = null, CancellationToken cancellationToken = default);
    PackBatchSession CreateSession(PackBatchPlan plan, PackSecret? secret = null);
}

/// <summary>外层仅分组与分配目标，真实清单和提交继续委托单包用例；按规范路径顺序逐组准备、逐组执行。</summary>
public sealed class PackBatchService(IPackService service) : IPackBatchService
{
    public PackBatchService() : this(new PackService()) { }

    public async Task<PackBatchPlan> PrepareAsync(PackBatchRequest request, IProgress<PackBatchProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Limits.Validate();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(request.Limits.Timeout);
        try { return await PrepareCoreAsync(request, progress, timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new PackFailureException(PackError.Timeout, "批次输入准备超过时间上限，尚未创建任何 ZIP。"); }
    }

    private async Task<PackBatchPlan> PrepareCoreAsync(PackBatchRequest request, IProgress<PackBatchProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Limits.Validate(); request.Options.Validate();
        if (!Enum.IsDefined(request.Grouping) || string.IsNullOrWhiteSpace(request.OutputDirectory) || !Path.IsPathFullyQualified(request.OutputDirectory))
            throw new PackValidationException("请选择完整输出目录和有效的分组方式。");
        var output = Path.GetFullPath(request.OutputDirectory);
        PackPaths.Check(output);
        var selection = PackInputSelection.Resolve(request.Inputs, request.Limits);
        if (request.Grouping == PackGrouping.Combined && (string.IsNullOrWhiteSpace(request.ArchiveName) || request.ArchiveName != Path.GetFileName(request.ArchiveName)))
            throw new PackValidationException("名称只能是一个归档文件名，位置请填写在输出文件夹中。");
        // 多事务写入若落在任一源树中，会改变其他组已经冻结的清单。
        // 分别模式要求外部输出位置，把这个边界在准备时说明，避免凭临时文件名忽略用户资料。
        if (request.Grouping == PackGrouping.Separate && selection.Roots.Any(r => r.IsDirectory &&
            (PackPaths.Equal(r.SourcePath, output) || PathPolicy.IsWithin(r.SourcePath, output))))
            throw new PackValidationException("分别打包请选择所有源文件夹之外的输出位置，避免批次产物改变已确认的输入。");

        var groupInputs = request.Grouping == PackGrouping.Combined ? new[] { request.Inputs.ToArray() } : selection.Roots.Select(r => new[] { r.SourcePath }).ToArray();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var groups = new List<PackGroupPlan>();
        long totalBytes = 0; long totalEntries = 0;
        for (var i = 0; i < groupInputs.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = request.Grouping == PackGrouping.Combined ? request.ArchiveName : AllocateName(selection.Roots[i], output, used, ArchiveCapabilities.For(request.Options.Format).Extension, cancellationToken);
            var target = Path.Combine(output, name);
            if (request.Grouping == PackGrouping.Separate && selection.Sources.Any(p => PackPaths.Equal(p, target)))
                throw new PackValidationException("批次输出不能同时作为选中的来源，请选择其他输出位置。");
            var label = request.Grouping == PackGrouping.Combined ? "全部输入" : selection.Roots[i].SourcePath;
            PackPlan? plan = null; PackDiagnostic? failure = null;
            try
            {
                var index = i;
                var relay = new PackProgressRelay(p => progress?.Report(new(index, groupInputs.Length, label, p)));
                plan = await service.PrepareAsync(new(groupInputs[i], target, request.Limits, request.Options), relay, cancellationToken).ConfigureAwait(false);
            }
            catch (PackFailureException e) { failure = new(e.Code, e.Message); }
            cancellationToken.ThrowIfCancellationRequested();
            totalBytes += plan?.TotalBytes ?? 0;
            totalEntries += (plan?.Entries.Count ?? 0) + (plan?.ExcludedItems.Count ?? 0);
            if (totalBytes > request.Limits.MaxTotalBytes || totalEntries > request.Limits.MaxEntries)
                throw new PackFailureException(PackError.BudgetExceeded, "整个批次的来源字节或清单条目超过上限，请减少输入后重新准备。");
            // 请求格式是批次级错误；来源名称等安全问题由 PackFailureException 限定在该组。
            groups.Add(new(i, label, target, plan, failure));
        }
        return new(request, groups, request.Inputs.Count - selection.Roots.Length);
    }

    private static string AllocateName(PackRoot root, string directory, HashSet<string> used, string extension, CancellationToken token)
    {
        var stem = root.IsDirectory ? root.EntryName : Path.GetFileNameWithoutExtension(root.EntryName);
        if (string.IsNullOrWhiteSpace(stem)) stem = "资料";
        if (stem.Length > 150) stem = stem[..150];
        for (var i = 0; i < 10_000; i++)
        {
            token.ThrowIfCancellationRequested();
            var name = i == 0 ? stem + extension : $"{stem} ({i}){extension}";
            if (!used.Contains(name) && !File.Exists(Path.Combine(directory, name)) && !Directory.Exists(Path.Combine(directory, name)))
            { used.Add(name); return name; }
        }
        throw new PackValidationException("无法为批次分配不冲突的归档名称。");
    }

    /// <summary>成功创建会话后，密码对象的释放责任转移给会话；每个会话需提供独立秘密对象。</summary>
    public PackBatchSession CreateSession(PackBatchPlan plan, PackSecret? secret = null) => new(service, plan, secret);
}

/// <summary>同步转发业务进度；线程切换由 GUI 的 Progress 负责，防止后台队列打乱取消与组序。</summary>
internal sealed class PackProgressRelay(Action<PackProgress> report) : IProgress<PackProgress>
{
    public void Report(PackProgress value) => report(value);
}

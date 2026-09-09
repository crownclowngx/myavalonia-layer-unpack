using System.Text.Json;
using LayerUnpackPlugin.Headless.Application;
using LayerUnpackPlugin.Headless.Contracts;
using MyAvaloniaManagement.PluginSdk;

namespace LayerUnpackPlugin.Workflow;

/// <summary>创建动作只做参数翻译、业务调用与结果投影，分组、源摘要和不覆盖提交完全复用 GUI 用例。</summary>
public sealed class CreateArchiveWorkflowAction(IPackBatchService service) : IWorkflowActionHandler
{
    public async ValueTask<JsonElement> InvokeAsync(JsonElement arguments, WorkflowActionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        if (!ArchiveWorkflowInput.IsValid(arguments, ArchiveWorkflowActions.Create))
            return ArchiveWorkflowResult.Empty(context, "invalid-arguments");
        try
        {
            var inputs = ArchiveWorkflowInput.Inputs(arguments);
            // 上游没有成功文件是正常跳过，不建立空任务或猜测目录。空数组仍须通过完整参数结构校验。
            if (inputs.Length == 0) return ArchiveWorkflowResult.Empty(context, "no-inputs", skipped: true);
            var options = new PackOptions
            {
                Format = ArchiveWorkflowInput.Text(arguments, "format") switch { "tar" => PackFormat.Tar, "tar.gz" => PackFormat.TarGZip, _ => PackFormat.Zip },
                Compression = ArchiveWorkflowInput.Text(arguments, "compression") switch
                { "fast" => PackCompression.Fast, "high" => PackCompression.High, "store" => PackCompression.Store, _ => PackCompression.Standard }
            };
            var request = new PackBatchRequest(inputs, ArchiveWorkflowInput.Text(arguments, "outputDirectory"),
                ArchiveWorkflowInput.Text(arguments, "archiveName"), ArchiveWorkflowInput.Text(arguments, "grouping") == "separate" ? PackGrouping.Separate : PackGrouping.Combined,
                options, new PackLimits { MaxInputs = ArchiveWorkflowActions.MaximumItems });
            var progress = new ArchiveWorkflowProgress<PackBatchProgress>(context, "creating");
            var plan = await service.PrepareAsync(request, progress, cancellationToken).ConfigureAwait(false);
            await using var session = service.CreateSession(plan);
            var result = await session.ExecuteAsync(progress, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (result.State == PackBatchState.Cancelled) throw new OperationCanceledException(cancellationToken);
            return ArchiveWorkflowResult.From(result, context);
        }
        catch (PackValidationException) { return ArchiveWorkflowResult.Empty(context, "invalid-request"); }
        catch (PackFailureException e) { return ArchiveWorkflowResult.Empty(context, e.Code.ToString()); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        { return ArchiveWorkflowResult.Empty(context, "invalid-request"); }
    }
}

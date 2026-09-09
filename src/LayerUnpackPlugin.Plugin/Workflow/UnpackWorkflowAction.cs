using System.Text.Json;
using LayerUnpackPlugin.Headless.Application;
using LayerUnpackPlugin.Headless.Contracts;
using MyAvaloniaManagement.PluginSdk;

namespace LayerUnpackPlugin.Workflow;

/// <summary>
/// 解压适配器仅依赖解压用例。每次调用创建、等待并释放自己的会话，不依赖 DocumentLifetime 或 UI。
/// 工作流暂停补密没有已验证的独立秘密通道，因此只执行无密码尝试，缺密由节点错误码表达。
/// </summary>
public sealed class UnpackWorkflowAction(IUnpackService service) : IWorkflowActionHandler
{
    public async ValueTask<JsonElement> InvokeAsync(JsonElement arguments, WorkflowActionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        if (!ArchiveWorkflowInput.IsValid(arguments, ArchiveWorkflowActions.Unpack))
            return ArchiveWorkflowResult.Empty(context, "invalid-arguments");
        try
        {
            var encoding = ArchiveWorkflowInput.Text(arguments, "nameEncoding") switch
            { "utf8" => LegacyNameEncoding.Utf8, "cp437" => LegacyNameEncoding.Cp437, "cp866" => LegacyNameEncoding.Cp866, _ => LegacyNameEncoding.Gb18030 };
            await using var session = service.CreateSession(new(ArchiveWorkflowInput.Inputs(arguments),
                ArchiveWorkflowInput.Text(arguments, "outputDirectory"), arguments.GetProperty("maxDepth").GetInt32(),
                limits: new UnpackLimits { MaxArchives = ArchiveWorkflowActions.MaximumItems }, legacyNameEncoding: encoding));
            var result = await session.ExecuteAsync(new ArchiveWorkflowProgress<UnpackProgress>(context, "unpacking"), cancellationToken).ConfigureAwait(false);
            // Headless 用结构化取消终态收口；SDK 用取消异常收口。转换发生在会话排空之后，防止宿主误报成功。
            cancellationToken.ThrowIfCancellationRequested();
            if (result.State == BatchState.Cancelled) throw new OperationCanceledException(cancellationToken);
            return ArchiveWorkflowResult.From(result, context);
        }
        catch (UnpackValidationException) { return ArchiveWorkflowResult.Empty(context, "invalid-request"); }
        catch (UnpackFailureException e) { return ArchiveWorkflowResult.Empty(context, e.Code.ToString()); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        { return ArchiveWorkflowResult.Empty(context, "invalid-request"); }
    }
}

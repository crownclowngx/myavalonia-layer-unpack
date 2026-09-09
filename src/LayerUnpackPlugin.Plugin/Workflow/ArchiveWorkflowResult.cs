using System.Text.Json;
using LayerUnpackPlugin.Headless.Contracts;
using MyAvaloniaManagement.PluginSdk;
using MyAvaloniaManagement.PluginSdk.Workflow;

namespace LayerUnpackPlugin.Workflow;

/// <summary>
/// 把内部结果投影为稳定白名单。诊断只传错误码，不透传异常、候选密码、清理临时路径或可变会话。
/// 成功清单仅来自实际提交；计划路径、失败与深度停止项都不能混进下游输入。
/// </summary>
internal static class ArchiveWorkflowResult
{
    internal sealed record Item(string Id, string ParentId, string SourcePath, int Depth, string State, string DiagnosticCode, bool CleanupRequired);
    internal sealed record Output(string Id, string Path, string Kind);
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    internal static JsonElement From(UnpackResult result, WorkflowActionContext context)
    {
        var items = result.Nodes.Select(n => new Item(n.Id.ToString(), n.ParentId?.ToString() ?? "", n.SourcePath, n.Depth,
            n.State switch { NodeState.Extracted => "completed", NodeState.Failed => "failed", NodeState.DepthLimit => "skipped", _ => "not-run" },
            n.Error?.Code.ToString() ?? (n.State == NodeState.DepthLimit ? "depth-limit" : ""), n.CleanupWarnings.Count > 0)).ToArray();
        var outputs = result.Nodes.Where(n => n.State == NodeState.Extracted && n.OutputDirectory is not null)
            .Select(n => new Output(n.Id.ToString(), n.OutputDirectory!, "directory")).ToArray();
        return Build(context, result.State switch { BatchState.Completed => "completed", BatchState.PartialFailure => "partial-failure", _ => "failed" },
            "", items, outputs);
    }

    internal static JsonElement From(PackBatchResult result, WorkflowActionContext context) => Build(context,
        result.State switch { PackBatchState.Completed => "completed", PackBatchState.PartiallyCompleted => "partial-failure", PackBatchState.Skipped => "skipped", _ => "failed" }, "",
        result.Groups.Select(g => new Item(g.Index.ToString(System.Globalization.CultureInfo.InvariantCulture), "", g.SourceLabel, 0,
            g.Result.State switch { PackState.Completed => "completed", PackState.Skipped => "skipped", PackState.Failed => "failed", _ => "not-run" },
            g.Result.Error?.Code.ToString() ?? "", g.Result.CleanupWarning is not null)).ToArray(),
        result.Groups.Where(g => g.Result.State == PackState.Completed && g.Result.OutputPath is not null)
            .Select(g => new Output(g.Index.ToString(System.Globalization.CultureInfo.InvariantCulture), g.Result.OutputPath!, "archive")).ToArray());

    internal static JsonElement Empty(WorkflowActionContext context, string code, bool skipped = false) =>
        Build(context, skipped ? "skipped" : "failed", code, [], []);

    private static JsonElement Build(WorkflowActionContext context, string state, string code, Item[] items, Output[] outputs)
    {
        var counts = new
        {
            completed = outputs.Length,
            failed = items.Count(i => i.State == "failed"),
            skipped = items.Count(i => i.State == "skipped"),
            notRun = items.Count(i => i.State == "not-run")
        };
        var take = items.Length;
        while (true)
        {
            var visible = items.Take(take).ToArray();
            var ids = visible.Select(i => i.Id).ToHashSet(StringComparer.Ordinal);
            var truncated = take < items.Length;
            var json = JsonSerializer.SerializeToElement(new
            {
                contract = ArchiveWorkflowActions.Contract,
                version = 1,
                invocationId = context.InvocationId.ToString(),
                state = truncated ? "partial-failure" : state,
                diagnosticCode = truncated ? "result-budget-exceeded" : code,
                detailsTruncated = truncated,
                counts,
                successfulOutputs = outputs.Where(o => ids.Contains(o.Id)).ToArray(),
                items = visible
            }, JsonOptions);
            if (new WorkflowSchemaValidator().ValidateInstance(ArchiveWorkflowActions.Unpack.OutputSchema, json,
                WorkflowSchemaProfile.MaximumOutputBytes).IsValid) return json;
            // 极长路径可能撑破宿主的一 MiB 输出预算。按完整项裁剪并保留全量计数，明确标记结果不完整；
            // 不返回半个路径，也不因序列化超限把已经提交的整批产物伪装成毫无输出的 Host 错误。
            take--;
        }
    }
}

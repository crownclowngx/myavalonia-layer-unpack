using System.Text.Json;
using MyAvaloniaManagement.PluginSdk;
using MyAvaloniaManagement.PluginSdk.Workflow;

namespace LayerUnpackPlugin.Workflow;

/// <summary>直接 SDK 调用也执行与宿主相同的结构校验；路径可用性仍由实际 Headless 读取时复验。</summary>
internal static class ArchiveWorkflowInput
{
    internal static bool IsValid(JsonElement arguments, WorkflowActionDescriptor descriptor) =>
        new WorkflowSchemaValidator().ValidateInstance(descriptor.InputSchema, arguments, WorkflowSchemaProfile.MaximumInputBytes).IsValid;
    internal static string Text(JsonElement arguments, string name) => arguments.GetProperty(name).GetString()!;
    internal static string[] Inputs(JsonElement arguments) => arguments.GetProperty("inputs").EnumerateArray().Select(p => p.GetString()!).ToArray();
}

/// <summary>同步白名单进度，不回显源路径或诊断正文；观察者异常不能改变已提交的业务事实。</summary>
internal sealed class ArchiveWorkflowProgress<T>(WorkflowActionContext context, string stage) : IProgress<T>
{
    public void Report(T value)
    {
        try { context.Progress.Report(new(stage, null, "正在执行归档操作。")); }
        catch { /* 进度接收方不是文件事务的参与者。 */ }
    }
}

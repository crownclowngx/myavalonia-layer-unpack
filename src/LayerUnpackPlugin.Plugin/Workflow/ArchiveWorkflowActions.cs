using System.Text.Json;
using MyAvaloniaManagement.PluginSdk;

namespace LayerUnpackPlugin.Workflow;

/// <summary>
/// 归档动作的公开 JSON 契约目录。这里只声明协议，不持有文件、会话或引擎。
/// v1 固定在动作标识中；破坏性变化必须另建标识，避免旧定义重新打开后改变执行含义。
/// </summary>
public static class ArchiveWorkflowActions
{
    public const string Contract = "myavalonia.layer-unpack.workflow-result";
    public const int MaximumItems = 64;
    public const string UnpackId = "myavalonia.plugin.layer.unpack.workflow.unpack-v1";
    public const string CreateId = "myavalonia.plugin.layer.unpack.workflow.create-v1";

    public static WorkflowActionDescriptor Unpack { get; } = Describe(UnpackId, "解压归档（无密码）", Object(
        ("version", Version()), ("inputs", Array(PathSchema(), 1)), ("outputDirectory", PathSchema()),
        ("maxDepth", Integer(1, 16)), ("nameEncoding", Choice("gb18030", "utf8", "cp437", "cp866")),
        ("repeatPolicy", Choice("create-new"))));

    public static WorkflowActionDescriptor Create { get; } = Describe(CreateId, "创建归档（无密码）", Object(
        ("version", Version()), ("inputs", Array(PathSchema(), 0)), ("outputDirectory", PathSchema()),
        ("archiveName", Text(1, 255)), ("grouping", Choice("combined", "separate")),
        ("format", Choice("zip", "tar", "tar.gz")), ("compression", Choice("standard", "fast", "high", "store")),
        ("repeatPolicy", Choice("create-new"))));

    private static WorkflowActionDescriptor Describe(string id, string name, object input) => new(new(id), name,
        "复用普通页面的 Headless 用例。返回业务 state、逐项诊断及 successfulOutputs；调用完成不代表整批成功。" +
        "create-new 表示每次调用重新创建并自动编号，永不覆盖，也不承诺幂等；请关闭调度器自动重试。无加密参数。",
        JsonSerializer.SerializeToElement(input), JsonSerializer.SerializeToElement(ResultSchema()),
        WorkflowActionRiskFlags.ReadsLocalFiles | WorkflowActionRiskFlags.WritesLocalFiles | WorkflowActionRiskFlags.LongRunning,
        WorkflowActionConfirmationPolicy.OncePerRun);

    private static object ResultSchema() => Object(
        ("contract", Choice(Contract)), ("version", Version()), ("invocationId", Text(36, 36)),
        ("state", Choice("completed", "partial-failure", "failed", "skipped")),
        ("diagnosticCode", Text(0, 80)), ("detailsTruncated", new { type = "boolean" }),
        ("counts", Object(("completed", Integer(0, MaximumItems)), ("failed", Integer(0, MaximumItems)),
            ("skipped", Integer(0, MaximumItems)), ("notRun", Integer(0, MaximumItems)))),
        ("successfulOutputs", Array(Object(("id", Text(1, 36)), ("path", PathSchema()), ("kind", Choice("directory", "archive"))), 0)),
        ("items", Array(Object(("id", Text(1, 36)), ("parentId", Text(0, 36)), ("sourcePath", Text(0, 32767)),
            ("depth", Integer(0, 17)), ("state", Choice("completed", "failed", "skipped", "not-run")),
            ("diagnosticCode", Text(0, 80)), ("cleanupRequired", new { type = "boolean" })), 0)));

    // 朴素的 Schema 构造器只消除重复声明；所有对象封闭且字段必需，保证 Studio 能静态验证结果引用。
    private static object Object(params (string Name, object Schema)[] fields) => new
    {
        type = "object",
        properties = fields.ToDictionary(f => f.Name, f => f.Schema),
        required = fields.Select(f => f.Name).ToArray(),
        additionalProperties = false
    };
    private static object Array(object item, int minimum) => new { type = "array", items = item, minItems = minimum, maxItems = MaximumItems };
    private static object Text(int minimum, int maximum) => new { type = "string", minLength = minimum, maxLength = maximum };
    private static object PathSchema() => Text(1, 32767);
    private static object Integer(int minimum, int maximum) => new { type = "integer", minimum, maximum };
    private static object Version() => new { type = "integer", @enum = new[] { 1 } };
    private static object Choice(params string[] values) => new { type = "string", @enum = values };
}

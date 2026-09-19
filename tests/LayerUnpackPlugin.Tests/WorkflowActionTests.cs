using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using LayerUnpackPlugin.Headless.Application;
using LayerUnpackPlugin.Headless.Contracts;
using LayerUnpackPlugin.Headless.Tests;
using LayerUnpackPlugin.Workflow;
using MyAvaloniaManagement.PluginSdk;
using MyAvaloniaManagement.PluginSdk.Workflow;
using Xunit;
using Avalonia.Headless.XUnit;
using LayerUnpackPlugin.Features.Unpack;

namespace LayerUnpackPlugin.Tests;

/// <summary>真实文件验证适配结果；故障替身仅控制取消时间点，不能代替归档格式的回读证据。</summary>
public sealed class WorkflowActionTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    internal static JsonElement UnpackArgs(IEnumerable<string> inputs, string output, int depth = 1) => JsonSerializer.SerializeToElement(new
    { version = 1, inputs, outputDirectory = output, maxDepth = depth, nameEncoding = "gb18030", repeatPolicy = "create-new" });
    internal static JsonElement CreateArgs(IEnumerable<string> inputs, string output, string grouping = "combined", string format = "zip", string compression = "standard") => JsonSerializer.SerializeToElement(new
    { version = 1, inputs, outputDirectory = output, archiveName = "交付." + format, grouping, format, compression, repeatPolicy = "create-new" });
    internal static WorkflowActionContext Context(IProgress<WorkflowActionProgress>? progress = null) => new(Guid.NewGuid(), new("myavalonia.plugin.workflow-studio"), progress ?? new QuietProgress());
    private static string[] Outputs(JsonElement result) => result.GetProperty("successfulOutputs").EnumerateArray().Select(o => o.GetProperty("path").GetString()!).ToArray();
    private static void Valid(JsonElement output, WorkflowActionDescriptor descriptor) =>
        Assert.True(new WorkflowSchemaValidator().ValidateInstance(descriptor.OutputSchema, output, WorkflowSchemaProfile.MaximumOutputBytes).IsValid, output.GetRawText());

    [Theory]
    [InlineData("plain", false, "completed")]
    [InlineData("plain", true, "failed")]
    [InlineData("header-encrypted", false, "failed")]
    public async Task 分卷无密码工作流整组去重且保持既有输出结构(string variant, bool missing, string expectedState)
    {
        using var w = new TestWorkspace(); var parts = SplitTestData.CopyParts(w, variant);
        if (missing) File.Delete(parts[2]);
        var inputs = parts.Where(File.Exists).Reverse().Concat(parts.Where(File.Exists));
        var result = await new UnpackWorkflowAction(new UnpackService()).InvokeAsync(UnpackArgs(inputs, w.Output), Context(), Token);
        Valid(result, ArchiveWorkflowActions.Unpack);
        Assert.Equal(expectedState, result.GetProperty("state").GetString());
        Assert.Equal(1, result.GetProperty("items").GetArrayLength());
        Assert.DoesNotContain("SourceMembers", result.GetRawText()); Assert.DoesNotContain("volume-test", result.GetRawText());
        if (expectedState == "completed") SplitTestData.AssertContents(Assert.Single(Outputs(result)));
        else Assert.Empty(Outputs(result));
    }

    [Theory]
    [InlineData("zip", "standard")]
    [InlineData("zip", "store")]
    [InlineData("tar", "standard")]
    [InlineData("tar.gz", "high")]
    public async Task 无界面创建与解压使用同一业务用例且内容等价(string format, string compression)
    {
        using var w = new TestWorkspace();
        var source = w.FilePath("中文.txt"); await File.WriteAllTextAsync(source, "真实产物正文", Token);
        var pack = await new CreateArchiveWorkflowAction(new PackBatchService()).InvokeAsync(CreateArgs([source], w.Output, format: format, compression: compression), Context(), Token);
        Valid(pack, ArchiveWorkflowActions.Create); Assert.Equal("completed", pack.GetProperty("state").GetString());
        var archive = Assert.Single(Outputs(pack));
        var unpack = await new UnpackWorkflowAction(new UnpackService()).InvokeAsync(UnpackArgs([archive], w.FilePath("readback")), Context(), Token);
        Valid(unpack, ArchiveWorkflowActions.Unpack); Assert.Equal("completed", unpack.GetProperty("state").GetString());
        var directory = Assert.Single(Outputs(unpack));
        Assert.Equal("真实产物正文", await File.ReadAllTextAsync(Path.Combine(directory, "中文.txt"), Token));
        await using var gui = new UnpackService().CreateSession(new([archive], w.FilePath("gui")));
        var equivalent = await gui.ExecuteAsync(cancellationToken: Token);
        Assert.Equal(BatchState.Completed, equivalent.State);
        Assert.Equal(await File.ReadAllBytesAsync(Path.Combine(directory, "中文.txt"), Token), await File.ReadAllBytesAsync(Path.Combine(equivalent.Nodes[0].OutputDirectory!, "中文.txt"), Token));
    }

    [Fact]
    public async Task 部分解压失败仅显式成功产物进入下游并保留来源与深度停止()
    {
        using var w = new TestWorkspace();
        var nested = w.Zip("inner.zip", ("内容.txt", "正文"u8.ToArray()));
        var outer = w.Zip("outer.zip", ("inner.zip", await File.ReadAllBytesAsync(nested, Token)));
        var missing = w.FilePath("missing.zip");
        var result = await new UnpackWorkflowAction(new UnpackService()).InvokeAsync(UnpackArgs([outer, missing], w.Output), Context(), Token);
        Valid(result, ArchiveWorkflowActions.Unpack);
        Assert.Equal("partial-failure", result.GetProperty("state").GetString());
        Assert.Equal(1, result.GetProperty("counts").GetProperty("failed").GetInt32());
        Assert.Equal(1, result.GetProperty("counts").GetProperty("skipped").GetInt32());
        var items = result.GetProperty("items").EnumerateArray().ToArray();
        var parent = Assert.Single(items, i => i.GetProperty("sourcePath").GetString() == outer);
        var child = Assert.Single(items, i => i.GetProperty("depth").GetInt32() == 2);
        Assert.Equal(parent.GetProperty("id").GetString(), child.GetProperty("parentId").GetString());
        var successful = Assert.Single(Outputs(result));
        var downstream = await new CreateArchiveWorkflowAction(new PackBatchService()).InvokeAsync(CreateArgs([successful], w.FilePath("deliver")), Context(), Token);
        using var archive = ZipFile.OpenRead(Assert.Single(Outputs(downstream)));
        Assert.DoesNotContain(archive.Entries, e => e.FullName.Contains("missing", StringComparison.Ordinal));
    }

    [Fact]
    public async Task 分组部分失败保留失败组而合并不静默漏掉坏输入()
    {
        using var w = new TestWorkspace();
        var source = w.FilePath("valid.txt"); await File.WriteAllTextAsync(source, "ok", Token);
        var handler = new CreateArchiveWorkflowAction(new PackBatchService());
        var inputs = new[] { source, w.FilePath("missing.txt") };
        var separate = await handler.InvokeAsync(CreateArgs(inputs, w.Output, "separate"), Context(), Token);
        Valid(separate, ArchiveWorkflowActions.Create);
        Assert.Equal("partial-failure", separate.GetProperty("state").GetString()); Assert.Single(Outputs(separate));
        Assert.Equal(2, separate.GetProperty("items").GetArrayLength());
        var combined = await handler.InvokeAsync(CreateArgs(inputs, w.Output), Context(), Token);
        Assert.Equal("failed", combined.GetProperty("state").GetString()); Assert.Empty(Outputs(combined));
    }

    [Theory]
    [InlineData("password")]
    [InlineData("passwords")]
    [InlineData("encrypt")]
    [InlineData("secretReference")]
    public async Task 未开放的秘密字段在结构边界拒绝且不回显(string field)
    {
        using var w = new TestWorkspace();
        foreach (var create in new[] { false, true })
        {
            var arguments = JsonNode.Parse((create ? CreateArgs([w.FilePath("x")], w.Output) : UnpackArgs([w.FilePath("x")], w.Output)).GetRawText())!;
            arguments[field] = "password-sentinel-never-log";
            var descriptor = create ? ArchiveWorkflowActions.Create : ArchiveWorkflowActions.Unpack;
            var json = JsonSerializer.SerializeToElement(arguments);
            Assert.False(new WorkflowSchemaValidator().ValidateInstance(descriptor.InputSchema, json, WorkflowSchemaProfile.MaximumInputBytes).IsValid);
            IWorkflowActionHandler handler = create ? new CreateArchiveWorkflowAction(new PackBatchService()) : new UnpackWorkflowAction(new UnpackService());
            var output = await handler.InvokeAsync(json, Context(), Token);
            Valid(output, descriptor); Assert.Equal("invalid-arguments", output.GetProperty("diagnosticCode").GetString());
            Assert.DoesNotContain("password-sentinel", output.GetRawText()); Assert.False(Directory.Exists(w.Output));
            Assert.Empty(descriptor.SensitiveInputPointers); Assert.False(descriptor.Risks.HasFlag(WorkflowActionRiskFlags.HandlesSecret));
        }
    }

    [Fact]
    public async Task 非交互缺密有结构化诊断且无任何页面依赖()
    {
        using var w = new TestWorkspace();
        var encrypted = w.CopyFixture("Zip.deflate.pkware.zip");
        var result = await new UnpackWorkflowAction(new UnpackService()).InvokeAsync(UnpackArgs([encrypted], w.Output), Context(), Token);
        Valid(result, ArchiveWorkflowActions.Unpack); Assert.Empty(Outputs(result));
        Assert.Equal("PasswordRequiredOrInvalid", result.GetProperty("items")[0].GetProperty("diagnosticCode").GetString());
        Assert.Empty(Directory.GetFileSystemEntries(w.Output));
    }

    [Theory]
    [InlineData("version", "2")]
    [InlineData("maxDepth", "17")]
    [InlineData("maxDepth", "0")]
    [InlineData("repeatPolicy", "\"overwrite\"")]
    [InlineData("nameEncoding", "\"guess\"")]
    public async Task 版本范围及重跑策略拒绝不合法参数(string field, string raw)
    {
        using var w = new TestWorkspace();
        var arguments = JsonNode.Parse(UnpackArgs([w.FilePath("x")], w.Output).GetRawText())!;
        arguments[field] = JsonNode.Parse(raw);
        var result = await new UnpackWorkflowAction(new UnpackService()).InvokeAsync(JsonSerializer.SerializeToElement(arguments), Context(), Token);
        Assert.Equal("invalid-arguments", result.GetProperty("diagnosticCode").GetString()); Assert.False(Directory.Exists(w.Output));
    }

    [Fact]
    public async Task 重复字段及缺少显式重跑选择都不能被反序列化吞掉()
    {
        using var w = new TestWorkspace();
        var args = UnpackArgs([w.FilePath("x")], w.Output).GetRawText();
        using var duplicate = JsonDocument.Parse(args.Replace("\"version\":1", "\"version\":1,\"version\":1", StringComparison.Ordinal));
        var handler = new UnpackWorkflowAction(new UnpackService());
        Assert.Equal("invalid-arguments", (await handler.InvokeAsync(duplicate.RootElement, Context(), Token)).GetProperty("diagnosticCode").GetString());
        var missing = JsonNode.Parse(args)!.AsObject(); missing.Remove("repeatPolicy");
        Assert.Equal("invalid-arguments", (await handler.InvokeAsync(JsonSerializer.SerializeToElement(missing), Context(), Token)).GetProperty("diagnosticCode").GetString());
    }

    [Theory]
    [InlineData("tar", "high")]
    [InlineData("tar.gz", "store")]
    [InlineData("7z", "standard")]
    public async Task 不开放未经验证的格式或选项组合(string format, string compression)
    {
        using var w = new TestWorkspace();
        var source = w.FilePath("x.txt"); await File.WriteAllTextAsync(source, "x", Token);
        var result = await new CreateArchiveWorkflowAction(new PackBatchService()).InvokeAsync(CreateArgs([source], w.Output, format: format, compression: compression), Context(), Token);
        Assert.Equal("failed", result.GetProperty("state").GetString()); Assert.Empty(Outputs(result));
    }

    [Fact]
    public async Task 空成功清单正常跳过而相对路径重新验证()
    {
        using var w = new TestWorkspace();
        var pack = new CreateArchiveWorkflowAction(new PackBatchService());
        var empty = await pack.InvokeAsync(CreateArgs([], w.Output), Context(), Token);
        Valid(empty, ArchiveWorkflowActions.Create); Assert.Equal("skipped", empty.GetProperty("state").GetString());
        var bad = await pack.InvokeAsync(CreateArgs(["relative.txt"], w.Output), Context(), Token);
        Assert.Equal("invalid-request", bad.GetProperty("diagnosticCode").GetString());
        var unpack = await new UnpackWorkflowAction(new UnpackService()).InvokeAsync(UnpackArgs(["relative.zip"], w.Output), Context(), Token);
        Assert.Equal("invalid-request", unpack.GetProperty("diagnosticCode").GetString()); Assert.False(Directory.Exists(w.Output));
    }

    [Fact]
    public async Task 显式新建重跑与并发不覆盖且每次有独立调用标识()
    {
        using var w = new TestWorkspace();
        var source = w.FilePath("x.txt"); await File.WriteAllTextAsync(source, "内容", Token);
        var handler = new CreateArchiveWorkflowAction(new PackBatchService());
        var args = CreateArgs([source], w.Output);
        var first = await handler.InvokeAsync(args, Context(), Token);
        var original = await File.ReadAllBytesAsync(Assert.Single(Outputs(first)), Token);
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => handler.InvokeAsync(args, Context(), Token).AsTask()));
        Assert.All(results, r => Assert.Equal("completed", r.GetProperty("state").GetString()));
        Assert.Equal(4, results.SelectMany(Outputs).Distinct().Count());
        Assert.Equal(4, results.Select(r => r.GetProperty("invocationId").GetString()).Distinct().Count());
        Assert.Equal(original, await File.ReadAllBytesAsync(Assert.Single(Outputs(first)), Token));
        var unpack = new UnpackWorkflowAction(new UnpackService());
        var unpackArgs = UnpackArgs(Outputs(first), w.FilePath("unpacked"));
        var unpacked = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => unpack.InvokeAsync(unpackArgs, Context(), Token).AsTask()));
        Assert.Equal(3, unpacked.SelectMany(Outputs).Distinct().Count());
        Assert.DoesNotContain(Directory.EnumerateFileSystemEntries(w.Root, "*", SearchOption.AllDirectories), p => Path.GetFileName(p).StartsWith(".layer-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task 解压运行中取消到达实际读取并在返回前清理会话()
    {
        using var w = new TestWorkspace();
        var input = w.Zip("cancel.zip", ("x", "x"u8.ToArray()));
        using var stop = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var extractor = new DelegatingExtractor(async call =>
        {
            await File.WriteAllTextAsync(Path.Combine(call.Destination, "partial"), "部分", call.Token);
            entered.SetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, call.Token);
            throw new InvalidOperationException("取消未到达");
        });
        var running = new UnpackWorkflowAction(new UnpackService(extractor)).InvokeAsync(UnpackArgs([input], w.Output), Context(), stop.Token).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token); stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        Assert.Empty(Directory.GetFileSystemEntries(w.Output));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 已取消调用在任何准备与写入前结束(bool create)
    {
        using var w = new TestWorkspace(); using var stop = new CancellationTokenSource(); stop.Cancel();
        IWorkflowActionHandler handler = create ? new CreateArchiveWorkflowAction(new PackBatchService()) : new UnpackWorkflowAction(new UnpackService());
        var args = create ? CreateArgs([w.FilePath("x")], w.Output) : UnpackArgs([w.FilePath("x")], w.Output);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => handler.InvokeAsync(args, Context(), stop.Token).AsTask());
        Assert.False(Directory.Exists(w.Output));
    }

    private sealed class QuietProgress : IProgress<WorkflowActionProgress> { public void Report(WorkflowActionProgress value) { } }

    [Fact]
    public void 结果预算保留完整项与真实总数且不暴露临时清理路径()
    {
        var nodes = Enumerable.Range(0, 64).Select(_ => new ArchiveNodeResult(Guid.NewGuid(), null,
            new string('来', 16000), 1, NodeState.Extracted, "ZIP", new string('出', 16000), null,
            [".layer-unpack-private-residue"], 1)).ToArray();
        var output = ArchiveWorkflowResult.From(new UnpackResult(Guid.NewGuid(), BatchState.Completed, nodes, 64, 64), Context());
        Valid(output, ArchiveWorkflowActions.Unpack);
        Assert.True(output.GetProperty("detailsTruncated").GetBoolean());
        Assert.Equal("partial-failure", output.GetProperty("state").GetString());
        Assert.Equal(64, output.GetProperty("counts").GetProperty("completed").GetInt32());
        Assert.NotEmpty(output.GetProperty("successfulOutputs").EnumerateArray());
        var ids = output.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetString()).ToArray();
        Assert.All(output.GetProperty("successfulOutputs").EnumerateArray(), item => Assert.Contains(item.GetProperty("id").GetString(), ids));
        Assert.DoesNotContain("private-residue", output.GetRawText());
        Assert.All(output.GetProperty("items").EnumerateArray(), i => Assert.True(i.GetProperty("cleanupRequired").GetBoolean()));
    }

    [Fact]
    public async Task 实际创建写入中取消清理临时文件且保留先前交付()
    {
        using var w = new TestWorkspace();
        var source = w.FilePath("cancel.bin"); await File.WriteAllBytesAsync(source, new byte[2 * 1024 * 1024], Token);
        var handler = new CreateArchiveWorkflowAction(new PackBatchService());
        var first = await handler.InvokeAsync(CreateArgs([source], w.Output), Context(), Token);
        var delivered = Assert.Single(Outputs(first));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var progress = new CallbackProgress(_ =>
        {
            if (Directory.Exists(w.Output) && Directory.EnumerateFiles(w.Output, ".layer-pack-*.tmp").Any()) stop.Cancel();
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => handler.InvokeAsync(CreateArgs([source], w.Output), Context(progress), stop.Token).AsTask());
        Assert.True(stop.IsCancellationRequested); Assert.True(File.Exists(delivered));
        Assert.Single(Directory.GetFiles(w.Output));
    }

    [Fact]
    public async Task 危险归档路径在真实引擎拒绝且没有越界产物()
    {
        using var w = new TestWorkspace();
        var source = w.Zip("unsafe.zip", ("../escape.txt", "坏"u8.ToArray()));
        var output = await new UnpackWorkflowAction(new UnpackService()).InvokeAsync(UnpackArgs([source], w.Output), Context(), Token);
        Assert.Equal("failed", output.GetProperty("state").GetString()); Assert.Empty(Outputs(output));
        Assert.Equal("UnsafePath", output.GetProperty("items")[0].GetProperty("diagnosticCode").GetString());
        Assert.False(File.Exists(w.FilePath("escape.txt")));
    }

    private sealed class CallbackProgress(Action<WorkflowActionProgress> callback) : IProgress<WorkflowActionProgress>
    { public void Report(WorkflowActionProgress value) => callback(value); }

    [AvaloniaFact]
    public async Task GUI与Workflow同时处理加密包时密码和关闭责任隔离()
    {
        using var w = new TestWorkspace(); using var lifetime = new TestLifetime();
        var encrypted = w.CopyFixture("Zip.deflate.pkware.zip");
        var service = new UnpackService();
        await using var document = new UnpackDocument(service, lifetime) { OutputDirectory = w.FilePath("gui"), PasswordText = "12345678" };
        await document.AddPathsAsync([encrypted]);
        var gui = document.StartCommand.ExecuteAsync(null);
        var workflow = new UnpackWorkflowAction(service).InvokeAsync(UnpackArgs([encrypted], w.FilePath("workflow")), Context(), Token).AsTask();
        await Task.WhenAll(gui, workflow);
        Assert.Equal(BatchState.Completed, document.CurrentResult?.State);
        Assert.Equal("failed", workflow.Result.GetProperty("state").GetString()); Assert.Empty(Outputs(workflow.Result));
        Assert.Equal("PasswordRequiredOrInvalid", workflow.Result.GetProperty("items")[0].GetProperty("diagnosticCode").GetString());
        lifetime.Close();
        var plain = w.Zip("independent.zip", ("x.txt", "独立"u8.ToArray()));
        var afterClose = await new UnpackWorkflowAction(service).InvokeAsync(UnpackArgs([plain], w.FilePath("independent")), Context(), Token);
        Assert.Equal("completed", afterClose.GetProperty("state").GetString());
    }
}

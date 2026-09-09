using System.IO.Compression;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Headless.XUnit;
using LayerUnpackPlugin.Headless.Tests;
using MyAvaloniaManagement.PluginSdk;
using Xunit;

namespace LayerUnpackPlugin.WorkflowIntegration.Tests;

public sealed class WorkflowIntegrationTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [AvaloniaFact]
    public async Task 真实Fractal产物经Studio公开引用创建并解压且三插件ALC隔离()
    {
        using var w = new TestWorkspace();
        await using var h = new WorkflowIntegrationHarness();
        Assert.Equal(3, h.Contexts.Distinct().Count());
        Assert.All(h.Contexts, c => Assert.NotSame(AssemblyLoadContext.Default, c));
        Assert.Same(AssemblyLoadContext.Default, AssemblyLoadContext.GetLoadContext(typeof(IWorkflowActionHandler).Assembly));
        Assert.DoesNotContain(typeof(WorkflowIntegrationTests).Assembly.GetReferencedAssemblies(), a => a.Name is "LayerUnpackPlugin.Plugin" or "FractalArtPlugin.Plugin" or "WorkflowStudio.Plugin");
        var definition = h.Definition(Path.Combine(AppContext.BaseDirectory, "Fixtures/sample.fractal-workflow.json"), w.Output, w.FilePath("unpack"));
        var result = await h.RunStudioAsync(definition, Token);
        Assert.True(result.GetProperty("Succeeded").GetBoolean(), result.GetRawText());
        Assert.Equal(4, result.GetProperty("Entries").GetArrayLength());
        var source = Assert.Single(h.Gateway.Artifacts);
        var pack = Assert.Single(h.Gateway.Results, r => r.Action == WorkflowIntegrationHarness.Create).Output;
        var unpack = Assert.Single(h.Gateway.Results, r => r.Action == WorkflowIntegrationHarness.Unpack).Output;
        Assert.Equal("completed", pack.GetProperty("state").GetString()); Assert.Equal("completed", unpack.GetProperty("state").GetString());
        var archivePath = pack.GetProperty("successfulOutputs")[0].GetProperty("path").GetString()!;
        var unpackDirectory = unpack.GetProperty("successfulOutputs")[0].GetProperty("path").GetString()!;
        using var zip = ZipFile.OpenRead(archivePath);
        var entry = Assert.Single(zip.Entries);
        await using var data = entry.Open();
        Assert.Equal(source.GetProperty("sha256").GetString(), Convert.ToHexString(await SHA256.HashDataAsync(data, Token)), ignoreCase: true);
        Assert.Equal(source.GetProperty("sha256").GetString(), Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(unpackDirectory, entry.FullName), Token))), ignoreCase: true);
        Assert.False(File.Exists(source.GetProperty("path").GetString()));
        Assert.Equal(h.Gateway.ScopesOpened, h.Gateway.ScopesClosed);
        var archiveContext = Assert.Single(h.Contexts, c => c.Name == "LayerUnpackPlugin.Plugin");
        Assert.Contains(archiveContext.Assemblies, a => a.GetName().Name == "LayerUnpackPlugin.Headless");
        Assert.All(h.Contexts, c => Assert.DoesNotContain(c.Assemblies, a => a.GetName().Name!.StartsWith("MyAvaloniaManagement.PluginSdk", StringComparison.Ordinal)));
    }

    [AvaloniaFact]
    public async Task 真实上游文件在读取前消失会重新验证且下游没有失败来源()
    {
        using var w = new TestWorkspace(); await using var h = new WorkflowIntegrationHarness();
        h.Gateway.Fault = action =>
        {
            if (action == WorkflowIntegrationHarness.Create) File.Delete(Assert.Single(h.Gateway.Artifacts).GetProperty("path").GetString()!);
            return false;
        };
        var result = await h.RunStudioAsync(h.Definition(Path.Combine(AppContext.BaseDirectory, "Fixtures/sample.fractal-workflow.json"), w.Output, w.FilePath("unpack")), Token);
        Assert.False(result.GetProperty("Succeeded").GetBoolean()); // SDK 调用完成，Studio 最终摘要仍必须报告业务失败。
        Assert.Contains(result.GetProperty("Entries").EnumerateArray(), entry => entry.GetProperty("FailureCode").GetString() == "archive.failed");
        var pack = Assert.Single(h.Gateway.Results, r => r.Action == WorkflowIntegrationHarness.Create).Output;
        Assert.Equal("failed", pack.GetProperty("state").GetString()); Assert.Empty(pack.GetProperty("successfulOutputs").EnumerateArray());
        Assert.DoesNotContain(h.Gateway.Results, r => r.Action == WorkflowIntegrationHarness.Unpack);
        Assert.False(Directory.Exists(w.Output));
    }

    [AvaloniaFact]
    public async Task 下游故障保留已交付归档且run拥有者可经公开Release补偿临时来源()
    {
        using var w = new TestWorkspace();
        string source;
        await using (var h = new WorkflowIntegrationHarness())
        {
            h.Gateway.Fault = action => action == WorkflowIntegrationHarness.Unpack;
            var result = await h.RunStudioAsync(h.Definition(Path.Combine(AppContext.BaseDirectory, "Fixtures/sample.fractal-workflow.json"), w.Output, w.FilePath("unpack")), Token);
            Assert.False(result.GetProperty("Succeeded").GetBoolean());
            source = Assert.Single(h.Gateway.Artifacts).GetProperty("path").GetString()!;
            Assert.True(File.Exists(source)); Assert.Single(Directory.GetFiles(w.Output, "*.zip"));
        }
        Assert.False(File.Exists(source)); Assert.Single(Directory.GetFiles(w.Output, "*.zip"));
    }

    [AvaloniaFact]
    public async Task 目录契约漂移在真实Studio验证阶段阻止所有调用()
    {
        using var w = new TestWorkspace(); await using var h = new WorkflowIntegrationHarness();
        var json = JsonNode.Parse(h.Definition(Path.Combine(AppContext.BaseDirectory, "Fixtures/sample.fractal-workflow.json"), w.Output, w.FilePath("unpack")))!;
        json["contractRevision"] = "sha256:" + new string('0', 64);
        var exception = await Assert.ThrowsAnyAsync<InvalidOperationException>(() => h.RunStudioAsync(json.ToJsonString(), Token));
        Assert.Equal("WorkflowValidationException", exception.GetType().Name); Assert.Empty(h.Gateway.Results);
    }

    [AvaloniaFact]
    public async Task Run释放取消跨ALC实际解压并等待Scope与自有暂存清理()
    {
        using var w = new TestWorkspace(); await using var h = new WorkflowIntegrationHarness();
        var archive = w.Zip("cancel.zip", ("large.bin", new byte[16 * 1024 * 1024]));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var run = h.Gateway.CreateRun();
        var args = JsonSerializer.SerializeToElement(new
        {
            version = 1,
            inputs = new[] { archive },
            outputDirectory = w.Output,
            maxDepth = 1,
            nameEncoding = "gb18030",
            repeatPolicy = "create-new"
        });
        var work = run.InvokeAsync(new(new(WorkflowIntegrationHarness.Unpack), args), new CallbackProgress(_ =>
        {
            if (Directory.Exists(w.Output) && Directory.EnumerateDirectories(w.Output, ".layer-unpack-*").Any()) entered.TrySetResult();
        }), Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        await run.DisposeAsync();
        var result = await work;
        Assert.Equal(WorkflowActionInvocationStatus.Cancelled, result.Status); Assert.Null(result.Output);
        Assert.Empty(Directory.GetFileSystemEntries(w.Output)); Assert.Equal(h.Gateway.ScopesOpened, h.Gateway.ScopesClosed);
    }

    private sealed class CallbackProgress(Action<WorkflowActionProgress> report) : IProgress<WorkflowActionProgress>
    { public void Report(WorkflowActionProgress value) => report(value); }
}

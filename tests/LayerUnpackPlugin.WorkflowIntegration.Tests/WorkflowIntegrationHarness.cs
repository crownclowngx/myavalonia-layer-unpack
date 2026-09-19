using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Microsoft.Extensions.DependencyInjection;
using MyAvaloniaManagement.PluginSdk;
using MyAvaloniaManagement.PluginSdk.UI;
using MyAvaloniaManagement.PluginSdk.Workflow;
using Xunit;

namespace LayerUnpackPlugin.WorkflowIntegration.Tests;

/// <summary>
/// 本地 SDK 集成边界：真实 Module、三个独立 ALC、三个私有容器和真实 Studio Runner。
/// 只替代宿主的目录／调用基础设施，不模拟授权窗口、安装、卸载或正式退出门禁。
/// 反射只供测试驱动 Studio 自己的 Codec/Runner，归档与生产者之间始终只通过 SDK JSON 传递。
/// </summary>
internal sealed class WorkflowIntegrationHarness : IAsyncDisposable
{
    internal const string Render = "myavalonia.plugin.fractal.art.workflow.render-artwork-file";
    internal const string Release = "myavalonia.plugin.fractal.art.workflow.release-artifact";
    internal const string Create = "myavalonia.plugin.layer.unpack.workflow.create-v1";
    internal const string Unpack = "myavalonia.plugin.layer.unpack.workflow.unpack-v1";
    private readonly List<PluginContext> _contexts = [];
    private readonly List<Registration> _registrations = [];
    private readonly Lifetime _lifetime = new();
    private readonly IServiceScope _studioScope;
    private readonly Assembly _studio;
    internal LocalGateway Gateway { get; } = new();
    internal IReadOnlyList<AssemblyLoadContext> Contexts => _contexts;

    internal WorkflowIntegrationHarness()
    {
        var repository = FindRepository();
        var configuration = typeof(WorkflowIntegrationHarness).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()!.Configuration;
        Register(Path.Combine(repository, $"src/LayerUnpackPlugin.Plugin/bin/{configuration}/net10.0/LayerUnpackPlugin.Plugin.dll"), "LayerUnpackPlugin.Plugin.LayerUnpackPluginModule", "myavalonia.plugin.layer.unpack");
        Register(Path.Combine(repository, $"../myavalonia-fractal-art/src/FractalArtPlugin.Plugin/bin/{configuration}/net10.0/FractalArtPlugin.Plugin.dll"), "FractalArtPlugin.Plugin.FractalArtPluginModule", "myavalonia.plugin.fractal.art");
        var studio = Register(Path.Combine(repository, $"../myavalonia-workflow-studio/src/WorkflowStudio.Plugin/bin/{configuration}/net10.0/WorkflowStudio.Plugin.dll"), "WorkflowStudio.Plugin.WorkflowStudioModule", "myavalonia.plugin.workflow-studio");
        _studio = studio.Assembly;
        _studioScope = studio.Provider.CreateScope();
    }

    internal static string FindRepository()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "LayerUnpackPlugin.slnx"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("未找到本地归档仓库，专项必须从本仓库的构建目录运行。");
    }

    private Registration Register(string path, string moduleName, string id)
    {
        var context = new PluginContext(Path.GetFullPath(path)); _contexts.Add(context);
        var assembly = context.LoadFromAssemblyPath(Path.GetFullPath(path));
        var module = (IPluginModule)Activator.CreateInstance(assembly.GetType(moduleName, throwOnError: true)!)!;
        var registration = new Registration(id, assembly);
        module.Configure(registration);
        registration.Services.AddSingleton<IWorkflowActionGateway>(Gateway);
        registration.Services.AddSingleton<IDocumentLifetime>(_lifetime);
        registration.Services.AddSingleton<IPluginWindowInteraction, NullWindow>();
        registration.Provider = registration.Services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        _registrations.Add(registration);
        foreach (var action in registration.Actions) Gateway.Actions.Add(action.Descriptor.Id.Value, (registration, action.Descriptor, action.Handler));
        return registration;
    }

    internal string Definition(string recipe, string archive, string unpack)
    {
        var revisions = WorkflowCatalogRevisionCalculator.Calculate(Gateway.GetAvailableActions());
        var json = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures/fractal-archive.workflow.json")))!.AsObject();
        json["contractRevision"] = revisions.ContractRevision;
        json["presentationRevision"] = revisions.PresentationRevision;
        var steps = json["steps"]!.AsArray();
        steps[0]!["arguments"]!["recipePath"] = recipe;
        steps[1]!["arguments"]!["outputDirectory"] = archive;
        steps[2]!["arguments"]!["outputDirectory"] = unpack;
        return json.ToJsonString();
    }

    internal async Task<JsonElement> RunStudioAsync(string json, CancellationToken token)
    {
        object Service(string name) => _studioScope.ServiceProvider.GetRequiredService(_studio.GetType("WorkflowStudio.Workflows." + name, true)!);
        var codec = Service("IWorkflowDefinitionCodec");
        var definition = codec.GetType().GetMethod("Parse")!.Invoke(codec, [json]);
        // 使用真实 Codec 往返，证明实例不是仅在测试内手工构造才可执行。
        var canonical = (string)codec.GetType().GetMethod("Serialize")!.Invoke(codec, [definition])!;
        definition = codec.GetType().GetMethod("Parse")!.Invoke(codec, [canonical]);
        var runner = Service("IWorkflowRunner");
        var task = (Task)runner.GetType().GetMethod("RunAsync")!.Invoke(runner, [definition, null, token])!;
        await task;
        return JsonSerializer.SerializeToElement(task.GetType().GetProperty("Result")!.GetValue(task));
    }

    public async ValueTask DisposeAsync()
    {
        await Gateway.StopAsync();
        // Studio 通用协议没有 finally 节点；测试拥有样例 run 的补偿责任，只使用生产者 Release 回收已捕获的文件。
        // 成功交付的归档和解压目录不属于这项补偿，测试工作区的删除由测试自身统一负责。
        Gateway.Fault = null;
        foreach (var artifact in Gateway.Artifacts)
        {
            await using var run = Gateway.CreateRun();
            var result = await run.InvokeAsync(new(new(Release), JsonSerializer.SerializeToElement(new { artifact })), null, CancellationToken.None);
            Assert.Equal(WorkflowActionInvocationStatus.Succeeded, result.Status);
        }
        _lifetime.Close(); _studioScope.Dispose();
        foreach (var registration in _registrations.AsEnumerable().Reverse()) registration.Provider.Dispose();
        _lifetime.Dispose();
        foreach (var context in _contexts) context.Unload();
    }

    private sealed class PluginContext(string path) : AssemblyLoadContext(Path.GetFileNameWithoutExtension(path), isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName name)
        {
            // Icons 与解码库同属插件私有资产；本地 ALC 回归也必须各自加载，不能误向默认宿主域索取。
            if (name.Name!.StartsWith("LayerUnpackPlugin", StringComparison.Ordinal) || name.Name.StartsWith("FractalArtPlugin", StringComparison.Ordinal) ||
                name.Name.StartsWith("WorkflowStudio", StringComparison.Ordinal) || name.Name is "SharpCompress" or "ICSharpCode.SharpZipLib" or "MyAvaloniaManagement.Icons")
            {
                var privatePath = Path.Combine(Path.GetDirectoryName(path)!, name.Name + ".dll");
                if (!File.Exists(privatePath)) privatePath = Path.Combine(AppContext.BaseDirectory, name.Name + ".dll");
                return LoadFromAssemblyPath(privatePath);
            }
            return Default.LoadFromAssemblyName(name);
        }
    }
}

internal sealed class Registration(string id, Assembly assembly) : IPluginRegistration, IPluginIconRegistration, IWorkflowActionRegistration, IWorkbenchCommandRegistration
{
    // V6.1：预览/测试只保留本次组合的纯图标数据；不使用 Host 的全局注册表或缓存。
    // 对重复名称和非法名称直接报错，避免预览吞掉正式 Host 会拒绝的声明。
    private readonly Dictionary<string, VectorIconDefinition> _previewIcons = new(StringComparer.Ordinal);
    public string AddIcon(string localName, VectorIconDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (localName is null || !System.Text.RegularExpressions.Regex.IsMatch(localName, @"\A[a-z][a-z0-9]*(?:-[a-z0-9]+)*\z"))
            throw new ArgumentException("图标名称必须使用小写字母、数字及单个连字符分段。", nameof(localName));
        var reference = $"plugin:{PluginId.Value}/{localName}";
        _previewIcons.Add(reference, definition);
        return reference;
    }


    public PluginId PluginId { get; } = new(id);
    public IServiceCollection Services { get; } = new ServiceCollection();
    internal Assembly Assembly { get; } = assembly;
    internal ServiceProvider Provider { get; set; } = null!;
    internal List<(WorkflowActionDescriptor Descriptor, Type Handler)> Actions { get; } = [];
    public void AddDocument<T, V>(DocumentDescriptor descriptor) where T : class, IPluginDocument where V : Control, new() => Services.AddScoped<T>();
    public void AddPersistableDocument<T, V>(DocumentDescriptor descriptor) where T : class, IPersistablePluginDocument where V : Control, new() => Services.AddScoped<T>();
    public void AddTool<T, V>(ToolDescriptor descriptor) where T : class where V : Control, new() => Services.AddScoped<T>();
    public void UseLifecycle<T>() where T : class, IPluginLifecycle => Services.AddSingleton<T>();
    public void AddWorkflowAction<T>(WorkflowActionDescriptor descriptor) where T : class, IWorkflowActionHandler
    {
        Assert.True(new WorkflowSchemaValidator().ValidateDescriptor(descriptor).IsValid);
        Actions.Add((descriptor, typeof(T))); Services.AddScoped<T>();
    }
    public void UseWorkflowActionGateway() { }
    public void AddDocumentCommand(CommandDescriptor descriptor, DocumentTypeId targetDocumentTypeId) { }
    public void AddMenuCommandContribution(MenuCommandContributionDescriptor descriptor) { }
    public void AddKeyBindingContribution(KeyBindingContributionDescriptor descriptor) { }
}

/// <summary>测试宿主端口只完成 Schema 校验、Scope 所有权和取消排空；实际动作不会得到其他插件的容器。</summary>
internal sealed class LocalGateway : IWorkflowActionGateway
{
    internal Dictionary<string, (Registration Registration, WorkflowActionDescriptor Descriptor, Type Handler)> Actions { get; } = [];
    internal List<(string Action, JsonElement Output)> Results { get; } = [];
    internal List<JsonElement> Artifacts { get; } = [];
    internal Func<string, bool>? Fault { get; set; }
    private readonly List<Run> _runs = [];
    internal int ScopesOpened;
    internal int ScopesClosed;
    public IReadOnlyList<WorkflowActionDescriptor> GetAvailableActions() => Actions.Values.Select(a => a.Descriptor).ToArray();
    public IWorkflowActionRun CreateRun() { var run = new Run(this); _runs.Add(run); return run; }
    internal async Task StopAsync() { foreach (var run in _runs.ToArray()) await run.DisposeAsync(); }

    private sealed class Run(LocalGateway owner) : IWorkflowActionRun
    {
        private readonly CancellationTokenSource _closing = new();
        private readonly List<Task> _calls = [];
        private bool _closed;
        public Task<WorkflowActionInvocationResult> InvokeAsync(WorkflowActionInvocationRequest request, IProgress<WorkflowActionProgress>? progress, CancellationToken token)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            var task = InvokeCoreAsync(request, progress, token); _calls.Add(task); return task;
        }
        private async Task<WorkflowActionInvocationResult> InvokeCoreAsync(WorkflowActionInvocationRequest request, IProgress<WorkflowActionProgress>? progress, CancellationToken token)
        {
            var id = Guid.NewGuid();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _closing.Token);
            if (owner.Fault?.Invoke(request.ActionId.Value) == true) return new(id, WorkflowActionInvocationStatus.Failed, null, new("test.failure", "受控下游失败"));
            if (!owner.Actions.TryGetValue(request.ActionId.Value, out var action)) return new(id, WorkflowActionInvocationStatus.Unavailable, null, new("action.missing", "动作缺失"));
            var schema = new WorkflowSchemaValidator();
            if (!schema.ValidateInstance(action.Descriptor.InputSchema, request.Arguments, WorkflowSchemaProfile.MaximumInputBytes).IsValid)
                return new(id, WorkflowActionInvocationStatus.Rejected, null, new("invalid.input", "参数不符合公开契约"));
            try
            {
                owner.ScopesOpened++;
                await using var scope = action.Registration.Provider.CreateAsyncScope();
                var handler = (IWorkflowActionHandler)scope.ServiceProvider.GetRequiredService(action.Handler);
                var output = await handler.InvokeAsync(request.Arguments, new(id, new("myavalonia.plugin.workflow-studio"), progress ?? new QuietProgress()), linked.Token);
                Assert.True(schema.ValidateInstance(action.Descriptor.OutputSchema, output, WorkflowSchemaProfile.MaximumOutputBytes).IsValid, output.GetRawText());
                owner.Results.Add((request.ActionId.Value, output.Clone()));
                if (request.ActionId.Value == WorkflowIntegrationHarness.Render) owner.Artifacts.Add(output.GetProperty("artifact").Clone());
                return new(id, WorkflowActionInvocationStatus.Succeeded, output, null);
            }
            catch (OperationCanceledException) { return new(id, WorkflowActionInvocationStatus.Cancelled, null, null); }
            finally { owner.ScopesClosed++; }
        }
        public async ValueTask DisposeAsync()
        {
            if (_closed) return;
            _closed = true; _closing.Cancel();
            await Task.WhenAll(_calls); _closing.Dispose(); owner._runs.Remove(this);
        }
    }
    private sealed class QuietProgress : IProgress<WorkflowActionProgress> { public void Report(WorkflowActionProgress value) { } }
}

internal sealed class Lifetime : IDocumentLifetime, IDisposable
{
    private readonly CancellationTokenSource _closing = new();
    public CancellationToken ClosingToken => _closing.Token;
    public bool IsClosing => _closing.IsCancellationRequested;
    internal void Close() => _closing.Cancel();
    public void Dispose() => _closing.Dispose();
}
internal sealed class NullWindow : IPluginWindowInteraction
{
    public Task<IReadOnlyList<string>> PickOpenFilesAsync(FilePickerOpenOptions options, CancellationToken cancellationToken) => throw new InvalidOperationException("工作流动作不得打开文件对话框。");
    public Task<string?> PickSaveFileAsync(FilePickerSaveOptions options, CancellationToken cancellationToken) => throw new InvalidOperationException("工作流动作不得打开保存对话框。");
    public Task<bool> TrySetClipboardTextAsync(string text, CancellationToken cancellationToken) => Task.FromResult(false);
}

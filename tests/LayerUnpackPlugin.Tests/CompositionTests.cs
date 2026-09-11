using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using LayerUnpackPlugin.Constants;
using LayerUnpackPlugin.Features.Unpack;
using LayerUnpackPlugin.Features.Pack;
using LayerUnpackPlugin.Features.Browse;
using LayerUnpackPlugin.Headless.Application;
using LayerUnpackPlugin.Plugin;
using LayerUnpackPlugin.Workflow;
using MyAvaloniaManagement.PluginSdk.Workflow;
using Microsoft.Extensions.DependencyInjection;
using MyAvaloniaManagement.PluginSdk;
using MyAvaloniaManagement.PluginSdk.UI;
using Xunit;

namespace LayerUnpackPlugin.Tests;

public sealed class CompositionTests
{
    [Fact]
    public void 组合注册三个普通Document和两个Workflow且无Tool历史命令或Gateway()
    {
        var registration = new Registration();
        new LayerUnpackPluginModule().Configure(registration);
        Assert.Equal(3, registration.Documents.Count);
        var browse = Assert.Single(registration.Documents, d => d.Model == typeof(BrowseDocument));
        Assert.Equal("myavalonia.plugin.layer.unpack.document.browse", browse.Descriptor.DocumentTypeId.Value);
        Assert.Equal(typeof(BrowseView), browse.View);
        Assert.False(typeof(IPersistablePluginDocument).IsAssignableFrom(typeof(BrowseDocument)));
        var document = Assert.Single(registration.Documents, d => d.Model == typeof(UnpackDocument));
        var pack = Assert.Single(registration.Documents, d => d.Model == typeof(PackDocument));
        Assert.Equal(PluginIds.PackDocument, pack.Descriptor.DocumentTypeId);
        Assert.Equal(typeof(PackView), pack.View);
        Assert.False(typeof(IPersistablePluginDocument).IsAssignableFrom(typeof(PackDocument)));
        Assert.Equal("myavalonia.plugin.layer.unpack", PluginIds.Plugin.Value);
        Assert.Equal("myavalonia.plugin.layer.unpack.document.main", document.Descriptor.DocumentTypeId.Value);
        Assert.Equal(typeof(UnpackDocument), document.Model);
        Assert.Equal(typeof(UnpackView), document.View);
        Assert.Equal(0, registration.OtherContributions);
        Assert.Equal(2, registration.Actions.Count);
        Assert.Equal(new[] { ArchiveWorkflowActions.UnpackId, ArchiveWorkflowActions.CreateId }, registration.Actions.Select(a => a.Id.Value));
        Assert.All(registration.Actions, action => Assert.True(new WorkflowSchemaValidator().ValidateDescriptor(action).IsValid));
        Assert.False(typeof(IPersistablePluginDocument).IsAssignableFrom(typeof(UnpackDocument)));
    }

    [AvaloniaFact]
    public void 严格Scope构造与释放保持两个Document状态隔离()
    {
        var registration = new Registration();
        new LayerUnpackPluginModule().Configure(registration);
        registration.Services.AddScoped<IDocumentLifetime, TestLifetime>();
        using var provider = registration.Services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        using var a = provider.CreateScope();
        using var b = provider.CreateScope();
        var first = a.ServiceProvider.GetRequiredService<UnpackDocument>();
        var second = b.ServiceProvider.GetRequiredService<UnpackDocument>();
        Assert.NotSame(first, second);
        Assert.NotSame(a.ServiceProvider.GetRequiredService<IUnpackService>(), b.ServiceProvider.GetRequiredService<IUnpackService>());
        Assert.Same(a.ServiceProvider.GetRequiredService<IArchiveExtractor>(), b.ServiceProvider.GetRequiredService<IArchiveExtractor>());
        first.PasswordText = "private-sentinel";
        first.OutputDirectory = "first";
        Assert.Empty(second.PasswordText); Assert.Empty(second.OutputDirectory);
        a.Dispose();
        Assert.True(first.IsClosed); Assert.Empty(first.PasswordText);
        Assert.False(second.IsClosed);
    }

    private sealed class Registration : IPluginRegistration, IPluginIconRegistration, IWorkflowActionRegistration, IWorkbenchCommandRegistration
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


        public PluginId PluginId => PluginIds.Plugin;
        public IServiceCollection Services { get; } = new ServiceCollection();
        internal List<(DocumentDescriptor Descriptor, Type Model, Type View)> Documents { get; } = [];
        internal int OtherContributions { get; private set; }
        internal List<WorkflowActionDescriptor> Actions { get; } = [];
        public void AddDocument<TDocument, TView>(DocumentDescriptor descriptor)
            where TDocument : class, IPluginDocument where TView : Control, new()
        { Documents.Add((descriptor, typeof(TDocument), typeof(TView))); Services.AddScoped<TDocument>(); Services.AddTransient<TView>(); }
        public void AddPersistableDocument<TDocument, TView>(DocumentDescriptor descriptor)
            where TDocument : class, IPersistablePluginDocument where TView : Control, new() => OtherContributions++;
        public void AddTool<TTool, TView>(ToolDescriptor descriptor) where TTool : class where TView : Control, new() => OtherContributions++;
        public void UseLifecycle<TLifecycle>() where TLifecycle : class, IPluginLifecycle => OtherContributions++;
        public void AddWorkflowAction<THandler>(WorkflowActionDescriptor descriptor) where THandler : class, IWorkflowActionHandler
        { Actions.Add(descriptor); Services.AddScoped<THandler>(); }
        public void UseWorkflowActionGateway() => OtherContributions++;
        public void AddDocumentCommand(CommandDescriptor descriptor, DocumentTypeId targetDocumentTypeId) => OtherContributions++;
        public void AddMenuCommandContribution(MenuCommandContributionDescriptor descriptor) => OtherContributions++;
        public void AddKeyBindingContribution(KeyBindingContributionDescriptor descriptor) => OtherContributions++;
    }
}

internal sealed class TestLifetime : IDocumentLifetime, IDisposable
{
    private readonly CancellationTokenSource _closing = new();
    public CancellationToken ClosingToken => _closing.Token;
    public bool IsClosing => _closing.IsCancellationRequested;
    public void Close() => _closing.Cancel();
    public void Dispose() => _closing.Dispose();
}

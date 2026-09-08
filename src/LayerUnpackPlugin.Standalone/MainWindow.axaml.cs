using Avalonia.Controls;
using LayerUnpackPlugin.Features.Unpack;
using LayerUnpackPlugin.Features.Pack;
using LayerUnpackPlugin.Features.Browse;
using Microsoft.Extensions.DependencyInjection;
using MyAvaloniaManagement.PluginSdk;

namespace LayerUnpackPlugin.Standalone;

public sealed partial class MainWindow : Window
{
    private AsyncServiceScope _scope;
    private AsyncServiceScope _packScope;
    private AsyncServiceScope _browseScope;
    private BrowseDocument? _browseDocument;
    private PackDocument? _packDocument;
    private UnpackDocument? _document;
    private Task? _initialization;
    private bool _closing;
    private bool _allowClose;

    public MainWindow() => InitializeComponent();

    public MainWindow(IServiceProvider services) : this()
    {
        _scope = services.CreateAsyncScope();
        _document = _scope.ServiceProvider.GetRequiredService<UnpackDocument>();
        var view = _scope.ServiceProvider.GetRequiredService<UnpackView>();
        view.DataContext = _document;
        PreviewHost.Content = view;
        // 三类页面分别拥有 Scope 和 ClosingToken；切换标签不会释放或重建正在工作的任务。
        _packScope = services.CreateAsyncScope();
        _packDocument = _packScope.ServiceProvider.GetRequiredService<PackDocument>();
        var packView = _packScope.ServiceProvider.GetRequiredService<PackView>();
        packView.DataContext = _packDocument;
        PackPreviewHost.Content = packView;
        _browseScope = services.CreateAsyncScope();
        _browseDocument = _browseScope.ServiceProvider.GetRequiredService<BrowseDocument>();
        var browseView = _browseScope.ServiceProvider.GetRequiredService<BrowseView>();
        browseView.DataContext = _browseDocument;
        BrowsePreviewHost.Content = browseView;
        Opened += (_, _) => _initialization = InitializeDocumentAsync();
        Closing += OnClosing;
    }

    /// <summary>窗口消息循环启动后才初始化，避免构造函数同步等待异步 UI 续体形成死锁。</summary>
    private async Task InitializeDocumentAsync()
    {
        try
        {
            await _document!.InitializeAsync(new NewDocumentActivation("解压任务"), CancellationToken.None);
            await _packDocument!.InitializeAsync(new NewDocumentActivation("压缩任务"), CancellationToken.None);
            await _browseDocument!.InitializeAsync(new NewDocumentActivation("浏览任务"), CancellationToken.None);
        }
        catch (OperationCanceledException) when (_closing) { }
        catch (Exception) { Title = "层解 · 初始化失败"; if (_document is not null) _document.Message = "初始化失败，请关闭后重试。"; }
    }

    /// <summary>首次关闭先取消并等待真实任务退出，完成 Scope 释放后再真正关闭窗口。</summary>
    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_allowClose) return;
        e.Cancel = true;
        if (_closing) return;
        _closing = true;
        _scope.ServiceProvider.GetRequiredService<PreviewDocumentLifetime>().BeginClosing();
        _packScope.ServiceProvider.GetRequiredService<PreviewDocumentLifetime>().BeginClosing();
        _browseScope.ServiceProvider.GetRequiredService<PreviewDocumentLifetime>().BeginClosing();
        try
        {
            if (_initialization is not null) await _initialization;
            if (_document is not null) await _document.DisposeAsync();
            if (_packDocument is not null) await _packDocument.DisposeAsync();
            if (_browseDocument is not null) await _browseDocument.DisposeAsync();
            await _scope.DisposeAsync();
            await _packScope.DisposeAsync();
            await _browseScope.DisposeAsync();
        }
        finally
        {
            PreviewHost.Content = null;
            PackPreviewHost.Content = null;
            BrowsePreviewHost.Content = null;
            _browseDocument = null;
            _packDocument = null;
            _document = null;
            _allowClose = true;
            Close();
        }
    }
}

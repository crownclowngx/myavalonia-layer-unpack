using Avalonia.Controls;
using LayerUnpackPlugin.Features.Unpack;
using Microsoft.Extensions.DependencyInjection;
using MyAvaloniaManagement.PluginSdk;

namespace LayerUnpackPlugin.Standalone;

public sealed partial class MainWindow : Window
{
    private AsyncServiceScope _scope;
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
        Opened += (_, _) => _initialization = InitializeDocumentAsync();
        Closing += OnClosing;
    }

    /// <summary>窗口消息循环启动后才初始化，避免构造函数同步等待异步 UI 续体形成死锁。</summary>
    private async Task InitializeDocumentAsync()
    {
        try { await _document!.InitializeAsync(new NewDocumentActivation("解压任务"), CancellationToken.None); }
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
        try
        {
            if (_initialization is not null) await _initialization;
            if (_document is not null) await _document.DisposeAsync();
            await _scope.DisposeAsync();
        }
        finally
        {
            PreviewHost.Content = null;
            _document = null;
            _allowClose = true;
            Close();
        }
    }
}

using LayerUnpackPlugin.Features.Unpack;
using LayerUnpackPlugin.Headless.Application;
using MyAvaloniaManagement.PluginSdk;

namespace LayerUnpackPlugin.Features.Browse;

/// <summary>浏览页转入原解压页时使用的显式所有权容器。只传来源与输出位置，不传密码、选择或已执行状态。</summary>
/// <remarks>SDK 3.3 未提供携带任意路径的新建 Document 导航端口，因此在当前页承载真实 UnpackView/Document。
/// 此任务有独立 ClosingToken；浏览页关闭会排空它，返回浏览只切换显示，不偷偷取消已开始的解压。</remarks>
internal sealed class OwnedUnpackTask : IDocumentLifetime, IAsyncDisposable
{
    private readonly CancellationTokenSource _closing;
    private bool _disposed;
    public UnpackDocument Document { get; }
    public CancellationToken ClosingToken => _closing.Token;
    public bool IsClosing => _disposed || _closing.IsCancellationRequested;
    public OwnedUnpackTask(IUnpackService service, CancellationToken parentClosing)
    {
        _closing = CancellationTokenSource.CreateLinkedTokenSource(parentClosing);
        Document = new(service, this);
    }
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true; _closing.Cancel();
        await Document.DisposeAsync().ConfigureAwait(false); _closing.Dispose();
    }
}

using MyAvaloniaManagement.PluginSdk;

namespace LayerUnpackPlugin.Standalone;

/// <summary>独立预览的显式生命周期适配，不模拟 Host Dock 或保存恢复。</summary>
internal sealed class PreviewDocumentLifetime : IDocumentLifetime, IDisposable
{
    private readonly CancellationTokenSource _closing = new();
    private bool _disposed;
    public CancellationToken ClosingToken => _closing.Token;
    public bool IsClosing => _disposed || _closing.IsCancellationRequested;
    public void BeginClosing() { if (!IsClosing) _closing.Cancel(); }
    public void Dispose() { if (_disposed) return; BeginClosing(); _disposed = true; _closing.Dispose(); }
}

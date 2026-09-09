using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LayerUnpackPlugin.Features.Unpack;
using LayerUnpackPlugin.Headless.Application;
using LayerUnpackPlugin.Headless.Contracts;
using LayerUnpackPlugin.Headless.Domain;

namespace LayerUnpackPlugin.Features.Check;

/// <summary>按需检查子页，只持有本次来源和密码。用户显式开始后才进入后台，父页关闭取消并等待实际清理，
/// 进度使用代次隔离；关闭不等待依赖 UI 调度的命令续体，避免同步释放 Scope 死锁。</summary>
public sealed partial class ArchiveCheckDocument : ObservableObject, IAsyncDisposable
{
    private readonly IArchiveCheckService _service;
    private readonly CancellationTokenSource _closing;
    private CancellationTokenSource? _operation;
    private Task _background = Task.CompletedTask;
    private Task? _disposeTask;
    private long _generation;
    public bool IsClosed { get; private set; }
    public bool CanEdit => !IsBusy && !IsClosed;
    public IReadOnlyList<string> Sources { get; }
    public IReadOnlyList<NameEncodingOption> NameEncodings => UnpackDocument.NameEncodings;
    [ObservableProperty] private string _sourcePath;
    [ObservableProperty] private bool _directoryOnly;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isCancelling;
    [ObservableProperty] private NameEncodingOption _selectedNameEncoding = UnpackDocument.NameEncodings[0];
    [ObservableProperty][property: JsonIgnore] private string _passwordText = "";
    [ObservableProperty] private ArchiveCheckResult? _result;
    [ObservableProperty] private string _summary = "尚未检查";
    [ObservableProperty] private string _details = "完整检查需要读取正文；可能使用临时磁盘空间，结束后清理。";
    public string CapabilitySummary => string.Join("\n", ArchiveCapabilities.Reading.Select(c =>
        $"{c.Format}：{c.Validation}。{c.Limitations}"));

    public ArchiveCheckDocument(IArchiveCheckService service, IEnumerable<string> sources, CancellationToken closing)
    {
        _service = service; Sources = Array.AsReadOnly(sources.ToArray()); _sourcePath = Sources.FirstOrDefault() ?? "";
        _closing = CancellationTokenSource.CreateLinkedTokenSource(closing);
    }
    partial void OnIsBusyChanged(bool value)
    { OnPropertyChanged(nameof(CanEdit)); StartCommand.NotifyCanExecuteChanged(); CancelCommand.NotifyCanExecuteChanged(); }
    partial void OnIsCancellingChanged(bool value) => CancelCommand.NotifyCanExecuteChanged();
    partial void OnSourcePathChanged(string value) { PasswordText = ""; Invalidate(); }
    partial void OnDirectoryOnlyChanged(bool value) { PasswordText = ""; Invalidate(); }
    partial void OnSelectedNameEncodingChanged(NameEncodingOption value) => Invalidate();
    private void Invalidate() { if (CanEdit) { Result = null; Summary = "参数已变化，请重新检查"; Details = ""; } StartCommand.NotifyCanExecuteChanged(); }
    private bool CanStart() => CanEdit && !string.IsNullOrWhiteSpace(SourcePath);
    private bool CanCancel() => IsBusy && !IsClosed && !IsCancelling;

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        if (!CanStart()) return;
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(_closing.Token);
        _operation = operation; var generation = ++_generation;
        IsBusy = true; IsCancelling = false; Result = null; Summary = "正在检查"; Details = "";
        try
        {
            var request = new ArchiveCheckRequest(SourcePath, DirectoryOnly ? ArchiveCheckScope.DirectoryOnly : ArchiveCheckScope.FullContent,
                DirectoryOnly ? [] : PasswordPool.ParseLines(PasswordText), nameEncoding: SelectedNameEncoding.Encoding);
            PasswordText = "";
            var progress = new Progress<ArchiveCheckProgress>(p =>
            {
                if (IsClosed || !IsBusy || generation != _generation) return;
                Summary = $"已处理 {p.Entries} 项 · 展开 {p.ExpandedBytes:N0} 字节 · 尝试 {p.Attempts} 次";
            });
            var work = _service.CheckAsync(request, progress, operation.Token); _background = work;
            var result = await work;
            if (IsClosed || generation != _generation) return;
            Result = result;
            Summary = result.State switch
            {
                ArchiveCheckState.DirectoryRead => "目录可读，尚未检查内容",
                ArchiveCheckState.Completed => "完整内容检查通过（仅下列校验范围）",
                ArchiveCheckState.CompletedWithLimitations => "内容已读完，校验存在限制",
                ArchiveCheckState.Cancelled => "检查已取消，工作已退出",
                _ => "检查未通过"
            };
            Details = $"读取 {result.FilesRead} 个文件 · 展开 {result.ExpandedBytes:N0} 字节 · {result.Elapsed.TotalSeconds:F2} 秒\n" +
                string.Join("\n", result.Evidence.Select(e => $"已验证 {e.Kind}：{e.Files} 个文件").Concat(result.Limitations));
            if (result.Error is not null) Details += $"\n{result.Error.Message}\n{result.Error.NextStep}";
            if (result.CleanupWarning is not null) Details += $"\n临时内容清理失败：{result.CleanupWarning}";
        }
        catch (OperationCanceledException) { if (!IsClosed) Summary = "检查已取消"; }
        catch (ArgumentException e) { if (!IsClosed) { Summary = "尚未检查"; Details = e.Message; } }
        catch (Exception) { if (!IsClosed) { Summary = "检查未完成"; Details = "请检查来源和支持范围。"; } }
        finally { ++_generation; PasswordText = ""; _operation = null; if (!IsClosed) { IsCancelling = false; IsBusy = false; } }
    }
    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() { IsCancelling = true; _operation?.Cancel(); Summary = "正在取消并清理临时内容"; }
    public ValueTask DisposeAsync() => new(_disposeTask ??= CloseAsync());
    private async Task CloseAsync()
    {
        IsClosed = true; ++_generation; _closing.Cancel(); PasswordText = "";
        try { await _background.ConfigureAwait(false); } catch (Exception) { /* 关闭仅等待实际工作退出，结果由命令观察。 */ }
        _closing.Dispose();
    }
}

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LayerUnpackPlugin.Headless.Application;
using LayerUnpackPlugin.Headless.Contracts;
using MyAvaloniaManagement.PluginSdk;

namespace LayerUnpackPlugin.Features.Pack;

/// <summary>一次创建批次的呈现与生命周期。只把用户选择交给 Headless，不在 UI 扫描目录或编写 ZIP。</summary>
public sealed partial class PackDocument : ObservableObject, IPluginDocument, IDisposable, IAsyncDisposable
{
    private readonly IPackBatchService _service;
    private readonly IDocumentLifetime _lifetime;
    private readonly CancellationTokenSource _closing = new();
    private readonly CancellationTokenRegistration _hostClosing;
    private CancellationTokenSource? _operation;
    private Task _background = Task.CompletedTask;
    private Task? _disposeTask;
    private long _generation;
    private bool _closed;
    private bool _suggesting;
    private bool _nameChosen;
    private bool _directoryChosen;
    private PackBatchPlan? _plan;
    private PackBatchSession? _session;
    private PackBatchPlan? _executedPlan;
    private long _revision;
    private DocumentPresentationState _presentation = new("压缩任务");

    [ObservableProperty] private string _archiveName = "";
    [ObservableProperty] private string _outputDirectory = "";
    [ObservableProperty] private string _message = "添加文件或文件夹，生成一个 ZIP。";
    [ObservableProperty] private string _summary = "尚未开始";
    [ObservableProperty] private string _currentEntry = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isCancelling;
    [ObservableProperty] private bool _detailsExpanded;
    [ObservableProperty] private double _progressValue;
    [ObservableProperty] private bool _isIndeterminate = true;
    [ObservableProperty] private PackBatchResult? _batchResult;
    public PackResult? CurrentResult => BatchResult?.Groups.Count == 1 ? BatchResult.Groups[0].Result : null;

    public ObservableCollection<PackInputItem> Inputs { get; } = [];
    public ObservableCollection<string> RootMappings { get; } = [];
    public bool CanEdit => !IsBusy && !IsClosed;
    public bool IsClosed => _closed || _lifetime.IsClosing;
    public bool HasInputs => Inputs.Count > 0;
    public bool HasOutput => BatchResult?.CompletedCount > 0 || PreviousOutputs.Count > 0;
    public bool HasRetryableGroups => BatchResult?.CanRetry == true;
    public bool HasUnfinishedGroups => BatchResult is { } result && result.FailedCount + result.CancelledCount > 0;
    public bool HasFeedback => HasInputs || BatchResult is not null;
    public string? ResultDirectory => (BatchResult?.Groups.FirstOrDefault(g => g.Result.OutputPath is not null)?.Result.OutputPath ?? PreviousOutputs.FirstOrDefault()) is string path ? Path.GetDirectoryName(path) : null;
    public string TargetHint => string.IsNullOrWhiteSpace(OutputDirectory) ? "请选择输出文件夹" : SeparateArchives ? $"分别输出到 {OutputDirectory}，名称见清单" : $"{OutputDirectory}{Path.DirectorySeparatorChar}{ArchiveName}";
    public string InputSummary => _plan is null ? $"已选择 {Inputs.Count} 个顶层项" : $"{_plan.FileCount} 个文件 · {_plan.Groups.Count} 个输出组 · {_plan.TotalBytes:N0} 字节";
    public DocumentPresentationState Presentation => _presentation;
    public event EventHandler? PresentationChanged;

    public PackDocument(IPackBatchService service, IDocumentLifetime lifetime)
    {
        _service = service; _lifetime = lifetime;
        _hostClosing = lifetime.ClosingToken.Register(() => _closing.Cancel());
        Inputs.CollectionChanged += (_, _) => InvalidatePlan();
    }
    public ValueTask InitializeAsync(DocumentActivation activation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested(); _closing.Token.ThrowIfCancellationRequested();
        if (activation is not NewDocumentActivation) throw new NotSupportedException("压缩任务不保存或恢复历史。");
        if (!string.IsNullOrWhiteSpace(activation.Title)) _presentation = new(activation.Title);
        PresentationChanged?.Invoke(this, EventArgs.Empty);
        return ValueTask.CompletedTask;
    }
    partial void OnArchiveNameChanged(string value) { if (!_suggesting) _nameChosen = true; InvalidatePlan(); }
    partial void OnOutputDirectoryChanged(string value) { if (!_suggesting) _directoryChosen = true; InvalidatePlan(); }
    partial void OnIsBusyChanged(bool value) { OnPropertyChanged(nameof(CanEdit)); NotifyCommands(); }
    partial void OnIsCancellingChanged(bool value) => NotifyCommands();
    partial void OnBatchResultChanged(PackBatchResult? value)
    {
        OnPropertyChanged(nameof(CurrentResult)); OnPropertyChanged(nameof(HasOutput));
        OnPropertyChanged(nameof(HasRetryableGroups)); OnPropertyChanged(nameof(HasUnfinishedGroups));
        OnPropertyChanged(nameof(HasFeedback)); OnPropertyChanged(nameof(ResultDirectory)); NotifyCommands();
    }
    private void InvalidatePlan()
    {
        ++_revision; _plan = null; RootMappings.Clear(); EntryMappings.Clear(); ExcludedMappings.Clear();
        OnPropertyChanged(nameof(TargetHint)); OnPropertyChanged(nameof(ExclusionSummary)); NotifyInput();
    }
    private void NotifyInput() { OnPropertyChanged(nameof(HasInputs)); OnPropertyChanged(nameof(HasFeedback)); OnPropertyChanged(nameof(InputSummary)); NotifyCommands(); }
    private void NotifyCommands()
    {
        StartCommand.NotifyCanExecuteChanged(); CancelCommand.NotifyCanExecuteChanged();
        RemoveSelectedCommand.NotifyCanExecuteChanged(); ClearCommand.NotifyCanExecuteChanged();
        PreviewCommand.NotifyCanExecuteChanged(); RetryFailedCommand.NotifyCanExecuteChanged();
        PrepareUnfinishedCommand.NotifyCanExecuteChanged();
    }
    private bool CanStart() => CanEdit && HasInputs && !string.IsNullOrWhiteSpace(OutputDirectory) && (SeparateArchives || !string.IsNullOrWhiteSpace(ArchiveName));
    private bool CanCancel() => IsBusy && !IsClosed && !IsCancelling;

    /// <summary>添加后只准备清单，让重叠合并与根名编号在点击开始之前可检查；选择器和拖放共用此入口。</summary>
    public async Task AddPathsAsync(IEnumerable<string> paths)
    {
        if (!CanEdit) return;
        try
        {
            foreach (var path in paths)
            {
                if (Inputs.Count >= 1000) { Message = "最多添加 1000 个顶层项。"; break; }
                var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
                if (!Inputs.Any(i => string.Equals(i.Path, full, StringComparison.OrdinalIgnoreCase))) Inputs.Add(new(full));
            }
            SuggestTarget();
            if (CanStart()) await RunAsync(previewOnly: true);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or IOException)
        { if (!IsClosed) Message = "无法添加路径，请重新选择文件或文件夹。"; }
    }
    private void SuggestTarget()
    {
        _suggesting = true;
        try
        {
            if (!_nameChosen)
            {
                var first = Inputs.FirstOrDefault();
                var stem = first is null ? "资料" : Path.GetFileNameWithoutExtension(first.Path);
                if (first is not null && Directory.Exists(first.Path)) stem = Path.GetFileName(first.Path);
                if (string.IsNullOrWhiteSpace(stem)) stem = "资料";
                ArchiveName = (stem.Length > 100 ? stem[..100] : stem) + CreationCapability.Extension;
            }
            if (!_directoryChosen)
            {
                var parents = Inputs.Select(i => Path.GetDirectoryName(i.Path)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                OutputDirectory = parents.Length == 1 ? parents[0] ?? "" : "";
            }
        }
        finally { _suggesting = false; }
    }
    public void ChooseOutputDirectory(string path)
    {
        if (!CanEdit) return;
        _directoryChosen = true; OutputDirectory = path;
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private Task StartAsync() => RunAsync(previewOnly: false);

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() { IsCancelling = true; _operation?.Cancel(); Message = "正在取消，等待写入与清理退出。"; }
    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void RemoveSelected()
    {
        if (!CanEdit) return;
        foreach (var item in Inputs.Where(i => i.IsSelected).ToArray()) Inputs.Remove(item);
        RootMappings.Clear(); SuggestTarget();
    }
    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void Clear()
    {
        if (!CanEdit) return;
        ReleaseIdleSession();
        PreviousOutputs.Clear(); _executedPlan = null;
        Inputs.Clear(); RootMappings.Clear(); BatchResult = null; GroupResults.Clear(); ResetOptions(); ArchiveName = ""; OutputDirectory = "";
        _nameChosen = false; _directoryChosen = false; DetailsExpanded = false;
        CurrentEntry = ""; Summary = "尚未开始"; Message = "当前任务已清空。";
    }
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
    public ValueTask DisposeAsync() => new(_disposeTask ??= CloseAsync());
    private async Task CloseAsync()
    {
        _closed = true; ++_generation; _closing.Cancel(); _hostClosing.Dispose();
        try { await _background.ConfigureAwait(false); } catch (Exception) { /* 命令负责呈现工作错误，关闭只负责排空。 */ }
        if (_session is not null) await _session.DisposeAsync().ConfigureAwait(false);
        ClearSecretFields();
        _closing.Dispose();
    }
}

public sealed partial class PackInputItem(string path) : ObservableObject
{
    public string Path { get; } = path;
    public string Name => System.IO.Path.GetFileName(Path);
    [ObservableProperty] private bool _isSelected;
}

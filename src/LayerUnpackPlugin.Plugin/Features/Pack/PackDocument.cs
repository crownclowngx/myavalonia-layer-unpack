using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LayerUnpackPlugin.Headless.Application;
using LayerUnpackPlugin.Headless.Contracts;
using MyAvaloniaManagement.PluginSdk;

namespace LayerUnpackPlugin.Features.Pack;

/// <summary>一次合包任务的呈现与生命周期。只把用户选择交给 Headless，不在 UI 扫描目录或编写 ZIP。</summary>
public sealed partial class PackDocument : ObservableObject, IPluginDocument, IDisposable, IAsyncDisposable
{
    private readonly IPackService _service;
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
    private PackPlan? _plan;
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
    [ObservableProperty] private PackResult? _currentResult;

    public ObservableCollection<PackInputItem> Inputs { get; } = [];
    public ObservableCollection<string> RootMappings { get; } = [];
    public bool CanEdit => !IsBusy && !IsClosed;
    public bool IsClosed => _closed || _lifetime.IsClosing;
    public bool HasInputs => Inputs.Count > 0;
    public bool HasOutput => CurrentResult?.OutputPath is not null;
    public bool HasFeedback => HasInputs || CurrentResult is not null;
    public string? ResultDirectory => CurrentResult?.OutputPath is string path ? Path.GetDirectoryName(path) : null;
    public string TargetHint => string.IsNullOrWhiteSpace(OutputDirectory) ? "请选择输出文件夹" : $"{OutputDirectory}{Path.DirectorySeparatorChar}{ArchiveName}";
    public string InputSummary => _plan is null ? $"已选择 {Inputs.Count} 个顶层项" : $"{_plan.FileCount} 个文件 · {_plan.Entries.Count(e => e.IsDirectory)} 个目录 · {_plan.TotalBytes:N0} 字节";
    public DocumentPresentationState Presentation => _presentation;
    public event EventHandler? PresentationChanged;

    public PackDocument(IPackService service, IDocumentLifetime lifetime)
    {
        _service = service; _lifetime = lifetime;
        _hostClosing = lifetime.ClosingToken.Register(() => _closing.Cancel());
        Inputs.CollectionChanged += (_, _) => { _plan = null; NotifyInput(); };
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
    partial void OnCurrentResultChanged(PackResult? value) { OnPropertyChanged(nameof(HasOutput)); OnPropertyChanged(nameof(HasFeedback)); OnPropertyChanged(nameof(ResultDirectory)); }
    private void InvalidatePlan() { _plan = null; RootMappings.Clear(); OnPropertyChanged(nameof(TargetHint)); NotifyInput(); }
    private void NotifyInput() { OnPropertyChanged(nameof(HasInputs)); OnPropertyChanged(nameof(HasFeedback)); OnPropertyChanged(nameof(InputSummary)); NotifyCommands(); }
    private void NotifyCommands()
    {
        StartCommand.NotifyCanExecuteChanged(); CancelCommand.NotifyCanExecuteChanged();
        RemoveSelectedCommand.NotifyCanExecuteChanged(); ClearCommand.NotifyCanExecuteChanged();
    }
    private bool CanStart() => CanEdit && HasInputs && !string.IsNullOrWhiteSpace(OutputDirectory) && !string.IsNullOrWhiteSpace(ArchiveName);
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
                ArchiveName = (stem.Length > 100 ? stem[..100] : stem) + ".zip";
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

    private async Task RunAsync(bool previewOnly)
    {
        if (!CanEdit) return;
        IsBusy = true; IsCancelling = false; IsIndeterminate = true; ProgressValue = 0;
        var generation = ++_generation;
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(_closing.Token);
        _operation = operation;
        try
        {
            // 新一次尝试的反馈必须属于本次操作；参数验证失败也不能残留上一产物的成功按钮。
            CurrentResult = null; Summary = previewOnly ? "正在准备清单" : "正在创建 ZIP";
            CurrentEntry = "";
            if (ArchiveName != Path.GetFileName(ArchiveName)) throw new PackValidationException("名称只能是一个 ZIP 文件名，位置请填写在输出文件夹中。");
            var request = new PackRequest(Inputs.Select(i => i.Path), Path.Combine(OutputDirectory, ArchiveName));
            // UI 正常会禁用运行参数；仍核对缓存与请求，防止程序调用或绑定更新导致下一次误用旧目标。
            var cachedPlan = !previewOnly && _plan is not null &&
                string.Equals(_plan.Request.OutputPath, request.OutputPath, StringComparison.OrdinalIgnoreCase) &&
                _plan.Request.Inputs.Order(StringComparer.OrdinalIgnoreCase).SequenceEqual(request.Inputs.Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase)
                ? _plan : null;
            var progress = new Progress<PackProgress>(p =>
            {
                if (IsClosed || generation != _generation || !IsBusy) return;
                CurrentEntry = p.CurrentEntry ?? "正在完成归档";
                IsIndeterminate = p.State != PackState.Writing || p.TotalBytes == 0;
                ProgressValue = p.TotalBytes == 0 ? 0 : Math.Clamp(p.ReadBytes * 100.0 / p.TotalBytes, 0, 100);
                Summary = p.State switch { PackState.Scanning => "正在读取输入清单", PackState.Finalizing => "正在完成归档并提交", _ => $"已处理 {p.EntriesDone} / {p.TotalEntries} 项 · {p.ReadBytes:N0} 字节" };
            });
            // 后台只引用冻结请求，关闭排空此任务即可；绝不能等待必须回 UI 线程的整个命令续体。
            var work = Task.Run(async () =>
            {
                var plan = cachedPlan ?? await _service.PrepareAsync(request, progress, operation.Token).ConfigureAwait(false);
                // 首次得知根名变化／输入合并时先呈现清单；普通默认路径已在添加时准备，不增加额外点击。
                var needsMappingReview = cachedPlan is null && (plan.MergedInputs > 0 || plan.Roots.Any(r => r.EntryName != Path.GetFileName(r.SourcePath)));
                var result = previewOnly || needsMappingReview ? null : await _service.ExecuteAsync(plan, progress, operation.Token).ConfigureAwait(false);
                return (plan, result);
            });
            _background = work;
            var outcome = await work;
            if (IsClosed || generation != _generation) return;
            _plan = outcome.plan;
            RootMappings.Clear();
            foreach (var root in _plan.Roots) RootMappings.Add($"{root.SourcePath} → {root.EntryName}{(root.IsDirectory ? "/" : "")}");
            OnPropertyChanged(nameof(InputSummary));
            CurrentResult = outcome.result;
            CurrentEntry = "";
            if (outcome.result is null)
            {
                Summary = "清单已准备，可以开始压缩";
                Message = "文件已就绪，按标准设置生成 ZIP。";
                if (_plan.MergedInputs > 0) Message += $"合并 {_plan.MergedInputs} 个重复或已包含的输入。";
                if (_plan.ExcludedOutputs > 0) Message += $"排除 {_plan.ExcludedOutputs} 个目标文件。";
            }
            else
            {
                var result = outcome.result;
                Summary = result.State switch { PackState.Completed => $"已完成 · {result.FileCount} 个文件 · ZIP {result.ArchiveBytes:N0} 字节", PackState.Cancelled => "已取消", _ => "创建失败" };
                Message = result.State == PackState.Completed ? $"{result.OutputPath}\n源文件 {_plan.TotalBytes:N0} 字节 · 用时 {result.Elapsed.TotalSeconds:F1} 秒" : result.Error?.Message ?? "已取消，未提交 ZIP。";
                if (result.CleanupWarning is not null) Message += "\n临时文件未能清理：" + result.CleanupWarning;
                // 失败后再次开始是新一次明确操作，重新准备，不能把失效来源清单当成可恢复快照。
                if (result.State != PackState.Completed) _plan = null;
            }
        }
        catch (OperationCanceledException) { if (!IsClosed) { Summary = "已取消"; Message = "输入准备已取消。"; } }
        catch (Exception e) when (e is PackValidationException or PackFailureException or ArgumentException)
        { if (!IsClosed) { _plan = null; Summary = "尚未创建"; Message = e.Message; } }
        catch (Exception) { if (!IsClosed) { _plan = null; Summary = "创建未完成"; Message = "请检查输入和输出位置后重试。"; } }
        finally
        {
            ++_generation; _operation = null;
            if (!IsClosed) { IsCancelling = false; IsBusy = false; }
        }
    }
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
        Inputs.Clear(); RootMappings.Clear(); CurrentResult = null; ArchiveName = ""; OutputDirectory = "";
        _nameChosen = false; _directoryChosen = false; DetailsExpanded = false;
        CurrentEntry = ""; Summary = "尚未开始"; Message = "当前任务已清空。";
    }
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
    public ValueTask DisposeAsync() => new(_disposeTask ??= CloseAsync());
    private async Task CloseAsync()
    {
        _closed = true; ++_generation; _closing.Cancel(); _hostClosing.Dispose();
        try { await _background.ConfigureAwait(false); } catch (Exception) { /* 命令负责呈现工作错误，关闭只负责排空。 */ }
        _closing.Dispose();
    }
}

public sealed partial class PackInputItem(string path) : ObservableObject
{
    public string Path { get; } = path;
    public string Name => System.IO.Path.GetFileName(Path);
    [ObservableProperty] private bool _isSelected;
}

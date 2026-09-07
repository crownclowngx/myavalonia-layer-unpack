using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LayerUnpackPlugin.Headless.Application;
using LayerUnpackPlugin.Headless.Contracts;
using LayerUnpackPlugin.Headless.Domain;
using MyAvaloniaManagement.PluginSdk;

namespace LayerUnpackPlugin.Features.Unpack;

/// <summary>临时解压工作区：只编排用户意图与呈现，不包含解码、递归或密码轮询规则。</summary>
/// <remarks>关闭取消并排空 Headless 工作；密码、输入与结果不进入 Host 内容持久化。</remarks>
public sealed partial class UnpackDocument : ObservableObject, IPluginDocument, IDisposable, IAsyncDisposable
{
    private readonly IUnpackService _service;
    private readonly IDocumentLifetime _lifetime;
    private readonly CancellationTokenSource _closing = new();
    private readonly CancellationTokenRegistration _hostClosing;
    private readonly Dictionary<Guid, ArchiveNodeViewModel> _nodeIndex = [];
    private UnpackSession? _session;
    private CancellationTokenSource? _operation;
    private long _generation;
    private bool _closed;
    private Task _backgroundWork = Task.CompletedTask;
    private Task? _disposeTask;
    private DocumentPresentationState _presentation = new("解压任务");

    [ObservableProperty] private string _outputDirectory = "";
    [ObservableProperty] private int _maxDepth = 1;
    [ObservableProperty] private string _passwordText = "";
    [ObservableProperty] private string _message = "添加压缩包，选择输出位置后开始。";
    [ObservableProperty] private string _summary = "尚未开始";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private ArchiveNodeViewModel? _selectedNode;
    [ObservableProperty] private NameEncodingOption _selectedNameEncoding = NameEncodings[0];

    public static IReadOnlyList<NameEncodingOption> NameEncodings { get; } = Array.AsReadOnly(new[]
    {
        new NameEncodingOption(LegacyNameEncoding.Gb18030, "简体中文 GB18030"),
        new NameEncodingOption(LegacyNameEncoding.Utf8, "UTF-8"),
        new NameEncodingOption(LegacyNameEncoding.Cp437, "西欧 CP437"),
        new NameEncodingOption(LegacyNameEncoding.Cp866, "俄文 CP866")
    });

    public ObservableCollection<InputItem> Inputs { get; } = [];
    public ObservableCollection<ArchiveNodeViewModel> Roots { get; } = [];
    public bool CanEdit => !IsBusy && !IsClosed;
    public bool IsClosed => _closed || _lifetime.IsClosing;
    public DocumentPresentationState Presentation => _presentation;
    public event EventHandler? PresentationChanged;
    public UnpackResult? CurrentResult => _session?.Snapshot;

    public UnpackDocument(IUnpackService service, IDocumentLifetime lifetime)
    {
        _service = service;
        _lifetime = lifetime;
        _hostClosing = lifetime.ClosingToken.Register(() => _closing.Cancel());
        Inputs.CollectionChanged += (_, _) => { NotifyPresentation(); NotifyCommands(); };
    }

    public ValueTask InitializeAsync(DocumentActivation activation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(activation);
        cancellationToken.ThrowIfCancellationRequested();
        _closing.Token.ThrowIfCancellationRequested();
        if (activation is not NewDocumentActivation) throw new NotSupportedException("解压任务不保存或恢复历史内容。");
        if (!string.IsNullOrWhiteSpace(activation.Title)) _presentation = new DocumentPresentationState(activation.Title);
        PresentationChanged?.Invoke(this, EventArgs.Empty);
        return ValueTask.CompletedTask;
    }

    partial void OnIsBusyChanged(bool value) { OnPropertyChanged(nameof(CanEdit)); NotifyPresentation(); NotifyCommands(); }
    partial void OnOutputDirectoryChanged(string value)
    {
        if (!_updatingSuggestion) _outputChosen = true;
        OnPropertyChanged(nameof(OutputHint));
        NotifyCommands();
    }
    partial void OnSelectedNodeChanged(ArchiveNodeViewModel? value) { OnPropertyChanged(nameof(HasSelectedNode)); NotifyCommands(); }
    private bool CanStart() => CanEdit && Inputs.Count > 0 && !string.IsNullOrWhiteSpace(OutputDirectory);
    private bool CanRetry() => CanEdit && _session is not null && !_session.Snapshot.RetryBlocked && SelectedNode?.CanRetry == true;
    private bool CanCancel() => IsBusy && !IsClosed && !IsCancelling;

    private void NotifyCommands()
    {
        StartCommand.NotifyCanExecuteChanged(); CancelCommand.NotifyCanExecuteChanged();
        RetryCommand.NotifyCanExecuteChanged(); ClearCommand.NotifyCanExecuteChanged();
        RemoveSelectedCommand.NotifyCanExecuteChanged();
        ShowPasswordEntryCommand.NotifyCanExecuteChanged(); PrepareNewBatchCommand.NotifyCanExecuteChanged();
    }

    /// <summary>文件选择、拖放与目录扫描共用同一入口，关闭后的扫描结果不能再写入界面。</summary>
    public async Task AddPathsAsync(IEnumerable<string> paths)
    {
        if (!CanEdit) return;
        IsScanning = true;
        IsCancelling = false;
        CurrentItemText = "";
        IsBusy = true;
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(_closing.Token);
        _operation = operation;
        try
        {
            var inputs = Inputs.Select(i => i.Path).Concat(paths).ToArray();
            var output = string.IsNullOrWhiteSpace(OutputDirectory) ? null : OutputDirectory;
            var discovery = Task.Run(() => InputDiscovery.DiscoverAsync(inputs, output, cancellationToken: operation.Token));
            _backgroundWork = discovery;
            var found = await discovery;
            if (IsClosed) return;
            Inputs.Clear();
            foreach (var path in found.Files) Inputs.Add(new InputItem(path));
            UpdateOutputSuggestion();
            Message = found.Warnings.Count == 0 ? $"已添加 {Inputs.Count} 个压缩包。" : string.Join(Environment.NewLine, found.Warnings.Take(5));
        }
        catch (OperationCanceledException) { if (!IsClosed) Message = "已取消输入扫描。"; }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException)
        { if (!IsClosed) Message = "无法读取输入，请检查路径和访问权限。"; }
        finally
        {
            _operation = null;
            if (!IsClosed) { IsScanning = false; IsCancelling = false; IsBusy = false; }
        }
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private Task StartAsync() => RunOperationAsync(false);
    [RelayCommand(CanExecute = nameof(CanRetry))]
    private Task RetryAsync() => RunOperationAsync(true);

    private async Task RunOperationAsync(bool retry)
    {
        if (!CanEdit) return;
        IsCancelling = false;
        IsBusy = true;
        var generation = ++_generation;
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(_closing.Token);
        _operation = operation;
        try
        {
            var passwords = PasswordPool.ParseLines(PasswordText);
            if (!retry)
            {
                var request = new UnpackRequest(Inputs.Select(i => i.Path), OutputDirectory, MaxDepth, passwords,
                    legacyNameEncoding: SelectedNameEncoding.Encoding);
                // 新参数先验证，成功创建下一会话后才释放旧会话；错误参数不会丢掉用户正在检查的结果。
                var next = _service.CreateSession(request);
                if (_session is not null) await _session.DisposeAsync();
                _session = next;
                _batchOutputDirectory = request.OutputDirectory;
                Roots.Clear(); Issues.Clear(); _nodeIndex.Clear(); SelectedNode = null; PasswordText = "";
                ResultOutputPath = null; CurrentItemText = "";
                NotifyPresentation();
            }
            var session = _session ?? throw new InvalidOperationException("没有当前批次。");
            var selectedId = SelectedNode?.Id;
            var progress = new Progress<UnpackProgress>(p =>
            {
                if (!IsClosed && generation == _generation && IsBusy) ApplySnapshot(p.Snapshot);
            });
            Message = retry ? "正在重试所选失败项。" : "正在解压；密码只在当前批次内存中使用。";
            var work = Task.Run(() => retry
                ? session.RetryAsync([selectedId!.Value], passwords, progress, operation.Token)
                : session.ExecuteAsync(progress, operation.Token));
            _backgroundWork = work;
            var result = await work;
            if (IsClosed || generation != _generation) return;
            ApplySnapshot(result);
            PasswordText = "";
            Message = result.State switch
            {
                BatchState.Completed => "处理完成，可以查看输出目录。",
                BatchState.PartialFailure => "部分项目未完成。选择失败项查看原因，可恢复项目可补充密码后重试。",
                BatchState.Cancelled => "已取消，已成功提交的文件仍可使用。",
                _ => "处理未完成，请查看节点原因。"
            };
        }
        catch (UnpackValidationException e)
        {
            if (!IsClosed) Message = (e.Field switch
            {
                "Inputs" => "输入",
                "OutputDirectory" => "输出目录",
                "MaxDepth" => "深度",
                "Passwords" => "候选密码",
                "LegacyNameEncoding" => "文件名编码",
                _ => "资源限制"
            }) + "：" + e.Message;
        }
        catch (OperationCanceledException) { if (!IsClosed) Message = "已取消。"; }
        catch (InvalidOperationException e) { if (!IsClosed) Message = e.Message; }
        catch (Exception) { if (!IsClosed) Message = "执行失败，请检查输入、输出目录和当前任务状态。"; }
        finally
        {
            // Progress 回调可能晚于最终结果；推进运行代次后，排队回调不能覆盖终态。
            ++_generation;
            _operation = null;
            if (!IsClosed) { IsCancelling = false; IsBusy = false; }
        }
    }

    private void ApplySnapshot(UnpackResult result)
    {
        foreach (var node in result.Nodes)
        {
            if (!_nodeIndex.TryGetValue(node.Id, out var item))
            {
                item = new ArchiveNodeViewModel(node.Id);
                _nodeIndex.Add(node.Id, item);
                if (node.ParentId is Guid parent && _nodeIndex.TryGetValue(parent, out var parentItem)) parentItem.Children.Add(item);
                else Roots.Add(item);
            }
            item.Apply(node);
        }
        Summary = $"已发现 {result.Nodes.Count} 项 · 已解压 {result.Succeeded} 项";
        // 默认摘要只突出真实发生的情况，避免普通成功批次被一排零值诊断淹没。
        if (result.Failed > 0) Summary += $" · 失败 {result.Failed} 项";
        if (result.DiscoveryFailures > 0) Summary += $" · 发现中断 {result.DiscoveryFailures} 项";
        if (result.StoppedByDepth > 0) Summary += $" · 按深度停止 {result.StoppedByDepth} 项";
        if (result.State == BatchState.Cancelled)
            Summary += $" · 已取消 {result.Nodes.Count(n => n.State == NodeState.Cancelled)} 项 · 未执行 {result.Nodes.Count(n => n.State == NodeState.NotRun)} 项";
        OnPropertyChanged(nameof(CurrentResult));
        UpdateResultPresentation(result);
        NotifyCommands();
    }

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() { IsCancelling = true; _operation?.Cancel(); Message = "正在取消，等待当前写入和清理退出。"; }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void RemoveSelected()
    {
        if (!CanEdit) return;
        foreach (var item in Inputs.Where(i => i.IsSelected).ToArray()) Inputs.Remove(item);
        UpdateOutputSuggestion();
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private async Task ClearAsync()
    {
        if (!CanEdit) return;
        if (_session is not null) await _session.DisposeAsync();
        _session = null;
        Inputs.Clear(); Roots.Clear(); Issues.Clear(); _nodeIndex.Clear(); SelectedNode = null;
        PasswordText = ""; OutputDirectory = ""; MaxDepth = 1;
        _outputChosen = false; _recursiveDepth = 2; _batchOutputDirectory = null;
        OnPropertyChanged(nameof(RecursiveDepth));
        ResultOutputPath = null; CurrentItemText = "";
        InputsExpanded = false; PasswordsExpanded = false; OptionsExpanded = false; ResultsExpanded = false;
        SelectedNameEncoding = NameEncodings[0];
        Message = "当前批次已清空。"; Summary = "尚未开始";
        OnPropertyChanged(nameof(CurrentResult)); OnPropertyChanged(nameof(OutputHint)); NotifyPresentation();
        NotifyCommands();
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    public ValueTask DisposeAsync() => new(_disposeTask ??= CloseCoreAsync());

    private async Task CloseCoreAsync()
    {
        _closed = true; ++_generation; _closing.Cancel();
        PasswordText = ""; _hostClosing.Dispose();
        // 只排空不依赖 UI 的工作任务。排空整个命令续体会与 Host 同步释放 UI Scope 形成死锁。
        if (_session is not null) await _session.DisposeAsync().ConfigureAwait(false);
        try { await _backgroundWork.ConfigureAwait(false); } catch (Exception) { /* 工作结果由关闭前的命令观察；关闭不再次传播失败。 */ }
        _closing.Dispose();
    }
}

public sealed record NameEncodingOption(LegacyNameEncoding Encoding, string Label)
{
    public override string ToString() => Label;
}

public sealed partial class InputItem(string path) : ObservableObject
{
    public string Path { get; } = path;
    public string Name { get; } = System.IO.Path.GetFileName(path);
    [ObservableProperty] private bool _isSelected;
}

/// <summary>结果树只投影快照，节点对象保持稳定，以保留用户选择和展开状态。</summary>
public sealed partial class ArchiveNodeViewModel(Guid id) : ObservableObject
{
    public Guid Id { get; } = id;
    public ObservableCollection<ArchiveNodeViewModel> Children { get; } = [];
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string _source = "";
    [ObservableProperty] private string _details = "";
    [ObservableProperty] private string? _outputPath;
    [ObservableProperty] private bool _canRetry;
    [ObservableProperty] private UnpackError? _errorCode;
    public bool HasOutput => OutputPath is not null;
    public bool NeedsPassword => ErrorCode == UnpackError.PasswordRequiredOrInvalid;
    public bool RequiresNewBatch => ErrorCode is UnpackError.InvalidNameEncoding or UnpackError.InputChanged
        or UnpackError.UnsupportedFormat or UnpackError.UnsupportedEncryption or UnpackError.MissingVolume or UnpackError.BudgetExceeded;
    partial void OnOutputPathChanged(string? value) => OnPropertyChanged(nameof(HasOutput));
    partial void OnErrorCodeChanged(UnpackError? value)
    {
        OnPropertyChanged(nameof(NeedsPassword)); OnPropertyChanged(nameof(RequiresNewBatch));
    }

    internal void Apply(ArchiveNodeResult node)
    {
        Name = System.IO.Path.GetFileName(node.SourcePath); Source = node.SourcePath; OutputPath = node.OutputDirectory;
        Status = node.State switch
        {
            NodeState.Queued => "排队",
            NodeState.Probing => "探测",
            NodeState.Extracting => "解压中",
            NodeState.Extracted => node.Warning is not null ? "已解压（有提示）" : node.Error is not null ? "已解压（发现中断）" : "已解压",
            NodeState.Failed => "失败",
            NodeState.Cancelled => "已取消",
            NodeState.DepthLimit => "按深度停止",
            _ => "未执行"
        };
        // 可重试性完全服从 Headless；界面仅据错误类型提供补密或新批次的导航入口。
        CanRetry = node.CanRetry; ErrorCode = node.Error?.Code;
        Details = $"第 {node.Depth} 层 · {node.Format ?? "尚未识别"}\n{Status}\n{node.Error?.Message ?? ""}" +
            (node.Warning is null ? "" : "\n" + node.Warning) +
            (node.CleanupWarnings.Count > 0 ? "\n临时输出清理失败：\n" + string.Join("\n", node.CleanupWarnings) : "");
    }
}

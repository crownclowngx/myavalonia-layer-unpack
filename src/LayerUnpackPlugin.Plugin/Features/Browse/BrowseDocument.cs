using System.Collections.ObjectModel;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LayerUnpackPlugin.Features.Unpack;
using LayerUnpackPlugin.Headless.Application;
using LayerUnpackPlugin.Headless.Contracts;
using MyAvaloniaManagement.PluginSdk;

namespace LayerUnpackPlugin.Features.Browse;

/// <summary>单归档的临时浏览页。目录与选择规则交给 Headless，页面只负责命令、分页呈现和会话生命周期。</summary>
public sealed partial class BrowseDocument : ObservableObject, IPluginDocument, IDisposable, IAsyncDisposable
{
    private readonly IArchiveBrowseService _service;
    private readonly IUnpackService _unpackService;
    private readonly IDocumentLifetime _lifetime;
    private readonly CancellationTokenSource _closing = new();
    private readonly CancellationTokenRegistration _hostClosing;
    private CancellationTokenSource? _operation;
    private IArchiveBrowseSession? _session;
    private ArchiveSelection? _selection;
    private Task _background = Task.CompletedTask;
    private Task? _disposeTask;
    private long _generation;
    private bool _closed;
    private bool _outputChosen;
    private bool _suggesting;
    private int _offset;
    private BrowsePage? _page;
    private OwnedUnpackTask? _unpackTask;
    private DocumentPresentationState _presentation = new("浏览任务");

    [ObservableProperty] private string _sourcePath = "";
    [ObservableProperty] private string _outputDirectory = "";
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty]
    [property: JsonIgnore]
    private string _passwordText = "";
    [ObservableProperty] private NameEncodingOption _selectedNameEncoding = UnpackDocument.NameEncodings[0];
    [ObservableProperty] private string _message = "选择或拖入一个 ZIP，查看目录后提取所需内容。";
    [ObservableProperty] private string _summary = "尚未加载归档";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isCancelling;
    [ObservableProperty] private bool _passwordExpanded;
    [ObservableProperty] private bool _detailsExpanded;
    [ObservableProperty] private bool _showUnpackTask;
    [ObservableProperty] private BrowseEntryItem? _selectedRow;
    [ObservableProperty] private BrowseExtractResult? _currentResult;
    public ObservableCollection<BrowseEntryItem> Rows { get; } = [];
    public ObservableCollection<string> Outputs { get; } = [];
    public static IReadOnlyList<NameEncodingOption> NameEncodings => UnpackDocument.NameEncodings;
    public static IReadOnlyList<BrowseCapability> Capabilities => BrowseCapabilities.All;
    public bool IsClosed => _closed || _lifetime.IsClosing;
    public bool CanEdit => !IsBusy && !IsClosed && !ShowCheckTask;
    public bool HasCatalog => _session is { IsInvalidated: false } && _selection is not null;
    public bool HasOutputs => Outputs.Count > 0;
    public int SelectedCount => _selection?.Count ?? 0;
    public string SelectionSummary => $"已选 {_selection?.FileCount ?? 0} 个文件、{SelectedCount - (_selection?.FileCount ?? 0)} 个目录条目（包含搜索外的选择）";
    public string PageSummary => _page is null ? "目录尚未加载" : $"匹配 {_page.TotalMatches} 行 · 本页 {(_page.Entries.Count == 0 ? 0 : _offset + 1)}–{_offset + _page.Entries.Count}";
    public string ArchiveName => string.IsNullOrWhiteSpace(SourcePath) ? "浏览任务" : Path.GetFileName(SourcePath);
    public string Details => SelectedRow?.Details ?? "点击一行查看包内路径、条目身份及限制。";
    public UnpackDocument? UnpackTask => _unpackTask?.Document;
    public DocumentPresentationState Presentation => _presentation;
    public event EventHandler? PresentationChanged;

    public BrowseDocument(IArchiveBrowseService service, IUnpackService unpackService, IDocumentLifetime lifetime, IArchiveCheckService? checkService = null)
    {
        _service = service; _unpackService = unpackService; _lifetime = lifetime;
        _checkService = checkService ?? new ArchiveCheckService();
        _hostClosing = lifetime.ClosingToken.Register(() => _closing.Cancel());
    }
    public ValueTask InitializeAsync(DocumentActivation activation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested(); _closing.Token.ThrowIfCancellationRequested();
        if (activation is not NewDocumentActivation) throw new NotSupportedException("浏览任务不保存目录、选择或密码历史。");
        if (!string.IsNullOrWhiteSpace(activation.Title)) _presentation = new(activation.Title);
        PresentationChanged?.Invoke(this, EventArgs.Empty); return ValueTask.CompletedTask;
    }
    partial void OnSourcePathChanged(string value)
    {
        PasswordText = ""; OnPropertyChanged(nameof(ArchiveName));
        if (CanEdit) ResetCatalog();
        NotifyCommands();
    }
    partial void OnSelectedNameEncodingChanged(NameEncodingOption value) { if (CanEdit) ResetCatalog(); }
    partial void OnOutputDirectoryChanged(string value) { if (!_suggesting) _outputChosen = true; NotifyCommands(); }
    partial void OnSearchTextChanged(string value) { _offset = 0; RefreshPage(); }
    partial void OnSelectedRowChanged(BrowseEntryItem? value) => OnPropertyChanged(nameof(Details));
    partial void OnIsBusyChanged(bool value) { OnPropertyChanged(nameof(CanEdit)); NotifyCommands(); }
    partial void OnIsCancellingChanged(bool value) => NotifyCommands();
    private bool CanLoad() => CanEdit && !string.IsNullOrWhiteSpace(SourcePath);
    private bool CanExtract() => CanLoad() && HasCatalog && SelectedCount > 0 && !string.IsNullOrWhiteSpace(OutputDirectory) &&
        CurrentResult?.CleanupWarning is null && CurrentResult?.Error?.Code != UnpackError.BudgetExceeded;
    private bool CanCancel() => IsBusy && !IsCancelling && !IsClosed;
    private bool CanPrevious() => CanEdit && _offset > 0;
    private bool CanNext() => CanEdit && _page?.HasNext == true;
    private void NotifyCommands()
    {
        CheckArchiveCommand.NotifyCanExecuteChanged(); ReturnFromCheckCommand.NotifyCanExecuteChanged();
        LoadCommand.NotifyCanExecuteChanged(); ExtractCommand.NotifyCanExecuteChanged(); CancelCommand.NotifyCanExecuteChanged();
        PreviousPageCommand.NotifyCanExecuteChanged(); NextPageCommand.NotifyCanExecuteChanged();
        ClearSelectionCommand.NotifyCanExecuteChanged(); ClearCommand.NotifyCanExecuteChanged(); PrepareUnpackCommand.NotifyCanExecuteChanged();
    }
    public async Task OpenPathAsync(string path)
    {
        if (!CanEdit) return;
        SourcePath = path;
        await LoadAsync();
    }
    public void ChooseOutputDirectory(string path) { if (CanEdit) OutputDirectory = path; }

    [RelayCommand(CanExecute = nameof(CanPrevious))]
    private void PreviousPage() { _offset = Math.Max(0, _offset - 200); RefreshPage(); }
    [RelayCommand(CanExecute = nameof(CanNext))]
    private void NextPage() { _offset += 200; RefreshPage(); }
    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void ClearSelection() { _selection?.Clear(); RefreshSelection(); }

    private void RefreshPage()
    {
        Rows.Clear(); SelectedRow = null;
        _page = HasCatalog ? _session!.Catalog.GetPage(SearchText, _offset) : null;
        if (_page is not null)
            foreach (var entry in _page.Entries) Rows.Add(new(entry, SetSelection));
        RefreshSelection(); OnPropertyChanged(nameof(PageSummary));
    }
    private void SetSelection(BrowseEntryItem row, bool selected)
    {
        if (!CanEdit || !HasCatalog) { RefreshSelection(); return; }
        _selection!.SetSelected(row.Entry.Id, selected); RefreshSelection();
    }
    private void RefreshSelection()
    {
        foreach (var row in Rows) row.PresentSelection(_selection is null ? false : _selection.GetState(row.Entry.Id));
        OnPropertyChanged(nameof(SelectedCount)); OnPropertyChanged(nameof(SelectionSummary));
        OnPropertyChanged(nameof(HasCatalog)); NotifyCommands();
    }
    private void ResetCatalog()
    {
        // 只在空闲时释放；每次重新加载都生成新身份，旧目录与旧密码不能自动套用到替换的归档。
        _session?.DisposeAsync().AsTask().GetAwaiter().GetResult(); _session = null; _selection = null;
        _page = null; _offset = 0; Rows.Clear(); SelectedRow = null;
        RefreshSelection(); OnPropertyChanged(nameof(PageSummary));
    }

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        IsCancelling = true; _operation?.Cancel();
        // 准备全部解压输入时，真实扫描属于所承载的原任务；将取消送到该任务，不能只改浏览页文字。
        if (_operation is null && _unpackTask?.Document.CancelCommand.CanExecute(null) == true)
            _unpackTask.Document.CancelCommand.Execute(null);
        Message = "正在取消，等待读取、写入和临时内容清理退出。";
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private async Task ClearAsync()
    {
        await ResetCheckAsync();
        if (!CanEdit) return;
        IsBusy = true;
        if (_unpackTask is not null)
        {
            var work = _unpackTask.DisposeAsync().AsTask(); _background = work; await work;
            _unpackTask = null; OnPropertyChanged(nameof(UnpackTask));
        }
        if (IsClosed) return;
        ShowUnpackTask = false; IsBusy = false;
        ResetCatalog(); PasswordText = ""; SourcePath = ""; SearchText = ""; OutputDirectory = "";
        _outputChosen = false; PasswordExpanded = false; DetailsExpanded = false; CurrentResult = null;
        Outputs.Clear(); OnPropertyChanged(nameof(HasOutputs)); Summary = "尚未加载归档"; Message = "浏览任务已清空，已提交产物仍保留在磁盘。";
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
    public ValueTask DisposeAsync() => new(_disposeTask ??= CloseAsync());
    private async Task CloseAsync()
    {
        _closed = true; ++_generation; _closing.Cancel(); _hostClosing.Dispose();
        if (CheckTask is not null) await CheckTask.DisposeAsync().ConfigureAwait(false);
        try { await _background.ConfigureAwait(false); } catch (Exception) { /* 关闭只排空，错误由当前命令归一化呈现。 */ }
        if (_session is not null) await _session.DisposeAsync().ConfigureAwait(false);
        if (_unpackTask is not null) await _unpackTask.DisposeAsync().ConfigureAwait(false);
        _session = null; _selection = null; PasswordText = ""; _closing.Dispose();
    }
}

/// <summary>行只保留显示数据。三态由完整选择集合计算，搜索与分页重建行时不会清除隐藏选择。</summary>
public sealed partial class BrowseEntryItem(BrowseEntry entry, Action<BrowseEntryItem, bool> changed) : ObservableObject
{
    private bool _presenting;
    public BrowseEntry Entry { get; } = entry;
    public string Label => (Entry.IsDirectory ? "目录 · " : "文件 · ") + (Entry.Path.Length == 0 ? "根目录" : Entry.Path);
    public string SizeText => Entry.IsDirectory ? "目录" : Entry.Size is long size ? $"{size:N0} 字节" : "大小未知";
    public string Note => Entry.Problem?.Message ?? Entry.Warning ?? (Entry.IsEncrypted ? "加密内容：提取时需要密码" : "");
    public bool HasNote => Note.Length > 0;
    public string Details => $"ZIP · {Entry.Path}\n条目序号：{Entry.Id.Ordinal} · {(Entry.IsSynthetic ? "隐含目录" : "中心目录记录")}\n" +
        $"原大小：{Entry.Size?.ToString() ?? "未知"} · 压缩大小：{Entry.CompressedSize?.ToString() ?? "未知"} · 属性：{Entry.Attributes}\n{Note}";
    [ObservableProperty] private bool? _isSelected = false;
    partial void OnIsSelectedChanged(bool? value) { if (!_presenting) changed(this, value != false); }
    public void PresentSelection(bool? selected) { _presenting = true; try { IsSelected = selected; } finally { _presenting = false; } }
}

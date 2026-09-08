using System.Collections.ObjectModel;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LayerUnpackPlugin.Features.Organize;
using LayerUnpackPlugin.Features.Unpack;
using LayerUnpackPlugin.Headless.Application;
using LayerUnpackPlugin.Headless.Contracts;
using LayerUnpackPlugin.Headless.Domain;

namespace LayerUnpackPlugin.Features.Repack;

/// <summary>由来源页面拥有的转换／结果打包子任务。进入时只携带快照，必须显式开始才调用写入用例。
/// 生命周期与来源页面相连，关闭等待后台清理；页面只组装意图，不操作文件系统、递归或密码尝试。</summary>
public sealed partial class RepackDocument : ObservableObject, IDisposable, IAsyncDisposable
{
    private readonly IRepackService _service;
    private readonly UnpackResult? _unpacked;
    private readonly OrganizationResult? _organized;
    private readonly CancellationTokenSource _closing;
    private CancellationTokenSource? _operation;
    private Task _work = Task.CompletedTask;
    private Task? _disposeTask;
    private bool _closed;
    private long _generation;
    private int _revision;
    private int _page;
    public const int PageSize = 100;
    public IReadOnlyList<string> Sources { get; }
    public bool IsConversion => _unpacked is null && _organized is null;
    public string Title => IsConversion ? "转换为 ZIP" : "打包结果";
    public string StartLabel => IsConversion ? "开始转换" : "开始打包";
    public bool IsClosed => _closed || _closing.IsCancellationRequested;
    public bool CanEdit => !IsClosed && !IsBusy;
    public bool HasPlan => Plan is not null;
    public bool HasResult => Result is not null;
    public bool HasOutputs => Outputs.Count > 0;
    public string ContentRule => IsConversion ? ExpandInternalArchives
        ? $"内容结构转换：总解压 {MaxDepth} 层，{SelectedFileTypes.Label}；边界内部包不再展开，是否带入由类型规则决定，按顶层来源分别生成 ZIP。"
        : "每个来源生成一个新 ZIP，保留相对路径、文件内容和空目录；内部压缩包作为普通文件保留。"
        : "只使用带入的已提交清单；来源新增、删除或内容变化会要求重新检查，历史文件不会自动混入。";
    public string MetadataNotice => "不保留原归档时间戳、权限、链接、注释和扩展属性；不支持的特殊条目会报告失败。目标默认不加密。中间内容只用于本任务，结束或取消后清理。";

    [ObservableProperty] private string _outputDirectory;
    [ObservableProperty] private bool _expandInternalArchives;
    [ObservableProperty] private int _maxDepth = 2;
    [ObservableProperty] private OrganizationTypeOption _selectedFileTypes = OrganizationDocument.FileTypes[0];
    [ObservableProperty] private bool _flattenWrappingDirectories;
    [ObservableProperty] private NameEncodingOption _selectedNameEncoding = UnpackDocument.NameEncodings[0];
    [ObservableProperty] private bool _separateArchives;
    [ObservableProperty] private bool _encryptionEnabled;
    [ObservableProperty][property: JsonIgnore] private string _sourcePasswordText = "";
    [ObservableProperty][property: JsonIgnore] private string _targetPassword = "";
    [ObservableProperty][property: JsonIgnore] private string _confirmPassword = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isCancelling;
    [ObservableProperty] private bool _detailsExpanded;
    [ObservableProperty] private bool _optionsExpanded;
    [ObservableProperty] private RepackPlan? _plan;
    [ObservableProperty] private RepackResult? _result;
    [ObservableProperty] private string _summary = "尚未开始";
    [ObservableProperty] private string _message = "检查来源和目标后显式开始。已有来源和成功产物保留。";
    [ObservableProperty] private string _pageSummary = "";
    public ObservableCollection<string> Rows { get; } = [];
    public ObservableCollection<string> Results { get; } = [];
    public ObservableCollection<string> Outputs { get; } = [];

    public RepackDocument(IRepackService service, IEnumerable<string> sources, string outputDirectory, CancellationToken parentClosing)
    {
        _service = service; Sources = Array.AsReadOnly(sources.ToArray()); _outputDirectory = outputDirectory;
        _closing = CancellationTokenSource.CreateLinkedTokenSource(parentClosing);
        Outputs.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasOutputs));
    }
    public RepackDocument(IRepackService service, UnpackResult input, string outputDirectory, CancellationToken parentClosing)
        : this(service, input.Nodes.Where(n => n.ParentId is null).Select(n => n.SourcePath), outputDirectory, parentClosing) => _unpacked = input;
    public RepackDocument(IRepackService service, OrganizationResult input, string outputDirectory, CancellationToken parentClosing)
        : this(service, new[] { input.OutputDirectory! }, outputDirectory, parentClosing) => _organized = input;

    partial void OnOutputDirectoryChanged(string value) => Invalidate();
    partial void OnSeparateArchivesChanged(bool value) => Invalidate();
    partial void OnEncryptionEnabledChanged(bool value) { if (!value) { TargetPassword = ""; ConfirmPassword = ""; } Invalidate(); }
    partial void OnExpandInternalArchivesChanged(bool value) { Invalidate(); OnPropertyChanged(nameof(ContentRule)); }
    partial void OnMaxDepthChanged(int value) => OnPropertyChanged(nameof(ContentRule));
    partial void OnSelectedFileTypesChanged(OrganizationTypeOption value) => OnPropertyChanged(nameof(ContentRule));
    partial void OnIsBusyChanged(bool value) { OnPropertyChanged(nameof(CanEdit)); NotifyCommands(); }
    partial void OnIsCancellingChanged(bool value) => NotifyCommands();
    partial void OnPlanChanged(RepackPlan? value) { _page = 0; RefreshRows(); OnPropertyChanged(nameof(HasPlan)); NotifyCommands(); }
    partial void OnResultChanged(RepackResult? value) => OnPropertyChanged(nameof(HasResult));
    private void Invalidate() { _revision++; Plan = null; NotifyCommands(); }
    private bool CanPreview() => CanEdit && !IsConversion && !string.IsNullOrWhiteSpace(OutputDirectory);
    private bool CanStart() => CanEdit && !string.IsNullOrWhiteSpace(OutputDirectory) && (IsConversion ? Sources.Count > 0 : Plan?.Groups.Count > 0);
    private bool CanCancel() => IsBusy && !IsClosed && !IsCancelling;
    private void NotifyCommands()
    {
        PreviewCommand.NotifyCanExecuteChanged(); StartCommand.NotifyCanExecuteChanged(); CancelCommand.NotifyCanExecuteChanged();
        PreviousPageCommand.NotifyCanExecuteChanged(); NextPageCommand.NotifyCanExecuteChanged();
    }
    [RelayCommand(CanExecute = nameof(CanPreview))] private Task PreviewAsync() => RunAsync(true);
    [RelayCommand(CanExecute = nameof(CanStart))] private Task StartAsync() => RunAsync(false);

    private async Task RunAsync(bool preview)
    {
        if (!CanEdit || (preview ? !CanPreview() : !CanStart())) return;
        IsBusy = true; IsCancelling = false;
        var generation = ++_generation; var revision = _revision;
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(_closing.Token);
        _operation = operation;
        try
        {
            var output = OutputDirectory;
            var grouping = SeparateArchives ? PackGrouping.Separate : PackGrouping.Combined;
            var options = new PackOptions { Encrypt = EncryptionEnabled };
            if (preview)
            {
                Plan = null; Summary = "正在核对提交清单";
                var task = Task.Run(() => _unpacked is not null
                    ? _service.PrepareAsync(_unpacked, output, grouping, options, operation.Token)
                    : _service.PrepareAsync(_organized!, output, grouping, options, operation.Token));
                _work = task;
                var plan = await task;
                if (IsClosed || generation != _generation) return;
                if (revision != _revision) { Message = "参数已变化，请重新生成预览。"; return; }
                Plan = plan; DetailsExpanded = true;
                Summary = $"{plan.Groups.Count} 个 ZIP · {plan.Groups.Sum(g => g.Plan.FileCount)} 个文件";
                Message = "逐页检查带入清单，再点击开始打包。";
            }
            else
            {
                if (EncryptionEnabled && TargetPassword != ConfirmPassword) throw new PackValidationException("两次目标密码不一致。");
                var passwords = PasswordPool.ParseLines(SourcePasswordText);
                var selected = Plan;
                var request = new ConversionRequest(Sources, output, ExpandInternalArchives ? ConversionMode.ExpandAndOrganize : ConversionMode.FormatOnly,
                    MaxDepth, ExpandInternalArchives ? new(SelectedFileTypes.Types, FlattenWrappingDirectories) : new(), options,
                    legacyNameEncoding: SelectedNameEncoding.Encoding);
                var progress = new Progress<RepackProgress>(p =>
                {
                    if (!IsClosed && generation == _generation && IsBusy)
                        Summary = $"第 {p.SourceIndex + 1}/{p.SourceCount} 个来源 · {Phase(p.Phase)} · 累计写入 {p.Usage.WrittenBytes:N0} 字节";
                });
                var secret = EncryptionEnabled ? new PackSecret(TargetPassword) : null;
                // 秘密由后台调用的 finally 释放，父页面同步关闭只需等待工作任务，无需等待 UI 续体。
                var task = Task.Run(async () =>
                {
                    using (secret)
                        return IsConversion ? await _service.ConvertAsync(request, passwords, secret, progress, operation.Token).ConfigureAwait(false)
                            : await _service.ExecuteAsync(selected!, secret, progress, operation.Token).ConfigureAwait(false);
                });
                _work = task; ClearSecrets();
                var result = await task;
                if (IsClosed || generation != _generation) return;
                Result = result; Plan = null; Results.Clear(); OptionsExpanded = false;
                Summary = $"{State(result.State)} · 已提交 {result.CommittedCount} 个 ZIP · 来源受限 {result.RestrictedCount} 个";
                Message = $"累计展开 {result.Usage.ExpandedBytes:N0} 字节，ZIP 暂存写入 {result.Usage.ArchiveBytes:N0} 字节。";
                foreach (var group in result.Groups)
                {
                    Results.Add($"{group.Source} → {group.OutputPath ?? group.PlannedOutput}\n{State(group.State)} · {group.FileCount} 个文件\n{group.Error?.Message}\n{string.Join("\n", group.Warnings)}");
                    if (group.OutputPath is not null && !Outputs.Contains(group.OutputPath)) Outputs.Add(group.OutputPath);
                }
                if (result.CleanupWarnings.Count > 0) Message += "\n本任务暂存残留：\n" + string.Join("\n", result.CleanupWarnings);
                var firstProblem = result.Groups.Select(g => g.Error?.Message).OfType<string>().FirstOrDefault()
                    ?? result.Groups.SelectMany(g => g.Warnings).FirstOrDefault();
                if (firstProblem is not null) Message = firstProblem + "\n" + Message;
            }
        }
        catch (OperationCanceledException) { if (!IsClosed) { Summary = "已取消"; Message = "操作已退出，已有结果保留。"; } }
        catch (Exception e) when (e is PackValidationException or UnpackValidationException or OrganizationFailureException or PackFailureException)
        { if (!IsClosed) { Plan = null; Summary = "尚未完成"; Message = e.Message; } }
        catch (Exception) { if (!IsClosed) { Plan = null; Summary = "尚未完成"; Message = "请检查来源、输出位置和访问权限。"; } }
        finally
        {
            ++_generation; _operation = null;
            if (!preview) ClearSecrets();
            if (!IsClosed) { IsCancelling = false; IsBusy = false; }
        }
    }
    [RelayCommand(CanExecute = nameof(CanCancel))] private void Cancel() { IsCancelling = true; _operation?.Cancel(); Message = "正在取消并清理本任务暂存内容。"; }
    private int RowCount => Plan?.Groups.Sum(g => g.Plan.Entries.Count + 1) ?? 0;
    private bool CanPrevious() => CanEdit && _page > 0;
    private bool CanNext() => CanEdit && (_page + 1) * PageSize < RowCount;
    [RelayCommand(CanExecute = nameof(CanPrevious))] private void PreviousPage() { _page--; RefreshRows(); }
    [RelayCommand(CanExecute = nameof(CanNext))] private void NextPage() { _page++; RefreshRows(); }
    private void RefreshRows()
    {
        Rows.Clear();
        if (Plan is not null)
            foreach (var row in Plan.Groups.SelectMany(g => new[] { $"{g.Source} → {g.Plan.Request.OutputPath}{(g.Warnings.Count == 0 ? "" : "\n" + string.Join("\n", g.Warnings))}" }
                .Concat(g.Plan.Entries.Select(e => $"{e.SourcePath} → {Path.GetFileName(g.Plan.Request.OutputPath)} / {e.EntryName}{(e.IsDirectory ? "/" : "")}")))
                .Skip(_page * PageSize).Take(PageSize)) Rows.Add(row);
        PageSummary = $"第 {_page + 1}/{Math.Max(1, (RowCount + PageSize - 1) / PageSize)} 页 · {RowCount} 项映射";
        NotifyCommands();
    }
    private void ClearSecrets() { SourcePasswordText = ""; TargetPassword = ""; ConfirmPassword = ""; }
    private static string Phase(RepackPhase phase) => phase switch
    { RepackPhase.Reading => "读取来源", RepackPhase.Planning => "生成清单", RepackPhase.Writing => "创建 ZIP", RepackPhase.Verifying => "回读核对", RepackPhase.Committed => "已提交", _ => "清理暂存" };
    private static string State(RepackState state) => state switch
    { RepackState.Completed => "完成", RepackState.CompletedWithWarnings => "完成（有提示）", RepackState.PartiallyCompleted => "部分完成", RepackState.Cancelled => "已取消", RepackState.Skipped => "没有匹配内容", RepackState.NotRun => "未执行", _ => "失败" };
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
    public ValueTask DisposeAsync() => new(_disposeTask ??= CloseAsync());
    private async Task CloseAsync()
    {
        _closed = true; ++_generation; _closing.Cancel();
        try { await _work.ConfigureAwait(false); } catch { /* 工作错误由命令观察；关闭只负责排空清理。 */ }
        ClearSecrets(); _closing.Dispose();
    }
}

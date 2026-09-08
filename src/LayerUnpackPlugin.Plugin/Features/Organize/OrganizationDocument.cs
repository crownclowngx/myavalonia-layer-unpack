using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LayerUnpackPlugin.Headless.Application;
using LayerUnpackPlugin.Headless.Contracts;

namespace LayerUnpackPlugin.Features.Organize;

/// <summary>解压 Document 拥有的临时整理子任务。只冻结输入快照并编排预览、执行和分页，业务规则由 Headless 决定。
/// 页面返回不销毁结果；新批次、清空或父任务关闭时取消并排空，结果不持久化，也不继承密码。</summary>
public sealed partial class OrganizationDocument : ObservableObject, IDisposable, IAsyncDisposable
{
    private readonly IOrganizationService _service;
    private readonly UnpackResult _input;
    private readonly CancellationTokenSource _closing;
    private CancellationTokenSource? _operation;
    private Task _backgroundWork = Task.CompletedTask;
    private Task? _disposeTask;
    private long _generation;
    private bool _closed;
    private int _page;
    public const int PageSize = 100;

    [ObservableProperty] private string _outputParent;
    [ObservableProperty] private OrganizationTypeOption _selectedFileTypes = FileTypes[0];
    [ObservableProperty] private bool _flattenWrappingDirectories;
    [ObservableProperty] private bool _rulesExpanded;
    [ObservableProperty] private bool _detailsExpanded;
    [ObservableProperty] private bool _conflictsOnly;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isCancelling;
    [ObservableProperty] private OrganizationPlan? _plan;
    [ObservableProperty] private OrganizationResult? _result;
    [ObservableProperty] private string _message = "选择规则后生成预览；原解压结果保留，整理通过复制生成新目录。";
    [ObservableProperty] private string _summary = "尚未生成预览";
    [ObservableProperty] private string _pageSummary = "";
    public ObservableCollection<OrganizationPreviewRow> Rows { get; } = [];
    public static IReadOnlyList<OrganizationTypeOption> FileTypes { get; } = Array.AsReadOnly(new[]
    {
        new OrganizationTypeOption(OrganizationFileTypes.All, "全部文件（包含嵌套归档）"),
        new OrganizationTypeOption(OrganizationFileTypes.Pdf, "仅 PDF（.pdf）"),
        new OrganizationTypeOption(OrganizationFileTypes.Images, "仅图片（常见图片扩展名）"),
        new OrganizationTypeOption(OrganizationFileTypes.PdfAndImages, "PDF 和图片")
    });
    public bool IsClosed => _closed || _closing.IsCancellationRequested;
    public bool CanEdit => !IsClosed && !IsBusy && !ShowRepackTask;
    public bool HasPlan => Plan is not null;
    public bool CanOpenResult => Result?.State == OrganizationState.Completed;
    public string? OutputPath => Result?.OutputDirectory ?? Plan?.OutputDirectory;
    public string SourceSummary => $"当前解压批次：{_input.Succeeded} 个已提交归档节点；按顶层来源归类。";
    public string MappingSummary => Plan is null ? "" : $"{Plan.FileCount} 个文件 · {Plan.TotalBytes:N0} 字节 · {Plan.Sources.Count} 个来源 · {Plan.Conflicts.Count} 个命名调整";
    public string FlattenSummary => Plan is null ? "" : string.Join(Environment.NewLine, Plan.Sources.Where(s => s.RemovedPrefix.Length > 0)
        .Take(10).Select(s => $"{s.TargetName}：去掉 {s.RemovedPrefix}/")) + (Plan.Sources.Count(s => s.RemovedPrefix.Length > 0) > 10 ? "\n其余路径变化见映射详情。" : "");

    public OrganizationDocument(IOrganizationService service, UnpackResult input, string outputParent, CancellationToken parentClosing, IRepackService? repackService = null)
    {
        _service = service; _input = input; _outputParent = outputParent;
        _repackService = repackService ?? new RepackService();
        _closing = CancellationTokenSource.CreateLinkedTokenSource(parentClosing);
    }

    partial void OnOutputParentChanged(string value) => InvalidatePlan();
    partial void OnSelectedFileTypesChanged(OrganizationTypeOption value) => InvalidatePlan();
    partial void OnFlattenWrappingDirectoriesChanged(bool value) => InvalidatePlan();
    partial void OnConflictsOnlyChanged(bool value) { _page = 0; RefreshRows(); }
    partial void OnIsBusyChanged(bool value) { OnPropertyChanged(nameof(CanEdit)); NotifyCommands(); }
    partial void OnIsCancellingChanged(bool value) => NotifyCommands();
    partial void OnPlanChanged(OrganizationPlan? value)
    {
        _page = 0; RefreshRows();
        OnPropertyChanged(nameof(HasPlan)); OnPropertyChanged(nameof(MappingSummary)); OnPropertyChanged(nameof(FlattenSummary)); OnPropertyChanged(nameof(OutputPath)); NotifyCommands();
    }
    partial void OnResultChanged(OrganizationResult? value) { OnPropertyChanged(nameof(CanOpenResult)); OnPropertyChanged(nameof(OutputPath)); NotifyCommands(); }
    private void InvalidatePlan()
    {
        // 运行时绑定被冻结；程序化改值也不会改变已捕获计划，结束后不会把旧预览误用于新规则。
        Plan = null;
        if (!IsBusy) { Summary = "规则或输出位置已改变"; Message = "请重新生成预览后开始整理。"; }
        NotifyCommands();
    }
    private bool CanPreview() => CanEdit && !string.IsNullOrWhiteSpace(OutputParent);
    private bool CanExecute() => CanEdit && Plan?.FileCount > 0 && Result?.State != OrganizationState.Completed;
    private bool CanCancel() => IsBusy && !IsClosed && !IsCancelling;
    private void NotifyCommands()
    {
        PreviewCommand.NotifyCanExecuteChanged(); ExecuteCommand.NotifyCanExecuteChanged(); CancelCommand.NotifyCanExecuteChanged();
        PreviousPageCommand.NotifyCanExecuteChanged(); NextPageCommand.NotifyCanExecuteChanged();
        PackResultCommand.NotifyCanExecuteChanged(); ReturnFromRepackCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanPreview))]
    private Task PreviewAsync() => RunAsync(false);
    [RelayCommand(CanExecute = nameof(CanExecute))]
    private Task ExecuteAsync() => RunAsync(true);

    private async Task RunAsync(bool execute)
    {
        if (!CanEdit || (execute && !CanExecute())) return;
        var selectedPlan = Plan;
        var rules = new OrganizationRules(SelectedFileTypes.Types, FlattenWrappingDirectories);
        var output = OutputParent;
        var generation = ++_generation;
        IsCancelling = false; IsBusy = true;
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(_closing.Token);
        _operation = operation;
        try
        {
            await ResetRepackAsync();
            Result = null;
            if (!execute)
            {
                Plan = null; Summary = "正在核对提交清单并生成预览";
                var work = Task.Run(() => _service.PlanAsync(_input, output, rules, operation.Token));
                _backgroundWork = work;
                var plan = await work;
                if (IsClosed || generation != _generation) return;
                if (OutputParent != output || SelectedFileTypes.Types != rules.FileTypes || FlattenWrappingDirectories != rules.FlattenWrappingDirectories)
                { Message = "参数已改变，请重新生成预览。"; return; }
                Plan = plan; Summary = MappingSummary;
                Message = plan.FileCount == 0 ? "没有匹配文件，不会创建整理目录。" : "预览已就绪。可展开检查全部映射和命名调整，再开始整理。";
            }
            else
            {
                Summary = "正在验证来源";
                var progress = new Progress<OrganizationProgress>(p =>
                {
                    if (!IsClosed && generation == _generation && IsBusy)
                        Summary = $"{p.Phase} · {p.FilesDone}/{p.TotalFiles} 个文件 · {p.CopiedBytes:N0}/{p.TotalBytes:N0} 字节";
                });
                var work = Task.Run(() => _service.ExecuteAsync(selectedPlan!, progress, operation.Token));
                _backgroundWork = work;
                var result = await work;
                if (IsClosed || generation != _generation) return;
                Result = result;
                Summary = result.State switch { OrganizationState.Completed => "整理完成", OrganizationState.Cancelled => "整理已取消", OrganizationState.NoMatches => "没有匹配文件", _ => "整理未完成" };
                Message = result.Error?.Message ?? (result.State == OrganizationState.Completed ? "独立整理目录已生成，原始包和原解压结果保留。" : "原始包和原解压结果保留，可重新生成预览。");
                if (result.CleanupWarning is not null) Message += "\n本次暂存内容未能清理，请检查：" + result.CleanupWarning;
                if (result.State != OrganizationState.Completed) Plan = null;
            }
        }
        catch (OperationCanceledException) { if (!IsClosed) { Summary = "预览已取消"; Message = "未创建整理目录。"; } }
        catch (OrganizationFailureException e) { if (!IsClosed) { Plan = null; Summary = "预览未完成"; Message = e.Message; } }
        catch (Exception) { if (!IsClosed) { Plan = null; Summary = "整理未完成"; Message = "请检查来源、输出位置和访问权限后重新预览。"; } }
        finally
        {
            ++_generation; _operation = null;
            if (!IsClosed) { IsCancelling = false; IsBusy = false; }
        }
    }

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() { IsCancelling = true; _operation?.Cancel(); Message = "正在取消，等待复制与清理退出。"; }
    private int RowCount => ConflictsOnly ? Plan?.Conflicts.Count ?? 0 : Plan?.Mappings.Count ?? 0;
    private bool CanPrevious() => CanEdit && _page > 0;
    private bool CanNext() => CanEdit && (_page + 1) * PageSize < RowCount;
    [RelayCommand(CanExecute = nameof(CanPrevious))]
    private void PreviousPage() { _page--; RefreshRows(); }
    [RelayCommand(CanExecute = nameof(CanNext))]
    private void NextPage() { _page++; RefreshRows(); }
    private void RefreshRows()
    {
        Rows.Clear();
        if (Plan is not null)
        {
            var rows = ConflictsOnly
                ? Plan.Conflicts.Skip(_page * PageSize).Take(PageSize).Select(c => new OrganizationPreviewRow(c.OriginalPath, c.SuggestedPath, c.Reason))
                : Plan.Mappings.Skip(_page * PageSize).Take(PageSize).Select(m => new OrganizationPreviewRow(m.SourcePath, m.TargetRelativePath, m.IsDirectory ? "目录" : $"文件 · {m.Length:N0} 字节"));
            foreach (var row in rows) Rows.Add(row);
        }
        PageSummary = RowCount == 0 ? "没有详情" : $"第 {_page + 1}/{(RowCount + PageSize - 1) / PageSize} 页 · 共 {RowCount:N0} 项";
        NotifyCommands();
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
    public ValueTask DisposeAsync() => new(_disposeTask ??= CloseAsync());
    private async Task CloseAsync()
    {
        _closed = true; ++_generation; _closing.Cancel();
        if (RepackTask is not null) await RepackTask.DisposeAsync().ConfigureAwait(false);
        // 只等待无 UI 依赖的后台工作，Host 同步释放 Scope 时不会等待界面续体；清理完成后才归还所有权。
        try { await _backgroundWork.ConfigureAwait(false); } catch { /* 命令负责观察错误，关闭只负责排空。 */ }
        _closing.Dispose();
    }
}

public sealed record OrganizationTypeOption(OrganizationFileTypes Types, string Label) { public override string ToString() => Label; }
public sealed record OrganizationPreviewRow(string Source, string Target, string Details);

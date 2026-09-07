using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LayerUnpackPlugin.Headless.Contracts;

namespace LayerUnpackPlugin.Features.Unpack;

/// <summary>
/// Document 的呈现部分：将当前输入与 Headless 快照投影为页面状态。
/// 分文件仅用于把交互意图与执行编排放在各自容易检查的位置，不引入第二套业务状态机。
/// </summary>
public sealed partial class UnpackDocument
{
    private bool _outputChosen;
    private bool _updatingSuggestion;
    private int _recursiveDepth = 2;
    private string? _batchOutputDirectory;

    [ObservableProperty] private bool _inputsExpanded;
    [ObservableProperty] private bool _passwordsExpanded;
    [ObservableProperty] private bool _optionsExpanded;
    [ObservableProperty] private bool _resultsExpanded;
    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private bool _isCancelling;
    [ObservableProperty] private string _currentItemText = "";
    [ObservableProperty] private string? _resultOutputPath;

    /// <summary>问题列表与完整结果树共用稳定节点。折叠树时，失败和校验／清理提示仍然可见。</summary>
    public ObservableCollection<ArchiveNodeViewModel> Issues { get; } = [];
    public bool HasIssues => Issues.Count > 0;
    public bool HasInputs => Inputs.Count > 0;
    public bool HasResults => Roots.Count > 0;
    public bool IsEmpty => !HasInputs && !HasResults;
    public bool HasSelectedNode => SelectedNode is not null;
    public bool CanOpenResult => ResultOutputPath is not null;
    public string InputSummary => $"已添加 {Inputs.Count} 个压缩包";
    public string StartLabel => HasResults ? "开始新批次" : "开始解压";
    public string OutputHint => _outputChosen ? "使用本任务指定的位置；每个包独立输出，同名自动编号。"
        : string.IsNullOrEmpty(OutputDirectory) ? "请选择输出位置；多个来源不会自动推测共同目录。"
        : "建议输出到输入旁的“解压结果”；可以修改，每个包独立输出。";

    public string StatusTitle => IsCancelling ? "正在取消" : IsScanning ? "正在扫描输入" : IsBusy ? "正在解压"
        : CurrentResult?.State switch
        {
            BatchState.Completed => "处理完成",
            BatchState.PartialFailure => "部分项目未完成",
            BatchState.Cancelled => "已取消",
            BatchState.Failed => "处理未完成",
            _ => HasInputs ? "准备解压" : "添加文件即可开始"
        };

    /// <summary>
    /// 开关只改变既有 MaxDepth，不另存“额外层数”。关闭为总层数 1；再次开启恢复本任务有效选择。
    /// Headless 仍是 1–16 校验与组合归档计层的唯一依据，程序传入非法值时不会在呈现层偷偷纠正。
    /// </summary>
    public bool IsRecursive
    {
        get => MaxDepth > 1;
        set
        {
            if (value == IsRecursive) return;
            MaxDepth = value ? _recursiveDepth : 1;
        }
    }

    /// <summary>
    /// 折叠的数值控件仍会建立双向绑定。给它一个始终有效的递归选项值，
    /// 避免控件的 Minimum=2 将默认 MaxDepth=1 反向修正为 2，导致用户未勾选就递归。
    /// </summary>
    public int RecursiveDepth
    {
        get => _recursiveDepth;
        set
        {
            if (value is < 2 or > UnpackLimits.DepthCeiling || value == _recursiveDepth) return;
            _recursiveDepth = value;
            OnPropertyChanged();
            if (IsRecursive) MaxDepth = value;
        }
    }

    partial void OnMaxDepthChanged(int value)
    {
        if (value is >= 2 and <= UnpackLimits.DepthCeiling) _recursiveDepth = value;
        OnPropertyChanged(nameof(IsRecursive));
        OnPropertyChanged(nameof(RecursiveDepth));
    }

    partial void OnIsScanningChanged(bool value) => OnPropertyChanged(nameof(StatusTitle));
    partial void OnIsCancellingChanged(bool value) { OnPropertyChanged(nameof(StatusTitle)); NotifyCommands(); }
    partial void OnResultOutputPathChanged(string? value) => OnPropertyChanged(nameof(CanOpenResult));

    /// <summary>选择器即使确认了与建议相同的路径，也代表用户明确选择，之后不能被自动建议覆盖。</summary>
    public void ChooseOutputDirectory(string path)
    {
        if (!CanEdit) return;
        _outputChosen = true;
        OutputDirectory = path;
        OnPropertyChanged(nameof(OutputHint));
    }

    private void UpdateOutputSuggestion()
    {
        if (_outputChosen) return;
        _updatingSuggestion = true;
        try { OutputDirectory = OutputDirectorySuggestion.ForInputs(Inputs.Select(i => i.Path)) ?? ""; }
        finally { _updatingSuggestion = false; }
        OnPropertyChanged(nameof(OutputHint));
    }

    private void NotifyPresentation()
    {
        OnPropertyChanged(nameof(HasInputs)); OnPropertyChanged(nameof(InputSummary));
        OnPropertyChanged(nameof(HasResults)); OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(HasIssues)); OnPropertyChanged(nameof(HasSelectedNode));
        OnPropertyChanged(nameof(StartLabel)); OnPropertyChanged(nameof(StatusTitle));
    }

    /// <summary>只在显式操作时展开设置；进度和密码失败不会抢焦点或自动打断其他独立包。</summary>
    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void ShowPasswordEntry()
    {
        PasswordsExpanded = true;
        Message = "一行一个候选密码。补充后可重试所选失败项，密码仅用于当前批次。";
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void PrepareNewBatch()
    {
        OptionsExpanded = true;
        Message = "调整编码、输入或输出后，点击“开始新批次”。重试仍沿用原批次的输出、层数、编码和预算。";
    }

    private void UpdateResultPresentation(UnpackResult result)
    {
        var problemIds = result.Nodes.Where(n => n.Error is not null || n.Warning is not null || n.CleanupWarnings.Count > 0)
            .Select(n => n.Id).ToHashSet();
        foreach (var issue in Issues.Where(i => !problemIds.Contains(i.Id)).ToArray()) Issues.Remove(issue);
        foreach (var node in result.Nodes.Where(n => problemIds.Contains(n.Id)))
        {
            var item = _nodeIndex[node.Id];
            if (!Issues.Contains(item)) Issues.Add(item);
        }

        var current = result.Nodes.FirstOrDefault(n => n.State is NodeState.Extracting or NodeState.Probing);
        CurrentItemText = current is null ? "" : $"当前：{Path.GetFileName(current.SourcePath)} · 第 {current.Depth} 层";
        var outputs = result.Nodes.Where(n => n.ParentId is null && n.OutputDirectory is not null).ToArray();
        // 输出框可为下一批修改；打开结果必须锚定真正执行过的批次，不能跳到尚未生成内容的新位置。
        ResultOutputPath = outputs.Length switch { 0 => null, 1 => outputs[0].OutputDirectory, _ => _batchOutputDirectory };
        NotifyPresentation();
    }
}

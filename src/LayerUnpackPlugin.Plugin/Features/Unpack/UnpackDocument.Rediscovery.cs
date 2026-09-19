using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LayerUnpackPlugin.Headless.Contracts;
using MyAvaloniaManagement.PluginSdk;

namespace LayerUnpackPlugin.Features.Unpack;

public sealed partial class UnpackDocument
{
    private OwnedUnpackTask? _rediscoveredTask;
    private int _batchMaxDepth = 1;
    private LegacyNameEncoding _batchEncoding = LegacyNameEncoding.Gb18030;
    [ObservableProperty] private bool _showRediscoveredTask;
    public UnpackDocument? RediscoveredTask => _rediscoveredTask?.Document;
    private bool CanRediscoverGroup() => CanEdit && SelectedNode?.CanRediscover == true;
    private bool CanReturnFromRediscovery() => !IsClosed && RediscoveredTask?.IsBusy != true;

    partial void OnShowRediscoveredTaskChanged(bool value)
    { OnPropertyChanged(nameof(ShowMainTask)); OnPropertyChanged(nameof(CanEdit)); NotifyCommands(); }

    /// <summary>
    /// 补卷改变了输入身份，不能借旧会话的重试接口接纳新成员。沿用项目已有的临时子页所有权，
    /// 准备一个独立任务并保留父结果；仅传来源、位置、剩余深度和编码，绝不复制密码或自动写入。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRediscoverGroup))]
    private async Task RediscoverGroupAsync()
    {
        if (!CanRediscoverGroup()) return;
        var node = CurrentResult!.Nodes.Single(n => n.Id == SelectedNode!.Id);
        IsBusy = true;
        try
        {
            await ResetRediscoveryAsync();
            if (IsClosed) return;
            _rediscoveredTask = new(_service, _closing.Token);
            var document = _rediscoveredTask.Document;
            document.PropertyChanged += RediscoveredChanged;
            OnPropertyChanged(nameof(RediscoveredTask));
            await document.InitializeAsync(new NewDocumentActivation("重新识别分卷"), _closing.Token);
            document.MaxDepth = Math.Max(1, _batchMaxDepth - node.Depth + 1);
            document.SelectedNameEncoding = NameEncodings.Single(e => e.Encoding == _batchEncoding);
            document.ChooseOutputDirectory(node.ParentId is null ? _batchOutputDirectory! : Path.GetDirectoryName(node.SourcePath)!);
            var work = document.AddPathsAsync([node.SourcePath]); _backgroundWork = work; await work;
            if (!IsClosed)
            {
                document.Message = "已重新识别此组；确认输入和剩余层数后开始。原任务结果已保留。";
                ShowRediscoveredTask = true;
            }
        }
        catch (Exception) { if (!IsClosed) Message = "重新识别未完成，请检查分卷所在目录后重试。"; }
        finally { if (!IsClosed) IsBusy = false; }
    }

    private async Task ResetRediscoveryAsync()
    {
        if (_rediscoveredTask is null) return;
        _rediscoveredTask.Document.PropertyChanged -= RediscoveredChanged;
        await _rediscoveredTask.DisposeAsync(); _rediscoveredTask = null;
        OnPropertyChanged(nameof(RediscoveredTask)); ShowRediscoveredTask = false;
    }

    private void RediscoveredChanged(object? sender, PropertyChangedEventArgs e)
    { if (e.PropertyName == nameof(IsBusy)) ReturnFromRediscoveryCommand.NotifyCanExecuteChanged(); }
    [RelayCommand(CanExecute = nameof(CanReturnFromRediscovery))]
    private void ReturnFromRediscovery() { if (CanReturnFromRediscovery()) ShowRediscoveredTask = false; }
}

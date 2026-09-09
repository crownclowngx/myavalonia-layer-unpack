using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LayerUnpackPlugin.Features.Check;
using LayerUnpackPlugin.Headless.Application;

namespace LayerUnpackPlugin.Features.Unpack;

/// <summary>检查入口复用当前来源，不继承解压密码或输出路径；父页拥有并排空子任务。</summary>
public sealed partial class UnpackDocument
{
    private readonly IArchiveCheckService _checkService;
    [ObservableProperty] private ArchiveCheckDocument? _checkTask;
    [ObservableProperty] private bool _showCheckTask;
    private bool CanCheck() => CanEdit && Inputs.Count > 0;
    private bool CanReturnFromCheck() => !IsClosed && CheckTask?.IsBusy != true;
    partial void OnShowCheckTaskChanged(bool value) { OnPropertyChanged(nameof(CanEdit)); OnPropertyChanged(nameof(ShowMainTask)); NotifyCommands(); }
    [RelayCommand(CanExecute = nameof(CanCheck))]
    private async Task CheckArchiveAsync()
    {
        if (!CanCheck()) return;
        await ResetCheckAsync();
        if (IsClosed) return;
        CheckTask = new(_checkService, Inputs.Select(i => i.Path), _closing.Token);
        CheckTask.PropertyChanged += CheckChanged; ShowCheckTask = true;
    }
    private void CheckChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    { if (e.PropertyName == nameof(ArchiveCheckDocument.IsBusy)) ReturnFromCheckCommand.NotifyCanExecuteChanged(); }
    [RelayCommand(CanExecute = nameof(CanReturnFromCheck))]
    private void ReturnFromCheck() { if (CanReturnFromCheck()) ShowCheckTask = false; }
    private async Task ResetCheckAsync()
    {
        if (CheckTask is null) return;
        CheckTask.PropertyChanged -= CheckChanged; await CheckTask.DisposeAsync(); CheckTask = null; ShowCheckTask = false;
    }
}

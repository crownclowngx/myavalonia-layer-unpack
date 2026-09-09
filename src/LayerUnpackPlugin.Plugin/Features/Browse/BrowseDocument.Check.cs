using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LayerUnpackPlugin.Features.Check;
using LayerUnpackPlugin.Headless.Application;

namespace LayerUnpackPlugin.Features.Browse;

/// <summary>目录可读和正文完整是两个动作；浏览快照不会自动产生检查成功标签。</summary>
public sealed partial class BrowseDocument
{
    private readonly IArchiveCheckService _checkService;
    [ObservableProperty] private ArchiveCheckDocument? _checkTask;
    [ObservableProperty] private bool _showCheckTask;
    public bool ShowMainTask => !ShowUnpackTask && !ShowCheckTask;
    partial void OnShowUnpackTaskChanged(bool value) => OnPropertyChanged(nameof(ShowMainTask));
    partial void OnShowCheckTaskChanged(bool value) { OnPropertyChanged(nameof(ShowMainTask)); OnPropertyChanged(nameof(CanEdit)); NotifyCommands(); }
    private bool CanReturnFromCheck() => !IsClosed && CheckTask?.IsBusy != true;
    [RelayCommand(CanExecute = nameof(CanLoad))]
    private async Task CheckArchiveAsync()
    {
        if (!CanLoad()) return;
        await ResetCheckAsync();
        if (IsClosed) return;
        CheckTask = new(_checkService, new[] { SourcePath }, _closing.Token);
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

using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LayerUnpackPlugin.Features.Repack;
using LayerUnpackPlugin.Headless.Application;

namespace LayerUnpackPlugin.Features.Organize;

/// <summary>只传递成功整理结果的提交清单，绝不把 OutputDirectory 当成任意目录扫描入口。</summary>
public sealed partial class OrganizationDocument
{
    private readonly IRepackService _repackService;
    [ObservableProperty] private RepackDocument? _repackTask;
    [ObservableProperty] private bool _showRepackTask;
    private bool CanPack() => CanEdit && CanOpenResult;
    private bool CanReturn() => !IsClosed && RepackTask?.IsBusy != true;
    partial void OnShowRepackTaskChanged(bool value) { OnPropertyChanged(nameof(CanEdit)); NotifyCommands(); }
    [RelayCommand(CanExecute = nameof(CanPack))]
    private void PackResult()
    {
        if (!CanPack()) return;
        if (RepackTask is null)
        {
            RepackTask = new(_repackService, Result!, OutputParent, _closing.Token);
            RepackTask.PropertyChanged += RepackChanged;
        }
        ShowRepackTask = true;
    }
    private void RepackChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(RepackDocument.IsBusy)) return;
        ReturnFromRepackCommand.NotifyCanExecuteChanged(); OnPropertyChanged(nameof(CanEdit));
    }
    [RelayCommand(CanExecute = nameof(CanReturn))]
    private void ReturnFromRepack() { if (CanReturn()) ShowRepackTask = false; }
    private async Task ResetRepackAsync()
    {
        if (RepackTask is null) return;
        RepackTask.PropertyChanged -= RepackChanged;
        await RepackTask.DisposeAsync(); RepackTask = null; ShowRepackTask = false;
    }
}

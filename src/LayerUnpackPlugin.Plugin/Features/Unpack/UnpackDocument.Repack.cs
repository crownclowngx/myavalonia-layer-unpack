using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LayerUnpackPlugin.Features.Repack;
using LayerUnpackPlugin.Headless.Application;

namespace LayerUnpackPlugin.Features.Unpack;

/// <summary>SDK 没有携带任意结果快照的新建页面端口，沿用浏览／整理的上下文子页惯例。
/// 父页负责所有权和导航；子页有独立执行状态，进入不继承来源密码、不自动开始写入。</summary>
public sealed partial class UnpackDocument
{
    private readonly IRepackService _repackService;
    [ObservableProperty] private RepackDocument? _repackTask;
    [ObservableProperty] private bool _showRepackTask;
    public bool ShowMainTask => !ShowOrganizationTask && !ShowRepackTask && !ShowCheckTask && !ShowRediscoveredTask;
    private bool CanConvert() => CanEdit && Inputs.Count > 0;
    private bool CanPackResults() => CanEdit && CurrentResult?.Succeeded > 0;
    private bool CanReturnFromRepack() => !IsClosed && RepackTask?.IsBusy != true;
    partial void OnShowRepackTaskChanged(bool value) { OnPropertyChanged(nameof(CanEdit)); OnPropertyChanged(nameof(ShowMainTask)); NotifyCommands(); }
    [RelayCommand(CanExecute = nameof(CanConvert))]
    private async Task ConvertToZipAsync()
    {
        if (!CanConvert()) return;
        await ResetRepackAsync();
        if (IsClosed) return;
        RepackTask = new(_repackService, Inputs.Select(i => i.Path), OutputDirectory, _closing.Token);
        EnterRepack();
    }
    [RelayCommand(CanExecute = nameof(CanPackResults))]
    private async Task PackResultsAsync()
    {
        if (!CanPackResults()) return;
        var input = CurrentResult!;
        await ResetRepackAsync();
        if (IsClosed) return;
        RepackTask = new(_repackService, input, _batchOutputDirectory ?? OutputDirectory, _closing.Token);
        EnterRepack();
    }
    private void EnterRepack() { RepackTask!.PropertyChanged += RepackChanged; ShowRepackTask = true; }
    private void RepackChanged(object? sender, PropertyChangedEventArgs e)
    { if (e.PropertyName == nameof(RepackDocument.IsBusy)) ReturnFromRepackCommand.NotifyCanExecuteChanged(); }
    [RelayCommand(CanExecute = nameof(CanReturnFromRepack))]
    private void ReturnFromRepack() { if (CanReturnFromRepack()) ShowRepackTask = false; }
    private async Task ResetRepackAsync()
    {
        if (RepackTask is null) return;
        RepackTask.PropertyChanged -= RepackChanged;
        await RepackTask.DisposeAsync(); RepackTask = null; ShowRepackTask = false;
    }
}

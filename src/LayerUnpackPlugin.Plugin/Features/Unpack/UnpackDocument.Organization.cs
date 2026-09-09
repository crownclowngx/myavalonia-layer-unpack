using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LayerUnpackPlugin.Features.Organize;
using LayerUnpackPlugin.Headless.Application;

namespace LayerUnpackPlugin.Features.Unpack;

/// <summary>整理作为解压结果的上下文子状态，不新增常驻面板。当前解压页拥有唯一整理子任务，
/// 返回时保留预览和成功目录，开始下一批或清空时明确释放，父 Document 关闭统一排空。</summary>
public sealed partial class UnpackDocument
{
    private readonly IOrganizationService _organizationService;
    [ObservableProperty] private OrganizationDocument? _organizationTask;
    [ObservableProperty] private bool _showOrganizationTask;
    private bool CanOrganize() => CanEdit && CurrentResult?.Succeeded > 0;
    private bool CanReturnToUnpack() => !IsClosed && OrganizationTask?.IsBusy != true && OrganizationTask?.RepackTask?.IsBusy != true;
    partial void OnShowOrganizationTaskChanged(bool value) { OnPropertyChanged(nameof(CanEdit)); OnPropertyChanged(nameof(ShowMainTask)); NotifyCommands(); }

    [RelayCommand(CanExecute = nameof(CanOrganize))]
    private void Organize()
    {
        if (!CanOrganize()) return;
        if (OrganizationTask is null)
        {
            OrganizationTask = new(_organizationService, CurrentResult!, _batchOutputDirectory ?? OutputDirectory, _closing.Token, _repackService);
            OrganizationTask.PropertyChanged += OrganizationChanged;
        }
        ShowOrganizationTask = true;
    }

    [RelayCommand(CanExecute = nameof(CanReturnToUnpack))]
    private void ReturnToUnpack() { if (CanReturnToUnpack()) ShowOrganizationTask = false; }
    private void OrganizationChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(OrganizationDocument.IsBusy) or nameof(OrganizationDocument.CanEdit)) ReturnToUnpackCommand.NotifyCanExecuteChanged();
    }
    private async Task ResetOrganizationAsync()
    {
        await ResetCheckAsync();
        await ResetRepackAsync();
        if (OrganizationTask is null) return;
        OrganizationTask.PropertyChanged -= OrganizationChanged;
        await OrganizationTask.DisposeAsync();
        OrganizationTask = null; ShowOrganizationTask = false;
    }
}

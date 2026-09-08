using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace LayerUnpackPlugin.Features.Organize;

/// <summary>仅适配原生目录选择和打开动作；选择器返回后再次核对页面身份及可编辑性。</summary>
public sealed partial class OrganizationView : UserControl
{
    public OrganizationView() => InitializeComponent();
    private async void PickOutput(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not OrganizationDocument { CanEdit: true } document) return;
        try
        {
            var provider = TopLevel.GetTopLevel(this)?.StorageProvider;
            if (provider is null) { document.Message = "当前窗口没有选择器，请输入完整目录路径。"; return; }
            var folders = await provider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "选择整理目录的位置", AllowMultiple = false });
            if (ReferenceEquals(DataContext, document) && document.CanEdit && folders.FirstOrDefault()?.TryGetLocalPath() is string path) document.OutputParent = path;
        }
        catch (Exception) { if (ReferenceEquals(DataContext, document) && document.CanEdit) document.Message = "目录选择未完成，请重试或手动输入。"; }
    }
    private void OpenResult(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not OrganizationDocument { IsClosed: false, CanOpenResult: true } document) return;
        var path = document.Result?.OutputDirectory;
        if (path is null || !Directory.Exists(path)) { document.Message = "整理目录已移动或不存在，请检查结果路径。"; return; }
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception) { document.Message = "无法打开整理目录，请复制结果路径查看。"; }
    }
}

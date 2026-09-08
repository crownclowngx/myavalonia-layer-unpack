using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using System.Diagnostics;

namespace LayerUnpackPlugin.Features.Repack;

/// <summary>仅适配窗口目录选择器，业务执行和清理完全由 Document 的 Headless 用例承担。</summary>
public sealed partial class RepackView : UserControl
{
    public RepackView() => InitializeComponent();
    private void OpenOutput(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not RepackDocument { IsClosed: false, HasOutputs: true } document) return;
        // 锚定真实提交的最后一个产物，不能使用用户为下次任务修改的输出输入框。
        var path = document.Outputs.Last();
        if (!File.Exists(path)) { document.Message = "最近提交的 ZIP 已移动或不存在，请检查记录的产物路径。"; return; }
        try { Process.Start(new ProcessStartInfo(Path.GetDirectoryName(path)!) { UseShellExecute = true }); }
        catch (Exception) { document.Message = "无法打开目录，请按已提交的产物路径查看。"; }
    }
    private async void PickOutput(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not RepackDocument { CanEdit: true } document) return;
        try
        {
            var provider = TopLevel.GetTopLevel(this)?.StorageProvider;
            if (provider is null) { document.Message = "当前窗口没有选择器，请输入完整目录路径。"; return; }
            var folders = await provider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "选择新 ZIP 的位置", AllowMultiple = false });
            if (ReferenceEquals(DataContext, document) && document.CanEdit && folders.FirstOrDefault()?.TryGetLocalPath() is string path)
                document.OutputDirectory = path;
        }
        catch (Exception) { if (ReferenceEquals(DataContext, document) && document.CanEdit) document.Message = "目录选择未完成，请重试或手动输入。"; }
    }
}

using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace LayerUnpackPlugin.Features.Unpack;

/// <summary>仅适配窗口文件选择、拖放和打开目录。Document 不保存 Window，Headless 不弹出文件选择器。</summary>
public sealed partial class UnpackView : UserControl
{
    public UnpackView()
    {
        InitializeComponent();
        AddHandler(DragDrop.DragOverEvent, DragOver);
        AddHandler(DragDrop.DropEvent, Drop);
    }
    private UnpackDocument? Document => DataContext as UnpackDocument;

    private async void PickFiles(object? sender, RoutedEventArgs e) => await WithPickerAsync(async provider =>
    {
        var files = await provider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "选择压缩包", AllowMultiple = true });
        if (Document is { CanEdit: true } document) await document.AddPathsAsync(files.Select(f => f.TryGetLocalPath()).OfType<string>());
    });
    private async void PickFolder(object? sender, RoutedEventArgs e) => await WithPickerAsync(async provider =>
    {
        var folders = await provider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "扫描文件夹中的压缩包", AllowMultiple = true });
        if (Document is { CanEdit: true } document) await document.AddPathsAsync(folders.Select(f => f.TryGetLocalPath()).OfType<string>());
    });
    private async void PickOutput(object? sender, RoutedEventArgs e) => await WithPickerAsync(async provider =>
    {
        var folders = await provider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "选择解压输出目录", AllowMultiple = false });
        if (Document is { CanEdit: true } document && folders.FirstOrDefault()?.TryGetLocalPath() is string path) document.OutputDirectory = path;
    });
    private async Task WithPickerAsync(Func<IStorageProvider, Task> action)
    {
        if (Document?.CanEdit != true) return;
        try
        {
            var provider = TopLevel.GetTopLevel(this)?.StorageProvider;
            if (provider is null) { Document.Message = "当前窗口没有文件选择能力。"; return; }
            await action(provider);
        }
        catch (Exception) { if (Document is { IsClosed: false } document) document.Message = "文件选择未完成，请重试或手动输入输出路径。"; }
    }
    private void DragOver(object? sender, DragEventArgs e)
    { e.DragEffects = Document?.CanEdit == true ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; }
    private async void Drop(object? sender, DragEventArgs e)
    {
        e.Handled = true;
        try
        {
            if (Document is { CanEdit: true } document)
                await document.AddPathsAsync(e.DataTransfer.TryGetFiles()?.Select(f => f.TryGetLocalPath()).OfType<string>() ?? []);
        }
        catch (Exception) { if (Document is { IsClosed: false } document) document.Message = "无法读取拖入文件。"; }
    }
    private void OpenOutput(object? sender, RoutedEventArgs e)
    {
        var path = Document?.SelectedNode?.OutputPath;
        if (path is null || !Directory.Exists(path)) { if (Document is not null) Document.Message = "当前项还没有可用的输出目录。"; return; }
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception) { if (Document is not null) Document.Message = "无法打开输出目录，可以复制详情中的路径。"; }
    }
}

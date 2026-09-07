using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace LayerUnpackPlugin.Features.Pack;

/// <summary>窗口适配只处理选择器、拖放与打开结果；始终验证异步返回仍属于同一可编辑任务。</summary>
public sealed partial class PackView : UserControl
{
    public PackView()
    {
        InitializeComponent();
        AddHandler(DragDrop.DragOverEvent, OnDragOver); AddHandler(DragDrop.DropEvent, OnDrop);
    }
    private PackDocument? Document => DataContext as PackDocument;
    private bool CanApply(PackDocument document) => ReferenceEquals(Document, document) && document.CanEdit;
    private async Task WithPicker(Func<IStorageProvider, PackDocument, Task> action)
    {
        if (Document is not { CanEdit: true } document) return;
        try
        {
            if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } provider) { document.Message = "当前窗口没有文件选择能力。"; return; }
            await action(provider, document);
        }
        catch (Exception) { if (CanApply(document)) document.Message = "选择未完成，请重试或手动输入输出位置。"; }
    }
    private async void PickFiles(object? sender, RoutedEventArgs e) => await WithPicker(async (provider, document) =>
    {
        var files = await provider.OpenFilePickerAsync(new() { Title = "选择要压缩的文件", AllowMultiple = true });
        if (CanApply(document) && files.Count > 0) await document.AddPathsAsync(files.Select(f => f.TryGetLocalPath()).OfType<string>());
    });
    private async void PickFolders(object? sender, RoutedEventArgs e) => await WithPicker(async (provider, document) =>
    {
        var folders = await provider.OpenFolderPickerAsync(new() { Title = "选择要压缩的文件夹", AllowMultiple = true });
        if (CanApply(document) && folders.Count > 0) await document.AddPathsAsync(folders.Select(f => f.TryGetLocalPath()).OfType<string>());
    });
    private async void PickOutput(object? sender, RoutedEventArgs e) => await WithPicker(async (provider, document) =>
    {
        var folders = await provider.OpenFolderPickerAsync(new() { Title = "选择 ZIP 输出文件夹" });
        if (CanApply(document) && folders.FirstOrDefault()?.TryGetLocalPath() is string path) document.ChooseOutputDirectory(path);
    });
    private void OnDragOver(object? sender, DragEventArgs e)
    { e.DragEffects = Document?.CanEdit == true ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; }
    private async void OnDrop(object? sender, DragEventArgs e)
    {
        e.Handled = true;
        if (Document is not { CanEdit: true } document) return;
        try { await document.AddPathsAsync(e.DataTransfer.TryGetFiles()?.Select(f => f.TryGetLocalPath()).OfType<string>() ?? []); }
        catch (Exception) { if (CanApply(document)) document.Message = "无法读取拖入的文件。"; }
    }
    private void OpenOutput(object? sender, RoutedEventArgs e)
    {
        if (Document is not { } document) return;
        var path = document.ResultDirectory;
        if (path is null || !Directory.Exists(path)) { document.Message = "输出文件夹已不存在，请检查结果路径。"; return; }
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception) { document.Message = "无法打开文件夹，可以复制结果中的路径。"; }
    }
}

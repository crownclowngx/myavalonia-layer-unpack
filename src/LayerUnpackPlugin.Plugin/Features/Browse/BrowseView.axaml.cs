using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace LayerUnpackPlugin.Features.Browse;

/// <summary>原生适配只取得本地路径和打开结果，不承担业务扫描。选择器返回时检查原任务仍然有效。</summary>
public sealed partial class BrowseView : UserControl
{
    public BrowseView()
    {
        InitializeComponent(); AddHandler(DragDrop.DragOverEvent, OnDragOver); AddHandler(DragDrop.DropEvent, OnDrop);
    }
    private BrowseDocument? Document => DataContext as BrowseDocument;
    private bool CanApply(BrowseDocument document) => ReferenceEquals(Document, document) && document.CanEdit && !document.ShowUnpackTask;
    private async void PickArchive(object? sender, RoutedEventArgs e)
    {
        if (Document is not { CanEdit: true } document) return;
        var source = document.SourcePath;
        try
        {
            if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } provider) { document.Message = "当前窗口无法打开选择器，可以输入本地路径。"; return; }
            var files = await provider.OpenFilePickerAsync(new() { Title = "选择要浏览的归档", AllowMultiple = false });
            if (CanApply(document) && document.SourcePath == source && files.FirstOrDefault()?.TryGetLocalPath() is { } path) await document.OpenPathAsync(path);
        }
        catch (Exception) { if (CanApply(document)) document.Message = "归档选择未完成，请重试。"; }
    }
    private async void PickOutput(object? sender, RoutedEventArgs e)
    {
        if (Document is not { CanEdit: true } document) return;
        var source = document.SourcePath; var output = document.OutputDirectory;
        try
        {
            if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } provider) { document.Message = "当前窗口无法打开选择器，可以输入输出路径。"; return; }
            var folders = await provider.OpenFolderPickerAsync(new() { Title = "选择提取结果的父目录" });
            if (CanApply(document) && source == document.SourcePath && output == document.OutputDirectory && folders.FirstOrDefault()?.TryGetLocalPath() is { } path)
                document.ChooseOutputDirectory(path);
        }
        catch (Exception) { if (CanApply(document)) document.Message = "输出位置选择未完成，请重试。"; }
    }
    private void OnDragOver(object? sender, DragEventArgs e)
    {
        if (Document?.ShowUnpackTask == true) return;
        e.DragEffects = Document?.CanEdit == true ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true;
    }
    private async void OnDrop(object? sender, DragEventArgs e)
    {
        if (Document is not { CanEdit: true, ShowUnpackTask: false } document) return;
        e.Handled = true;
        try
        {
            var paths = e.DataTransfer.TryGetFiles()?.Select(f => f.TryGetLocalPath()).OfType<string>().Take(2).ToArray() ?? [];
            if (paths.Length != 1) { document.Message = "浏览任务一次打开一个归档，请只拖入一个文件。"; return; }
            await document.OpenPathAsync(paths[0]);
        }
        catch (Exception) { if (CanApply(document)) document.Message = "无法读取拖入的归档。"; }
    }
    private void OpenOutput(object? sender, RoutedEventArgs e)
    {
        if (Document is not { } document) return;
        if (document.Outputs.FirstOrDefault() is not { } path || !Directory.Exists(path)) { document.Message = "结果目录已不存在，请查看已提交路径。"; return; }
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception) { document.Message = "无法打开目录，可从详情复制结果路径。"; }
    }
}

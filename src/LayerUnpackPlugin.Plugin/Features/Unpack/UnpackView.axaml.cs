using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

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

    private async void PickFiles(object? sender, RoutedEventArgs e) => await WithPickerAsync(async (provider, document) =>
    {
        var files = await provider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "选择压缩包", AllowMultiple = true });
        if (files.Count > 0 && CanApplyPicker(document)) await document.AddPathsAsync(files.Select(f => f.TryGetLocalPath()).OfType<string>());
    });
    private async void PickFolder(object? sender, RoutedEventArgs e) => await WithPickerAsync(async (provider, document) =>
    {
        var folders = await provider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "扫描文件夹中的压缩包", AllowMultiple = true });
        if (folders.Count > 0 && CanApplyPicker(document)) await document.AddPathsAsync(folders.Select(f => f.TryGetLocalPath()).OfType<string>());
    });
    private async void PickOutput(object? sender, RoutedEventArgs e) => await WithPickerAsync(async (provider, document) =>
    {
        var folders = await provider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "选择解压输出目录", AllowMultiple = false });
        if (CanApplyPicker(document) && folders.FirstOrDefault()?.TryGetLocalPath() is string path) document.ChooseOutputDirectory(path);
    });
    // 选择器返回时页面可能已经关闭或重新绑定。结果只能回到发起选择的同一可编辑 Document。
    private bool CanApplyPicker(UnpackDocument document) => ReferenceEquals(Document, document) && document.CanEdit;
    private async Task WithPickerAsync(Func<IStorageProvider, UnpackDocument, Task> action)
    {
        if (Document is not { CanEdit: true } document) return;
        try
        {
            var provider = TopLevel.GetTopLevel(this)?.StorageProvider;
            if (provider is null) { document.Message = "当前窗口没有文件选择能力。"; return; }
            await action(provider, document);
        }
        catch (Exception) { if (CanApplyPicker(document)) document.Message = "文件选择未完成，请重试或手动输入输出路径。"; }
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
    private void InspectNode(object? sender, RoutedEventArgs e)
    {
        if (Document is not { IsClosed: false } document || sender is not Button { Tag: ArchiveNodeViewModel node }) return;
        document.SelectedNode = node;
        Dispatcher.UIThread.Post(() => { if (ReferenceEquals(Document, document) && !document.IsClosed) NodeDetails.BringIntoView(); });
    }
    private void RevealPasswords(object? sender, RoutedEventArgs e)
    {
        if (Document is not { CanEdit: true } document) return;
        document.ShowPasswordEntryCommand.Execute(null);
        // 展开后再安排焦点，避免控件仍处于折叠布局时 Focus 失败；迟到动作不作用于新的任务。
        Dispatcher.UIThread.Post(() =>
        {
            if (!CanApplyPicker(document)) return;
            PasswordEditor.BringIntoView(); PasswordEditor.Focus();
        }, DispatcherPriority.Loaded);
    }
    private void RevealOptions(object? sender, RoutedEventArgs e)
    {
        if (Document is not { CanEdit: true } document) return;
        document.PrepareNewBatchCommand.Execute(null);
        Dispatcher.UIThread.Post(() => { if (CanApplyPicker(document)) OptionsExpander.BringIntoView(); }, DispatcherPriority.Loaded);
    }
    private void OpenOutput(object? sender, RoutedEventArgs e) => OpenDirectory(Document?.SelectedNode?.OutputPath);
    private void OpenBatchOutput(object? sender, RoutedEventArgs e) => OpenDirectory(Document?.ResultOutputPath);
    private void OpenDirectory(string? path)
    {
        if (path is null || !Directory.Exists(path)) { if (Document is not null) Document.Message = "当前项还没有可用的输出目录。"; return; }
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception) { if (Document is not null) Document.Message = "无法打开输出目录，可以复制详情中的路径。"; }
    }
}

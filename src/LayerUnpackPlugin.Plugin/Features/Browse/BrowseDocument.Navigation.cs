using CommunityToolkit.Mvvm.Input;
using MyAvaloniaManagement.PluginSdk;

namespace LayerUnpackPlugin.Features.Browse;

public sealed partial class BrowseDocument
{
    [RelayCommand(CanExecute = nameof(CanLoad))]
    private async Task PrepareUnpackAsync()
    {
        if (!CanLoad()) return;
        if (_unpackTask?.Document.IsBusy == true)
        {
            // 已存在的解压任务保留自己的输入、层数和结果，避免轻量入口覆盖仍在运行的用户任务。
            ShowUnpackTask = true; Message = "已返回仍在运行的解压任务，输入与层数以该页为准。"; return;
        }
        var source = SourcePath; var output = OutputDirectory;
        PasswordText = ""; IsBusy = true; IsCancelling = false;
        try
        {
            if (_unpackTask is not null)
            {
                var close = _unpackTask.DisposeAsync().AsTask(); _background = close; await close;
            }
            if (IsClosed) return;
            _unpackTask = new(_unpackService, _closing.Token); OnPropertyChanged(nameof(UnpackTask));
            await _unpackTask.Document.InitializeAsync(new NewDocumentActivation("全部解压"), _closing.Token);
            _unpackTask.Document.MaxDepth = 1;
            if (!string.IsNullOrWhiteSpace(output)) _unpackTask.Document.ChooseOutputDirectory(output);
            // AddPathsAsync 自带后台发现与取消；该操作只准备输入，不自动开始写入。
            var work = _unpackTask.Document.AddPathsAsync([source]); _background = work; await work;
            if (!IsClosed) ShowUnpackTask = true;
        }
        catch (Exception) { if (!IsClosed) Message = "解压任务准备未完成，请返回后重试。"; }
        finally { FinishOperation(); }
    }
    [RelayCommand]
    private void ReturnToBrowse() => ShowUnpackTask = false;
}

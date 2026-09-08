using CommunityToolkit.Mvvm.Input;
using LayerUnpackPlugin.Headless.Application;
using LayerUnpackPlugin.Headless.Contracts;

namespace LayerUnpackPlugin.Features.Browse;

public sealed partial class BrowseDocument
{
    [RelayCommand(CanExecute = nameof(CanLoad))]
    private async Task LoadAsync()
    {
        if (!CanLoad()) return;
        ResetCatalog(); PasswordText = ""; CurrentResult = null; IsBusy = true; IsCancelling = false;
        var source = SourcePath; var encoding = SelectedNameEncoding.Encoding;
        var generation = ++_generation;
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(_closing.Token); _operation = operation;
        try
        {
            Summary = "正在读取目录"; Message = "读取来源摘要与 ZIP 元数据，不展开文件内容。";
            // 后台返回之前登记会话所有权。即使 UI 续体尚未运行，关闭流程仍能找到并释放已创建的会话。
            async Task OpenOwnedSession()
            {
                _session = await _service.OpenAsync(new(source, encoding), ProgressFor(generation), operation.Token).ConfigureAwait(false);
            }
            var work = OpenOwnedSession(); _background = work; await work;
            if (IsClosed) return;
            if (SourcePath != source || SelectedNameEncoding.Encoding != encoding)
            { ResetCatalog(); Message = "输入或编码已变化，请重新加载。"; return; }
            _selection = new ArchiveSelection(_session!.Catalog); _offset = 0; RefreshPage();
            if (!_outputChosen)
            {
                _suggesting = true;
                try { OutputDirectory = Path.Combine(Path.GetDirectoryName(_session.Catalog.SourcePath)!, "提取结果"); }
                finally { _suggesting = false; }
            }
            Summary = $"已加载 {_session.Catalog.Entries.Count(e => !e.IsSynthetic)} 个 ZIP 条目";
            PasswordExpanded = _session.Catalog.Entries.Any(e => e.IsEncrypted);
            Message = "勾选目录将包含其全部后代；搜索只改变显示。提取所选默认一层，内部归档保留为文件。";
        }
        catch (OperationCanceledException) { if (!IsClosed) { Summary = "已取消"; Message = "目录读取已取消，未保留半份清单。"; } }
        catch (UnpackFailureException e) { if (!IsClosed) { Summary = "未加载"; Message = e.Message; } }
        catch (Exception) { if (!IsClosed) { Summary = "未加载"; Message = "无法加载，请检查归档路径后重试。"; } }
        finally { FinishOperation(); }
    }

    [RelayCommand(CanExecute = nameof(CanExtract))]
    private async Task ExtractAsync()
    {
        if (!CanExtract()) return;
        var session = _session!; var selection = _selection!.Capture();
        var output = OutputDirectory; var password = PasswordText.Length == 0 ? null : PasswordText;
        PasswordText = ""; CurrentResult = null; IsBusy = true; IsCancelling = false;
        var generation = ++_generation;
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(_closing.Token); _operation = operation;
        try
        {
            Summary = "正在提取所选";
            var work = session.ExtractAsync(selection, output, password, ProgressFor(generation), operation.Token);
            _background = work; var result = await work;
            if (IsClosed) return;
            CurrentResult = result;
            Summary = result.State switch { BrowseExtractState.Completed => $"已提取 {result.Files.Count} 个文件", BrowseExtractState.Cancelled => "已取消", _ => "提取未完成" };
            Message = result.Error?.Message ?? (result.State == BrowseExtractState.Completed ? "已完成一层提取，源包保留。" : "未提交内容已清理，之前的成功产物保留。");
            Message += $" 本会话累计读取 {result.ReadBytes:N0} 字节，展开 {result.ExpandedBytes:N0} 字节。";
            if (result.CleanupWarning is not null) Message += " 未能清理临时目录：" + result.CleanupWarning;
            if (result.OutputDirectory is { } path) { Outputs.Insert(0, path); OnPropertyChanged(nameof(HasOutputs)); }
            if (result.Error?.Code == UnpackError.PasswordRequiredOrInvalid) PasswordExpanded = true;
            if (session.IsInvalidated) { _selection.Clear(); RefreshSelection(); }
        }
        catch (Exception) { if (!IsClosed) { Summary = "提取未完成"; Message = "请检查选择、密码及输出位置后重试。"; } }
        finally { password = null; FinishOperation(); }
    }

    private IProgress<BrowseProgress> ProgressFor(long generation) => new Progress<BrowseProgress>(p =>
    {
        if (IsClosed || generation != _generation || !IsBusy) return;
        Summary = p.Operation switch
        {
            BrowseOperation.VerifyingSource => $"正在校验来源 · 累计读取 {p.ReadBytes:N0} 字节",
            BrowseOperation.ReadingDirectory => $"正在读取目录 · {p.Entries} 个条目",
            BrowseOperation.Committing => "正在校验并提交所选内容",
            _ => $"已处理 {p.Entries} 项 · 展开 {p.ExpandedBytes:N0} 字节"
        };
    });
    private void FinishOperation()
    {
        ++_generation; _operation = null;
        if (!IsClosed) { IsCancelling = false; IsBusy = false; }
    }
}

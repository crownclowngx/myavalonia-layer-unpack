using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LayerUnpackPlugin.Headless.Application;
using LayerUnpackPlugin.Headless.Contracts;

namespace LayerUnpackPlugin.Features.Unpack;

public sealed partial class UnpackDocument
{
    private readonly IArchiveRecycler? _archiveRecycler;
    private UnpackCollectionOptions? _batchCollectionOptions;
    private UnpackCollection? _collector;

    [ObservableProperty] private bool _collectFiles;
    [ObservableProperty] private bool _collectVideos = true;
    [ObservableProperty] private bool _collectDocuments = true;
    [ObservableProperty] private bool _collectImages;
    [ObservableProperty] private string _collectExtensions = "";
    [ObservableProperty] private string _collectionParent = "";
    [ObservableProperty] private bool _cleanupLeftovers;
    [ObservableProperty] private bool _recycleSourceArchives;
    [ObservableProperty] private string? _collectionPhase;
    [ObservableProperty] private CollectionResult? _collectionResult;

    public bool CanRecycleSourceArchives => OperatingSystem.IsWindows() && _archiveRecycler is not null;
    public string CollectionDestination
    {
        get
        {
            var parent = string.IsNullOrWhiteSpace(CollectionParent) ? OutputDirectory : CollectionParent;
            return string.IsNullOrWhiteSpace(parent) ? "解压目录下的「结果」" : Path.Combine(parent, "结果");
        }
    }
    public bool HasCollectedOutput => CollectionResult?.OutputDirectory is not null;
    public bool HasCollectionResult => CollectionResult is not null;
    public bool HasPendingCollection => CollectionResult?.State == CollectionState.Paused;
    public string CollectionSummary => CollectionResult is not { } result ? "" :
        $"文件收集 · 已保存 {result.Saved} 个 · 已移动 {result.Moved} 个" +
        (result.Cleaned > 0 ? $" · 已清理 {result.Cleaned} 项" : "") +
        (result.Recycled > 0 ? $" · 已回收 {result.Recycled} 个原包或分卷" : "") +
        (result.Remaining > 0 ? $" · 待处理 {result.Remaining} 项" : "") +
        (result.PreservedSources > 0 ? $" · 保留 {result.PreservedSources} 个无匹配或未展开完的来源" : "");

    private UnpackResult ResultForOutputTools()
    {
        var extraction = CurrentResult!;
        if (CollectionResult is not { OutputDirectory: { } output } collected) return extraction;
        var node = new ArchiveNodeResult(Guid.NewGuid(), null, Path.GetFileName(output), 1, NodeState.Extracted,
            null, output, null, [], 0) { CommittedEntries = collected.Entries };
        return extraction with { Nodes = [node] };
    }

    partial void OnCollectionParentChanged(string value) => OnPropertyChanged(nameof(CollectionDestination));
    partial void OnCleanupLeftoversChanged(bool value) { if (!value) RecycleSourceArchives = false; }
    partial void OnCollectionPhaseChanged(string? value) => OnPropertyChanged(nameof(StatusTitle));
    partial void OnCollectionResultChanged(CollectionResult? value)
    {
        OnPropertyChanged(nameof(HasCollectionResult));
        OnPropertyChanged(nameof(HasCollectedOutput));
        OnPropertyChanged(nameof(HasPendingCollection));
        OnPropertyChanged(nameof(CollectionSummary));
        OnPropertyChanged(nameof(StatusTitle));
        NotifyCommands();
    }

    private UnpackCollectionOptions? ReadCollectionOptions()
    {
        if (!CollectFiles) return null;
        var types = CollectionFileTypes.None;
        if (CollectVideos) types |= CollectionFileTypes.Video;
        if (CollectDocuments) types |= CollectionFileTypes.Document;
        if (CollectImages) types |= CollectionFileTypes.Image;
        return new(string.IsNullOrWhiteSpace(CollectionParent) ? OutputDirectory : CollectionParent,
            types, CollectExtensions, CleanupLeftovers, CanRecycleSourceArchives && RecycleSourceArchives);
    }

    private void ResetCollection()
    {
        _batchCollectionOptions = null;
        _collector = null;
        CollectionResult = null;
        CollectionPhase = null;
        WorkProgressIndeterminate = true;
        WorkProgressValue = 0;
    }

    private bool CanResumeCollection() => CanEdit && HasPendingCollection && _collector is not null;

    [RelayCommand(CanExecute = nameof(CanResumeCollection))]
    private async Task ResumeCollectionAsync()
    {
        if (!CanResumeCollection()) return;
        IsBusy = true;
        IsCancelling = false;
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(_closing.Token);
        _operation = operation;
        try { await RunCollectionAsync(operation); }
        finally
        {
            ++_generation;
            _operation = null;
            if (!IsClosed) { IsCancelling = false; IsBusy = false; }
        }
    }

    private async Task RunCollectionAsync(CancellationTokenSource operation)
    {
        var collector = _collector!;
        var generation = ++_generation;
        CollectionPhase = "正在准备文件收集";
        WorkProgressText = "";
        CurrentItemText = "";
        WorkProgressIndeterminate = true;
        var progress = new Progress<CollectionProgress>(item =>
        {
            if (IsClosed || generation != _generation || CollectionPhase is null) return;
            CollectionPhase = item.Phase;
            CurrentItemText = item.CurrentPath is null ? "" : Path.GetFileName(item.CurrentPath);
            WorkProgressIndeterminate = item.Total == 0;
            WorkProgressValue = item.TotalBytes > 0 ? 100.0 * item.Bytes / item.TotalBytes :
                item.Total > 0 ? 100.0 * item.Completed / item.Total : 0;
            WorkProgressText = item.Total == 0 ? "" : $"{item.Completed}/{item.Total} 项" +
                (item.TotalBytes > 0 ? $" · {FormatBytes(item.Bytes)} / {FormatBytes(item.TotalBytes)}" : "");
        });
        var work = Task.Run(() => collector.ExecuteAsync(progress, operation.Token));
        _backgroundWork = work;
        var result = await work;
        if (IsClosed || generation != _generation) return;
        CollectionPhase = null;
        CollectionResult = result;
        WorkProgressText = "";
        CurrentItemText = "";
        if (result.OutputDirectory is not null)
        {
            ResultOutputPath = result.OutputDirectory;
            foreach (var item in _nodeIndex.Values)
                if (item.OutputPath is { } path && !Directory.Exists(path)) item.OutputPath = null;
        }
        Message = result.Message ?? "文件已收集，可以打开结果目录。";
    }
}

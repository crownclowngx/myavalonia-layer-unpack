using System.Diagnostics;
using System.Security.Cryptography;
using LayerUnpackPlugin.Headless.Contracts;
using LayerUnpackPlugin.Headless.Infrastructure;

namespace LayerUnpackPlugin.Headless.Application;

[Flags]
public enum CollectionFileTypes { None = 0, Video = 1, Document = 2, Image = 4 }

public sealed class UnpackCollectionOptions
{
    private readonly HashSet<string> _extensions = new(StringComparer.OrdinalIgnoreCase);

    public UnpackCollectionOptions(string outputParent, CollectionFileTypes types, string otherExtensions,
        bool cleanup = false, bool recycleSourceArchives = false)
    {
        OutputParent = OrganizationFiles.Absolute(outputParent);
        Cleanup = cleanup;
        RecycleSourceArchives = cleanup && recycleSourceArchives;
        if (types.HasFlag(CollectionFileTypes.Video))
            _extensions.UnionWith([".mp4", ".mkv", ".mov", ".avi", ".webm", ".m4v", ".wmv", ".flv", ".ts", ".mpeg", ".mpg", ".m2ts"]);
        if (types.HasFlag(CollectionFileTypes.Document))
            _extensions.UnionWith([".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".txt", ".md", ".rtf", ".odt", ".ods", ".odp"]);
        if (types.HasFlag(CollectionFileTypes.Image))
            _extensions.UnionWith([".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".tif", ".tiff", ".heic", ".avif"]);
        foreach (var value in otherExtensions.Split([' ', '\t', '\r', '\n', ',', '，', ';', '；'], StringSplitOptions.RemoveEmptyEntries))
        {
            var extension = value.StartsWith('.') ? value[1..] : value;
            if (extension.Length is 0 or > 32 || extension.Any(c => !char.IsLetterOrDigit(c) && c is not ('-' or '_')))
                throw new ArgumentException("自定义后缀请输入 .srt 这样的扩展名，用逗号或空格分隔。");
            _extensions.Add("." + extension);
        }
        if (_extensions.Count == 0) throw new ArgumentException("请选择要保存的文件类型或输入后缀。");
    }

    public string OutputParent { get; }
    public bool Cleanup { get; }
    public bool RecycleSourceArchives { get; }
    internal bool Matches(string path) => _extensions.Contains(Path.GetExtension(path));
}

public interface IArchiveRecycler
{
    void Recycle(string path);
}

public enum CollectionState { Completed, NoMatches, Paused }
public sealed record CollectionProgress(string Phase, int Completed, int Total, string? CurrentPath, long Bytes = 0, long TotalBytes = 0);
public sealed record CollectionResult(CollectionState State, string? OutputDirectory, int Saved, int Moved, int Cleaned,
    int Recycled, int Remaining, int PreservedSources, string? Message)
{
    public IReadOnlyList<CommittedEntry> Entries { get; internal init; } = [];
}

/// <summary>一次已结束的解压批次拥有一个收集会话。目标先独立提交，所有源移除都在提交之后。</summary>
public sealed class UnpackCollection(UnpackResult batch, UnpackCollectionOptions options, IArchiveRecycler? recycler = null, IOrganizationCopier? copier = null)
{
    private readonly IOrganizationCopier _copier = copier ?? new OrganizationCopier();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HashSet<string> _removed = new(OrganizationFiles.Comparer);
    private readonly HashSet<string> _recycled = new(OrganizationFiles.Comparer);
    private bool _planned;
    private SavedFile[] _files = [];
    private OrganizationInput[] _leftovers = [];
    private ArchiveNodeResult[] _archives = [];
    private int _preservedSources;
    private string? _output;
    private string? _stagingResidue;
    private IReadOnlyList<CommittedEntry> _entries = [];

    public async Task<CollectionResult> ExecuteAsync(IProgress<CollectionProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!await _gate.WaitAsync(0).ConfigureAwait(false))
            throw new InvalidOperationException("当前收集操作尚未结束。");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(new OrganizationLimits().Timeout);
        var token = deadline.Token;
        var progressWatch = Stopwatch.StartNew();
        string? lastPhase = null;
        string? activePath = null;
        try
        {
            token.ThrowIfCancellationRequested();
            if (!_planned)
            {
                Report("盘点解压结果", 0, 0);
                BuildPlan(token);
            }
            if (_files.Length == 0) return Snapshot(CollectionState.NoMatches, "没有匹配文件，已保留解压内容。");
            if (_output is null)
                await PublishAsync().ConfigureAwait(false);
            else
                await ValidateResultAsync(_output, token).ConfigureAwait(false);

            foreach (var file in _files)
            {
                if (_removed.Contains(file.Input.Path)) continue;
                Report("完成移动", _removed.Count, _files.Length, file.Input.Path);
                var savedPath = OrganizationFiles.EntryPath(_output!, file.Name, false);
                await using var saved = OrganizationFiles.OpenRead(savedPath);
                await VerifyContentsAsync(saved, file.Input.Entry, savedPath, token).ConfigureAwait(false);
                await RemoveAsync(file.Input, token).ConfigureAwait(false);
                _removed.Add(file.Input.Path);
            }
            var done = _removed.Count - _files.Length;
            foreach (var input in _leftovers)
            {
                if (_removed.Contains(input.Path)) continue;
                Report("清理解压产物", done, _leftovers.Length, input.Path);
                await RemoveAsync(input, token).ConfigureAwait(false);
                _removed.Add(input.Path);
                done++;
            }
            var archiveCount = _archives.Sum(node => node.SourceMembers.Count);
            if (archiveCount > _recycled.Count)
            {
                Report("核对已保存文件", 0, 0);
                await ValidateResultAsync(_output!, token).ConfigureAwait(false);
            }
            foreach (var root in _archives)
            {
                for (var index = 0; index < root.SourceMembers.Count; index++)
                {
                    token.ThrowIfCancellationRequested();
                    var member = root.SourceMembers[index];
                    if (_recycled.Contains(member)) continue;
                    Report("回收原始压缩包", _recycled.Count, archiveCount, member);
                    var input = new OrganizationInput(root.Id, member, root.SourceSignatures[index]);
                    OrganizationFiles.ValidateMetadata(input);
                    await using (var stream = OrganizationFiles.OpenRead(member))
                        await VerifyContentsAsync(stream, input.Entry, member, token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    OrganizationFiles.ValidateMetadata(input);
                    OrganizationFiles.Check(member);
                    recycler!.Recycle(member);
                    _recycled.Add(member);
                }
            }
            return Snapshot(CollectionState.Completed, null);
        }
        catch (OperationCanceledException)
        {
            return Snapshot(CollectionState.Paused, cancellationToken.IsCancellationRequested ? "已暂停收集，可以继续。" : "收集超时，可以继续。");
        }
        catch (Exception e)
        {
            var message = OrganizationFiles.Diagnostic(e).Message;
            if (activePath is not null && !message.Contains(activePath, StringComparison.Ordinal))
                message += $"{Environment.NewLine}待处理：{activePath}";
            return Snapshot(CollectionState.Paused, message);
        }
        finally { _gate.Release(); }

        async Task PublishAsync()
        {
            var total = _files.Sum(file => file.Input.Entry.Length);
            long copied = 0;
            var completed = 0;
            var transaction = new OutputTransaction(options.OutputParent);
            try
            {
                foreach (var file in _files)
                {
                    token.ThrowIfCancellationRequested();
                    var target = OrganizationFiles.EntryPath(transaction.StagingDirectory, file.Name, false);
                    OrganizationFiles.ValidateMetadata(file.Input);
                    Report("准备结果文件", completed, _files.Length, file.Input.Path, copied, total);
                    await _copier.CopyAsync(new(file.Input.SourceId, file.Input.Path, file.Name, false,
                        file.Input.Entry.Length, file.Input.Entry.Sha256), target, bytes =>
                        {
                            copied += bytes;
                            Report("准备结果文件", completed, _files.Length, file.Input.Path, copied, total);
                        }, token).ConfigureAwait(false);
                    completed++;
                }
                Report("核对并提交结果", completed, _files.Length, null, copied, total);
                var entries = await CommittedManifest.CaptureAsync(transaction.StagingDirectory, _files.Length, token).ConfigureAwait(false);
                var staged = entries.ToDictionary(entry => entry.RelativePath, StringComparer.Ordinal);
                if (entries.Count != _files.Length || _files.Any(file => !staged.TryGetValue(file.Name, out var entry) ||
                    entry.IsDirectory || entry.Length != file.Input.Entry.Length || entry.Sha256 != file.Input.Entry.Sha256))
                    throw new OrganizationFailureException(OrganizationError.OutputError, "暂存结果与解压清单不一致。");
                _output = transaction.Commit("结果", token);
                _entries = entries;
            }
            finally
            {
                if (transaction.Rollback() is { } residue) _stagingResidue = residue;
            }
        }

        void Report(string phase, int count, int total, string? path = null, long bytes = 0, long totalBytes = 0)
        {
            activePath = path;
            if (phase == lastPhase && progressWatch.ElapsedMilliseconds < 100) return;
            lastPhase = phase;
            progressWatch.Restart();
            try { progress?.Report(new(phase, count, total, path, bytes, totalBytes)); }
            catch (Exception) { /* Progress observers do not participate in file operations. */ }
        }
    }

    private void BuildPlan(CancellationToken token)
    {
        if (batch.State != BatchState.Completed)
            throw new OrganizationFailureException(OrganizationError.MissingManifest, "请先完成本批解压，再收集文件。");
        if (options.RecycleSourceArchives && recycler is null)
            throw new OrganizationFailureException(OrganizationError.OutputError, "当前环境无法回收原始压缩包。");
        var inputs = OrganizationPlanner.Collect(batch, new(), token);
        var roots = inputs.Where(input => input.Entry.RelativePath.Length == 0).ToArray();
        if (roots.Any(root => OrganizationFiles.Comparer.Equals(root.Path, options.OutputParent) ||
            PathPolicy.IsWithin(root.Path, options.OutputParent)))
            throw new OrganizationFailureException(OrganizationError.UnsafePath, "结果目录应放在各压缩包解压目录之外。");

        var byId = batch.Nodes.ToDictionary(node => node.Id);
        var incomplete = new HashSet<Guid>();
        foreach (var node in batch.Nodes.Where(node => node.State != NodeState.Extracted || node.Error is not null))
        {
            var root = node;
            while (root.ParentId is Guid parent) root = byId[parent];
            incomplete.Add(root.Id);
        }
        var unfinishedFiles = batch.Nodes.Where(node => node.State == NodeState.DepthLimit).SelectMany(node => node.SourceMembers)
            .ToHashSet(OrganizationFiles.Comparer);
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = inputs.Where(input => !input.Entry.IsDirectory && options.Matches(input.Path) && !unfinishedFiles.Contains(input.Path))
            .OrderBy(input => input.Path, StringComparer.Ordinal)
            .Select(input => new SavedFile(input, OrganizationPlanner.Allocate(Path.GetFileName(input.Path), false, usedNames))).ToArray();
        var selectedRoots = files.Select(file => file.Input.SourceId).ToHashSet();
        var eligible = roots.Select(root => root.SourceId).Where(id => selectedRoots.Contains(id) && !incomplete.Contains(id)).ToHashSet();
        var selectedPaths = files.Select(file => file.Input.Path).ToHashSet(OrganizationFiles.Comparer);
        var leftovers = options.Cleanup ? inputs.Where(input => eligible.Contains(input.SourceId) && !selectedPaths.Contains(input.Path))
            .OrderBy(input => input.Entry.IsDirectory).ThenByDescending(input => input.Path.Length).ToArray() : [];
        var archives = options.RecycleSourceArchives ? batch.Nodes.Where(node => node.ParentId is null && eligible.Contains(node.Id)).ToArray() : [];
        // 一个包的源可能是另一个包的目录中的文件；只有独立顶层路径可进入原始包回收步骤。
        if (archives.Any(node => node.SourceMembers.Any(member => roots.Any(root => PathPolicy.IsWithin(root.Path, member)))))
            throw new OrganizationFailureException(OrganizationError.UnsafePath, "输入压缩包位于本批的解压目录内，无法回收原始包。");
        if (archives.Any(node => node.SourceMembers.Count == 0 || node.SourceMembers.Count != node.SourceSignatures.Count))
            throw new OrganizationFailureException(OrganizationError.MissingManifest, "原始压缩包缺少解压时的来源凭据。");
        _files = files;
        _leftovers = leftovers;
        _archives = archives;
        _preservedSources = options.Cleanup ? roots.Length - eligible.Count : 0;
        _planned = true;
    }

    private async Task ValidateResultAsync(string location, CancellationToken token)
    {
        foreach (var file in _files)
        {
            var target = OrganizationFiles.EntryPath(location, file.Name, false);
            await using var stream = OrganizationFiles.OpenRead(target);
            await VerifyContentsAsync(stream, file.Input.Entry, target, token).ConfigureAwait(false);
        }
    }

    private static async Task RemoveAsync(OrganizationInput input, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        OrganizationFiles.ValidateMetadata(input);
        if (input.Entry.IsDirectory)
        {
            // Never recurse. New items added after extraction keep their parent directory and surface as pending.
            Directory.Delete(input.Path);
            return;
        }
        await CommittedFileRemoval.RemoveAsync(input, token).ConfigureAwait(false);
    }

    internal static async Task VerifyContentsAsync(Stream stream, CommittedEntry entry, string path, CancellationToken token)
    {
        if (stream.Length != entry.Length ||
            Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false)) != entry.Sha256)
            OrganizationFiles.Changed(path);
    }

    private CollectionResult Snapshot(CollectionState state, string? message)
    {
        var moved = _files.Count(file => _removed.Contains(file.Input.Path));
        var remaining = _files.Length + _leftovers.Length + _archives.Sum(node => node.SourceMembers.Count) -
            _removed.Count - _recycled.Count;
        if (_stagingResidue is not null && Directory.Exists(_stagingResidue))
            message = $"{message} 暂存目录待检查：{_stagingResidue}";
        return new(state, _output, _output is null ? 0 : _files.Length, moved, _removed.Count - moved,
            _recycled.Count, remaining, _preservedSources, message) { Entries = _entries };
    }

    private sealed record SavedFile(OrganizationInput Input, string Name);
}

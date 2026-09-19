using System.Diagnostics;
using LayerUnpackPlugin.Headless.Contracts;
using LayerUnpackPlugin.Headless.Domain;
using LayerUnpackPlugin.Headless.Infrastructure;

namespace LayerUnpackPlugin.Headless.Application;

/// <summary>创建隔离会话的应用入口；服务本身没有候选、节点或历史状态，可安全复用。</summary>
public interface IUnpackService
{
    UnpackSession CreateSession(UnpackRequest request);
}

public sealed class UnpackService(IArchiveExtractor extractor) : IUnpackService
{
    public UnpackService() : this(new ArchiveExtractor()) { }
    public UnpackSession CreateSession(UnpackRequest request) => new(request, extractor);
}

/// <summary>一个当前批次的应用会话。负责串行调度、深度、重试与状态汇总，不负责格式解码或 UI。</summary>
/// <remarks>Execute/Retry 互斥。密码只在实例内保留供显式重试，DisposeAsync 取消、等待并释放引用。</remarks>
public sealed class UnpackSession : IAsyncDisposable, IDisposable
{
    private readonly IArchiveExtractor _extractor;
    private readonly string _outputDirectory;
    private readonly int _maxDepth;
    private readonly bool _discoverChildren;
    private readonly LegacyNameEncoding _legacyNameEncoding;
    private readonly PasswordPool _passwords = new();
    private readonly ExecutionBudget _budget;
    private readonly List<Node> _nodes = [];
    private readonly SemaphoreSlim _executionGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _snapshotLock = new();
    private readonly Guid _batchId = Guid.NewGuid();
    private UnpackResult _snapshot;
    private bool _started;
    private volatile bool _disposed;
    private bool _terminalBudgetFailure;
    private readonly object _disposeLock = new();
    private Task? _disposeTask;

    internal UnpackSession(UnpackRequest request, IArchiveExtractor extractor, ExecutionBudget? sharedBudget = null, bool discoverChildren = true, IReadOnlyList<ArchiveSource>? frozenSources = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        _extractor = extractor;
        _discoverChildren = discoverChildren;
        request.Limits.Validate();
        if (!Enum.IsDefined(request.LegacyNameEncoding)) throw new UnpackValidationException("LegacyNameEncoding", "文件名编码选项无效。");
        _legacyNameEncoding = request.LegacyNameEncoding;
        if (request.MaxDepth is < 1 or > UnpackLimits.DepthCeiling)
            throw new UnpackValidationException("MaxDepth", "解压深度必须在 1–16 之间。");
        _maxDepth = request.MaxDepth;
        _outputDirectory = ValidateAbsolutePath(request.OutputDirectory, "OutputDirectory");
        if (File.Exists(_outputDirectory)) throw new UnpackValidationException("OutputDirectory", "输出位置必须是目录。");
        PathPolicy.EnsureNoLinks(_outputDirectory);
        var sources = frozenSources ?? request.InputSnapshot ?? ArchiveSourceResolver.ResolveInputs(request.Inputs, request.Limits);
        if (request.InputSnapshot is not null && !request.Inputs.Select(p => ValidateAbsolutePath(p, "Inputs"))
            .SequenceEqual(request.InputSnapshot.Select(s => s.PrimaryPath), ArchiveSourceResolver.Comparer))
            throw new UnpackValidationException("Inputs", "来源快照与输入清单不一致，请重新添加输入。");
        if (sources.Count == 0 || sources.Count > request.Limits.MaxArchives)
            throw new UnpackValidationException("Inputs", "至少添加一个逻辑压缩包，且数量不得超过批次节点预算。");
        _passwords.Add(request.Passwords);
        _budget = sharedBudget ?? new ExecutionBudget(request.Limits);
        foreach (var source in sources) AddNode(source, null, 1);
        _snapshot = BuildSnapshot(BatchState.Ready);
    }

    public UnpackResult Snapshot { get { lock (_snapshotLock) return _snapshot; } }

    public Task<UnpackResult> ExecuteAsync(IProgress<UnpackProgress>? progress = null, CancellationToken cancellationToken = default) =>
        RunAsync(null, [], progress, cancellationToken);

    public Task<UnpackResult> RetryAsync(IEnumerable<Guid> nodeIds, IEnumerable<string> additionalPasswords,
        IProgress<UnpackProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(nodeIds);
        ArgumentNullException.ThrowIfNull(additionalPasswords);
        return RunAsync(nodeIds.Distinct().ToArray(), additionalPasswords.ToArray(), progress, cancellationToken);
    }

    private async Task<UnpackResult> RunAsync(Guid[]? retryIds, string[] additionalPasswords,
        IProgress<UnpackProgress>? progress, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!await _executionGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("本批次已有操作正在执行。");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        var operationId = Guid.NewGuid();
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            Node[] selected;
            if (retryIds is null)
            {
                if (_started) throw new InvalidOperationException("此批次已经开始，请重试失败项或创建新批次。");
                _started = true;
                selected = _nodes.ToArray();
            }
            else
            {
                if (!_started || retryIds.Length == 0 || _terminalBudgetFailure)
                    throw new InvalidOperationException("没有可重试项，或批次预算已经耗尽。");
                selected = retryIds.Select(id => _nodes.SingleOrDefault(n => n.Id == id) ??
                    throw new InvalidOperationException("重试节点不属于当前会话。")).ToArray();
                if (selected.Any(n => !n.ToResult().CanRetry))
                    throw new InvalidOperationException("只能重试当前会话中可恢复且临时输出已经清理的失败项。");
                _passwords.Add(additionalPasswords);
                foreach (var node in selected) { node.State = NodeState.Queued; node.Error = null; }
            }

            Publish(BatchState.Running, operationId, progress);
            var cancelled = false;
            try
            {
                // 每轮只取最浅层；本层所有解码终态后才发现孩子。重试第 2、4 层时，
                // 新产生的第 3 层仍会先执行，而不会被简单 FIFO 排在既有第 4 层后面。
                var pending = selected.ToList();
                var retrySet = retryIds?.ToHashSet() ?? [];
                while (pending.Count > 0)
                {
                    var depth = pending.Min(n => n.Depth);
                    var layer = pending.Where(n => n.Depth == depth).ToArray();
                    pending.RemoveAll(n => n.Depth == depth);
                    foreach (var node in layer)
                    {
                        linked.Token.ThrowIfCancellationRequested();
                        await ExecuteNodeAsync(node, retrySet.Contains(node.Id), operationId, progress, linked.Token).ConfigureAwait(false);
                    }
                    if (!_discoverChildren) continue;
                    foreach (var node in layer.Where(n => n.State == NodeState.Extracted))
                        pending.AddRange(await DiscoverChildrenAsync(node, operationId, progress, linked.Token).ConfigureAwait(false));
                }
            }
            catch (OperationCanceledException) when (linked.IsCancellationRequested) { cancelled = true; }
            catch (UnpackFailureException e) when (e.StopBatch) { _terminalBudgetFailure = true; }
            foreach (var node in _nodes.Where(n => n.State == NodeState.Queued))
                node.State = cancelled ? NodeState.Cancelled : NodeState.NotRun;
            var state = cancelled ? BatchState.Cancelled :
                _terminalBudgetFailure || _nodes.Any(n => n.Error is not null || n.State is NodeState.Failed or NodeState.NotRun or NodeState.Cancelled)
                    ? (_nodes.Any(n => n.State == NodeState.Extracted) ? BatchState.PartialFailure : BatchState.Failed)
                    : BatchState.Completed;
            return Publish(state, operationId, progress);
        }
        finally { _executionGate.Release(); }
    }

    private async Task ExecuteNodeAsync(Node node, bool retry, Guid operationId, IProgress<UnpackProgress>? progress, CancellationToken cancellationToken)
    {
        if (node.Depth > _maxDepth) { node.State = NodeState.DepthLimit; Publish(BatchState.Running, operationId, progress); return; }
        node.State = NodeState.Probing;
        node.Error = null;
        Publish(BatchState.Running, operationId, progress);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_budget.Limits.ArchiveTimeout);
        var token = timeout.Token;
        try
        {
            await using var sourceLock = await ArchiveSourceReader.OpenAsync(node.LogicalSource, _budget.Limits, token).ConfigureAwait(false);
            var fingerprint = await sourceLock.FingerprintAsync(token).ConfigureAwait(false);
            if (retry && node.Fingerprint is not null && node.Fingerprint != fingerprint)
                throw new UnpackFailureException(UnpackError.InputChanged, "源压缩包自上次执行后发生变化，请创建新批次。");
            node.Fingerprint = fingerprint;
            var parentOutput = node.ParentId is null ? _outputDirectory : Path.GetDirectoryName(node.Source)!;
            UnpackFailureException? lastFailure = null;
            ExtractedArchive? extracted = null;
            var lastNotification = Stopwatch.StartNew();
            foreach (var password in _passwords.Attempts())
            {
                token.ThrowIfCancellationRequested();
                _budget.AddAttempt();
                var transaction = new OutputTransaction(parentOutput);
                try
                {
                    node.State = NodeState.Extracting;
                    Publish(BatchState.Running, operationId, progress);
                    extracted = await _extractor.ExtractAsync(node.LogicalSource, transaction.StagingDirectory, password, _legacyNameEncoding, _budget, bytes =>
                    {
                        node.Bytes += bytes;
                        if (lastNotification.ElapsedMilliseconds >= 100)
                        { Publish(BatchState.Running, operationId, progress); lastNotification.Restart(); }
                    }, token).ConfigureAwait(false);
                    var manifest = await CommittedManifest.CaptureAsync(transaction.StagingDirectory, _budget.Limits.MaxEntries, token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    await sourceLock.VerifyAsync(fingerprint, token).ConfigureAwait(false);
                    node.Output = transaction.Commit(ArchiveProbe.OutputName(node.Source), token);
                    node.Entries = manifest;
                    node.Format = extracted.Format;
                    node.Warning = extracted.Warning;
                    node.State = NodeState.Extracted;
                    if (extracted.UsedPassword) _passwords.MarkSuccessful(password);
                    break;
                }
                catch (UnpackFailureException e) when (!e.StopBatch && e.Code is UnpackError.PasswordRequiredOrInvalid or UnpackError.CorruptArchive)
                { lastFailure = e; }
                finally
                {
                    var residue = transaction.Rollback();
                    if (residue is not null) node.CleanupWarnings.Add(residue);
                }
                if (node.CleanupWarnings.Count != 0) break;
            }
            if (node.State != NodeState.Extracted)
                throw lastFailure ?? new UnpackFailureException(UnpackError.PasswordRequiredOrInvalid, "没有可用密码或加密内容已损坏。");
            Publish(BatchState.Running, operationId, progress);

        }
        catch (Exception e)
        {
            if (cancellationToken.IsCancellationRequested)
            { node.State = NodeState.Cancelled; throw new OperationCanceledException(cancellationToken); }
            node.State = NodeState.Failed;
            node.Error = e switch
            {
                UnpackFailureException failure => Diagnostic(failure),
                OperationCanceledException => new(UnpackError.Timeout, "本包处理超过时间预算，可在检查文件后重试。"),
                FileNotFoundException or DirectoryNotFoundException => new(UnpackError.InputUnavailable, "输入文件不存在或已被移除。"),
                UnauthorizedAccessException => new(UnpackError.OutputError, "输入或输出位置没有访问权限。"),
                IOException => new(UnpackError.OutputError, "文件读取、写入或目录提交失败。"),
                _ => new(UnpackError.UnexpectedError, "处理失败，原始引擎异常已隔离，请检查格式与输入文件。")
            };
            if (e is UnpackFailureException { StopBatch: true }) throw;
        }
        finally { Publish(BatchState.Running, operationId, progress); }
    }

    /// <summary>只读取父提交清单，不扫描历史输出目录；超时属于发现阶段，不继承已结束的解码超时。</summary>
    private async Task<IReadOnlyList<Node>> DiscoverChildrenAsync(Node parent, Guid operationId, IProgress<UnpackProgress>? progress, CancellationToken cancellationToken)
    {
        var children = new List<Node>();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_budget.Limits.ArchiveTimeout);
        var token = timeout.Token;
        try
        {
            var paths = new List<string>();
            foreach (var entry in parent.Entries!.Where(e => !e.IsDirectory).OrderBy(e => e.RelativePath, StringComparer.OrdinalIgnoreCase))
            {
                token.ThrowIfCancellationRequested();
                var path = PathPolicy.EntryPath(parent.Output!, entry.RelativePath, false);
                PathPolicy.EnsureNoLinks(path);
                if (ArchiveSourceResolver.IsSplitPath(path) || ArchiveProbe.HasKnownExtension(path) ||
                    await ArchiveProbe.DetectAsync(path, token).ConfigureAwait(false) != ArchiveKind.Unknown) paths.Add(path);
            }
            foreach (var source in ArchiveSourceResolver.ResolveCommitted(paths, _budget.Limits, token))
                children.Add(AddNode(source, parent.Id, parent.Depth + 1));
        }
        catch (Exception e)
        {
            // 父包已经完整提交，发现失败只能附加诊断，不能把它改成解码失败或制造假孩子。
            parent.Error = e is UnpackFailureException failure ? Diagnostic(failure) :
                new(e is OperationCanceledException && !cancellationToken.IsCancellationRequested ? UnpackError.Timeout : UnpackError.InputUnavailable,
                    "解压已提交，但下一层发现未完成；已成功输出仍然保留。");
            if (cancellationToken.IsCancellationRequested) throw new OperationCanceledException(cancellationToken);
            if (e is UnpackFailureException { StopBatch: true }) throw;
        }
        finally { Publish(BatchState.Running, operationId, progress); }
        return children;
    }

    private Node AddNode(ArchiveSource source, Guid? parentId, int depth)
    {
        _budget.AddArchive();
        var node = new Node(source, parentId, depth);
        _nodes.Add(node);
        return node;
    }

    private UnpackResult BuildSnapshot(BatchState state) => new(_batchId, state,
        Array.AsReadOnly(_nodes.Select(n => n.ToResult()).ToArray()), _budget.Bytes, _budget.Attempts, _terminalBudgetFailure);

    private UnpackResult Publish(BatchState state, Guid operationId, IProgress<UnpackProgress>? progress)
    {
        var snapshot = BuildSnapshot(state);
        lock (_snapshotLock) _snapshot = snapshot;
        // 观察者不是事务参与者。外部进度接收器异常不能让已经提交的文件被误报为引擎失败。
        try { if (!_disposed) progress?.Report(new UnpackProgress(operationId, snapshot)); } catch { }
        return snapshot;
    }

    private static UnpackDiagnostic Diagnostic(UnpackFailureException failure) => new(failure.Code, failure.Message);
    private static string ValidateAbsolutePath(string path, string field)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new UnpackValidationException(field, "请提供完整的本地绝对路径。");
        try { return Path.GetFullPath(path); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        { throw new UnpackValidationException(field, "路径格式无效。"); }
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    public ValueTask DisposeAsync()
    {
        lock (_disposeLock) return new ValueTask(_disposeTask ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        _disposed = true;
        _lifetime.Cancel();
        // 核心全部使用 ConfigureAwait(false)，同步 Host Scope 释放也不会等待 UI 线程上的业务续体。
        await _executionGate.WaitAsync().ConfigureAwait(false);
        try { _passwords.Dispose(); }
        finally { _executionGate.Release(); }
    }

    private sealed class Node(ArchiveSource source, Guid? parentId, int depth)
    {
        internal Guid Id { get; } = Guid.NewGuid();
        internal Guid? ParentId { get; } = parentId;
        internal ArchiveSource LogicalSource { get; } = source;
        internal string Source => LogicalSource.PrimaryPath;
        internal int Depth { get; } = depth;
        internal NodeState State { get; set; } = NodeState.Queued;
        internal string? Format { get; set; }
        internal string? Warning { get; set; }
        internal string? Output { get; set; }
        internal string? Fingerprint { get; set; }
        internal UnpackDiagnostic? Error { get; set; }
        internal List<string> CleanupWarnings { get; } = [];
        internal long Bytes { get; set; }
        internal IReadOnlyList<CommittedEntry>? Entries { get; set; }
        internal ArchiveNodeResult ToResult() => new(Id, ParentId, Source, Depth, State, Format, Output, Error,
            Array.AsReadOnly(CleanupWarnings.ToArray()), Bytes, Warning)
        { CommittedEntries = Entries, SourceMembers = LogicalSource.Members, SourceDisplayName = LogicalSource.DisplayName, IsSplitSource = LogicalSource.IsSplit };
    }
}

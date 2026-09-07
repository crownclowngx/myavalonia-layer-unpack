using System.Diagnostics;
using System.Security.Cryptography;
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

    internal UnpackSession(UnpackRequest request, IArchiveExtractor extractor)
    {
        ArgumentNullException.ThrowIfNull(request);
        _extractor = extractor;
        request.Limits.Validate();
        if (!Enum.IsDefined(request.LegacyNameEncoding)) throw new UnpackValidationException("LegacyNameEncoding", "文件名编码选项无效。");
        _legacyNameEncoding = request.LegacyNameEncoding;
        if (request.MaxDepth is < 1 or > UnpackLimits.DepthCeiling)
            throw new UnpackValidationException("MaxDepth", "解压深度必须在 1–16 之间。");
        _maxDepth = request.MaxDepth;
        _outputDirectory = ValidateAbsolutePath(request.OutputDirectory, "OutputDirectory");
        if (File.Exists(_outputDirectory)) throw new UnpackValidationException("OutputDirectory", "输出位置必须是目录。");
        PathPolicy.EnsureNoLinks(_outputDirectory);
        var paths = request.Inputs.Select(p => ValidateAbsolutePath(p, "Inputs"))
            .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).ToArray();
        if (paths.Length == 0 || paths.Length > request.Limits.MaxArchives)
            throw new UnpackValidationException("Inputs", "至少添加一个压缩包，且输入数量不得超过批次节点预算。");
        _passwords.Add(request.Passwords);
        _budget = new ExecutionBudget(request.Limits);
        foreach (var path in paths) AddNode(path, null, 1);
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
                foreach (var node in selected)
                {
                    linked.Token.ThrowIfCancellationRequested();
                    await ExecuteNodeAsync(node, retryIds is not null, operationId, progress, linked.Token).ConfigureAwait(false);
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
            PathPolicy.EnsureNoLinks(node.Source);
            // Windows 下持有只共享读的源句柄，覆盖摘要验证及所有候选尝试，避免检查后被另一个写入者替换。
            await using var sourceLock = new FileStream(node.Source, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true);
            if (sourceLock.Length > _budget.Limits.MaxInputBytes)
                throw new UnpackFailureException(UnpackError.BudgetExceeded, "输入文件超过读取预算。", true);
            var fingerprint = Convert.ToHexString(await SHA256.HashDataAsync(sourceLock, token).ConfigureAwait(false));
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
                    extracted = await _extractor.ExtractAsync(node.Source, transaction.StagingDirectory, password, _legacyNameEncoding, _budget, bytes =>
                    {
                        node.Bytes += bytes;
                        if (lastNotification.ElapsedMilliseconds >= 100)
                        { Publish(BatchState.Running, operationId, progress); lastNotification.Restart(); }
                    }, token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    node.Output = transaction.Commit(ArchiveProbe.OutputName(node.Source), token);
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

            // 父包已经提交，后续任何子包失败都不能改变父包自身成功的事实。
            var children = new List<Node>();
            foreach (var relative in extracted!.RelativeFiles.Order(StringComparer.OrdinalIgnoreCase))
            {
                token.ThrowIfCancellationRequested();
                var childPath = PathPolicy.EntryPath(node.Output!, relative, false);
                var kind = await ArchiveProbe.DetectAsync(childPath, token).ConfigureAwait(false);
                if (kind != ArchiveKind.Unknown || ArchiveProbe.HasKnownExtension(childPath))
                    children.Add(AddNode(childPath, node.Id, node.Depth + 1));
            }
            // 当前包的超时不占用后代预算：每个子包另建自己的超时令牌，批次关闭仍传递给所有后代。
            foreach (var child in children)
                await ExecuteNodeAsync(child, false, operationId, progress, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            if (node.State == NodeState.Extracted)
            {
                // 已提交的父节点保留成功事实并附带发现诊断，不制造指向自身的假子包或开放重试。
                if (e is OperationCanceledException && cancellationToken.IsCancellationRequested) throw;
                if (e is UnpackFailureException stop && stop.StopBatch) { node.Error = Diagnostic(stop); throw; }
                node.Error = e is UnpackFailureException known ? Diagnostic(known) :
                    new UnpackDiagnostic(UnpackError.InputUnavailable, "解压已提交，但后续发现未完成。");
                return;
            }
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

    private Node AddNode(string source, Guid? parentId, int depth)
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

    private sealed class Node(string source, Guid? parentId, int depth)
    {
        internal Guid Id { get; } = Guid.NewGuid();
        internal Guid? ParentId { get; } = parentId;
        internal string Source { get; } = source;
        internal int Depth { get; } = depth;
        internal NodeState State { get; set; } = NodeState.Queued;
        internal string? Format { get; set; }
        internal string? Warning { get; set; }
        internal string? Output { get; set; }
        internal string? Fingerprint { get; set; }
        internal UnpackDiagnostic? Error { get; set; }
        internal List<string> CleanupWarnings { get; } = [];
        internal long Bytes { get; set; }
        internal ArchiveNodeResult ToResult() => new(Id, ParentId, Source, Depth, State, Format, Output, Error,
            Array.AsReadOnly(CleanupWarnings.ToArray()), Bytes, Warning);
    }
}

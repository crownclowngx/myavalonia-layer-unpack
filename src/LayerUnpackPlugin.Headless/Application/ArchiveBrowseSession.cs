using LayerUnpackPlugin.Headless.Contracts;
using LayerUnpackPlugin.Headless.Infrastructure;

namespace LayerUnpackPlugin.Headless.Application;

/// <summary>每次打开归档拥有独立预算、快照和互斥执行状态。密码仅属于当前调用，不存入快照或会话历史。</summary>
internal sealed class ArchiveBrowseSession(ArchiveCatalog catalog, LegacyNameEncoding encoding, DateTime modified,
    BrowseBudget budget) : IArchiveBrowseSession
{
    private readonly object _sync = new();
    private readonly CancellationTokenSource _closing = new();
    private Task _work = Task.CompletedTask;
    private Task? _disposeTask;
    private UnpackDiagnostic? _terminalFailure;
    private string? _cleanupWarning;
    private ArchiveCatalog? _catalog = catalog;
    public ArchiveCatalog Catalog => _catalog ?? throw new ObjectDisposedException(nameof(ArchiveBrowseSession));
    public bool IsInvalidated { get; private set; }
    public long ReadBytes => budget.ReadBytes;
    public long ExpandedBytes => budget.Expansion.Bytes;

    public Task<BrowseExtractResult> ExtractAsync(BrowseSelection selection, string outputDirectory, string? password = null,
        IProgress<BrowseProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selection);
        if (string.IsNullOrWhiteSpace(outputDirectory)) throw new UnpackValidationException("Output", "请选择输出文件夹。");
        if (password?.Length > 1024) throw new UnpackValidationException("Password", "密码长度不能超过 1024 个字符。");
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposeTask is not null, this);
            if (!_work.IsCompleted) throw new InvalidOperationException("当前浏览会话已有提取操作。");
            var snapshot = Catalog;
            // 输入不可变且提前捕获；运行中界面换搜索、输出文字或选择都不能改写此调用。
            var work = Task.Run(() => ExtractCoreAsync(snapshot, selection, outputDirectory, password, progress, cancellationToken));
            _work = work; return work;
        }
    }

    private async Task<BrowseExtractResult> ExtractCoreAsync(ArchiveCatalog snapshot, BrowseSelection selection,
        string outputDirectory, string? password, IProgress<BrowseProgress>? progress, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_closing.Token, cancellationToken);
        timeout.CancelAfter(budget.Limits.Extraction.ArchiveTimeout);
        var token = timeout.Token; OutputTransaction? transaction = null;
        var state = BrowseExtractState.Failed; UnpackDiagnostic? error = null; var encrypted = false; var reading = true;
        try
        {
            token.ThrowIfCancellationRequested();
            if (IsInvalidated) BrowseSource.Changed();
            if (_terminalFailure is not null)
                return new(BrowseExtractState.Failed, null, Array.Empty<string>(), ReadBytes, ExpandedBytes, _terminalFailure, _cleanupWarning);
            budget.Expansion.AddAttempt();
            var entries = ZipBrowseCatalog.ResolveSelection(snapshot, selection);
            await BrowseSource.VerifyAsync(snapshot.SourcePath, snapshot.Sha256, modified, budget, progress, token).ConfigureAwait(false);
            await using var file = BrowseSource.Open(snapshot.SourcePath, budget.Limits);
            using var input = new BrowseReadStream(file, budget, token);
            // 再验证实际用于解码的句柄，封住前一次按路径校验结束与本次打开之间的替换窗口。
            if (await BrowseSource.HashAsync(input, budget, progress, token).ConfigureAwait(false) != snapshot.Sha256) BrowseSource.Changed();
            ZipDirectoryGuard.Validate(input, budget.Limits, token);
            using var zip = BrowseSource.OpenZip(input, encoding); zip.Password = password;
            reading = false;
            transaction = new OutputTransaction(outputDirectory);
            var files = new List<string>(); var completed = 0; long reportedBytes = budget.Expansion.Bytes;
            foreach (var row in entries)
            {
                token.ThrowIfCancellationRequested(); budget.Expansion.AddEntry();
                var entry = zip[row.Id.Ordinal]; encrypted = entry.IsCrypted;
                ZipBrowseCatalog.ValidateEntry(entry);
                var target = PathPolicy.EntryPath(transaction.StagingDirectory, entry.Name, entry.IsDirectory);
                if (entry.IsDirectory) Directory.CreateDirectory(target);
                else
                {
                    await ZipArchiveExtractor.ExtractEntryAsync(zip, entry, target, password, budget.Expansion, _ =>
                    {
                        if (budget.Expansion.Bytes - reportedBytes < 4 * 1024 * 1024) return;
                        reportedBytes = budget.Expansion.Bytes;
                        progress?.Report(new(BrowseOperation.Extracting, completed, ReadBytes, ExpandedBytes, row.Path));
                    }, token).ConfigureAwait(false);
                    files.Add(row.Path);
                }
                completed++;
                if (completed % 64 == 0 || completed == entries.Count)
                    progress?.Report(new(BrowseOperation.Extracting, completed, ReadBytes, ExpandedBytes, row.Path));
            }
            reading = true;
            await BrowseSource.VerifyAsync(snapshot.SourcePath, snapshot.Sha256, modified, budget, progress, token).ConfigureAwait(false);
            reading = false;
            progress?.Report(new(BrowseOperation.Committing, completed, ReadBytes, ExpandedBytes));
            var output = transaction.Commit(ArchiveProbe.OutputName(snapshot.SourcePath) + "-所选", token);
            return new(BrowseExtractState.Completed, output, files.AsReadOnly(), ReadBytes, ExpandedBytes, null, null);
        }
        catch (Exception) when (token.IsCancellationRequested)
        {
            state = cancellationToken.IsCancellationRequested || _closing.IsCancellationRequested ? BrowseExtractState.Cancelled : BrowseExtractState.Failed;
            if (state == BrowseExtractState.Failed) error = new(UnpackError.Timeout, "选择提取超过时间上限，未提交输出。");
        }
        catch (UnpackFailureException e)
        {
            if (e.Code == UnpackError.InputChanged) IsInvalidated = true;
            error = new(e.Code, e.Message);
        }
        catch (ICSharpCode.SharpZipLib.SharpZipBaseException)
        {
            error = encrypted ? new(UnpackError.PasswordRequiredOrInvalid, "请补充本次密码；密码不正确或加密内容认证失败。") :
                new(UnpackError.CorruptArchive, "所选 ZIP 内容无法通过校验，未提交输出。");
        }
        catch (UnpackValidationException e) { error = new(UnpackError.OutputError, e.Message); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { error = new(reading ? UnpackError.InputUnavailable : UnpackError.OutputError, reading ? "源归档不可用，请检查来源后重新加载。" : "无法写入提取结果，请检查输出位置、权限及可用空间。"); }
        catch (Exception) { error = new(UnpackError.UnexpectedError, "提取未完成，请检查归档和输出位置。"); }
        _cleanupWarning = transaction?.Rollback();
        if (error?.Code == UnpackError.BudgetExceeded || _cleanupWarning is not null)
            _terminalFailure = error ?? new(UnpackError.OutputError, "上次取消的临时目录未能清理，请检查残留并重新加载归档。");
        return new(state, null, Array.Empty<string>(), ReadBytes, ExpandedBytes, error, _cleanupWarning);
    }

    public ValueTask DisposeAsync()
    {
        lock (_sync) return new(_disposeTask ??= CloseAsync());
    }
    private async Task CloseAsync()
    {
        _closing.Cancel();
        // 后台从不等待 UI 线程；同步 Scope 释放也可以安全排空，不把“发出取消”误当作清理完成。
        await _work.ConfigureAwait(false);
        _catalog = null; _closing.Dispose();
    }
}

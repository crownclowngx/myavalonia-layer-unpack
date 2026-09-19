using System.Diagnostics;
using LayerUnpackPlugin.Headless.Contracts;
using LayerUnpackPlugin.Headless.Domain;
using LayerUnpackPlugin.Headless.Infrastructure;

namespace LayerUnpackPlugin.Headless.Application;

public interface IArchiveCheckService
{
    Task<ArchiveCheckResult> CheckAsync(ArchiveCheckRequest request, IProgress<ArchiveCheckProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

/// <summary>检查是独立用例：目录检查复用浏览端口，正文检查复用已经审计的单包读取端口。
/// 正文暂存在私有事务目录且永不提交；每次密码尝试都清理，预算和总超时跨尝试共享。
/// 源只读句柄贯穿检查防止 Windows 上被替换，结束时再次核对元数据；不是历史快照或修复服务。</summary>
public sealed class ArchiveCheckService(IArchiveExtractor extractor, IArchiveBrowseService browser) : IArchiveCheckService
{
    public ArchiveCheckService() : this(new ArchiveExtractor(), new ArchiveBrowseService()) { }

    public async Task<ArchiveCheckResult> CheckAsync(ArchiveCheckRequest request, IProgress<ArchiveCheckProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Limits.Validate();
        if (!Enum.IsDefined(request.Scope) || !Enum.IsDefined(request.NameEncoding) ||
            string.IsNullOrWhiteSpace(request.SourcePath) || !Path.IsPathFullyQualified(request.SourcePath) ||
            (request.TemporaryDirectory is not null && !Path.IsPathFullyQualified(request.TemporaryDirectory)))
            throw new UnpackValidationException("Check", "请选择完整源路径、有效检查范围和编码；临时父目录必须为绝对路径。");
        using var passwords = new PasswordPool(); passwords.Add(request.Passwords);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(request.Limits.ArchiveTimeout);
        var watch = Stopwatch.StartNew();
        var budget = new ExecutionBudget(request.Limits);
        var state = ArchiveCheckState.Failed;
        ExtractedArchive? decoded = null; string? format = null; string? cleanup = null;
        UnpackDiagnostic? error = null; var files = 0;
        try
        {
            await Task.Run(async () =>
            {
                var token = timeout.Token; token.ThrowIfCancellationRequested();
                var logicalSource = ArchiveSourceResolver.ResolveInputs([request.SourcePath], request.Limits, token).Single();
                await using var source = await ArchiveSourceReader.OpenAsync(logicalSource, request.Limits, token).ConfigureAwait(false);
                var fingerprint = await source.FingerprintAsync(token).ConfigureAwait(false);
                if (request.Scope == ArchiveCheckScope.DirectoryOnly)
                {
                    var limits = new BrowseLimits { Extraction = request.Limits, MaxRows = request.Limits.MaxEntries };
                    await using var session = await browser.OpenAsync(new(request.SourcePath, request.NameEncoding, limits),
                        cancellationToken: token).ConfigureAwait(false);
                    files = session.Catalog.Entries.Count(e => !e.IsDirectory && !e.IsSynthetic); format = session.Catalog.Format;
                    state = ArchiveCheckState.DirectoryRead; return;
                }
                budget.AddArchive();
                foreach (var password in passwords.Attempts())
                {
                    token.ThrowIfCancellationRequested(); budget.AddAttempt();
                    OutputTransaction? temporary = null;
                    var retry = false;
                    try
                    {
                        temporary = new OutputTransaction(request.TemporaryDirectory ?? Path.GetTempPath());
                        decoded = await extractor.ExtractAsync(logicalSource, temporary.StagingDirectory, password,
                            request.NameEncoding, budget, _ => progress?.Report(new(budget.Entries, budget.Bytes, budget.Attempts)), token).ConfigureAwait(false);
                    }
                    catch (UnpackFailureException e) when (e.Code == UnpackError.PasswordRequiredOrInvalid)
                    { error = new(e.Code, e.Message); retry = true; }
                    finally { cleanup = temporary?.Rollback(); }
                    if (cleanup is not null)
                    { decoded = null; throw new UnpackFailureException(UnpackError.OutputError, "检查临时内容未能清理，已停止后续尝试。"); }
                    if (retry) continue;
                    token.ThrowIfCancellationRequested();
                    await source.VerifyAsync(fingerprint, token).ConfigureAwait(false);
                    format = decoded!.Format; files = decoded.RelativeFiles.Count; error = null;
                    state = decoded.Warning is not null || decoded.CheckLimitations.Count > 0 || (files > 0 && decoded.Evidence.Count == 0)
                        ? ArchiveCheckState.CompletedWithLimitations : ArchiveCheckState.Completed;
                    break;
                }
            }, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception) when (timeout.IsCancellationRequested)
        {
            state = cancellationToken.IsCancellationRequested ? ArchiveCheckState.Cancelled : ArchiveCheckState.Failed;
            error = cancellationToken.IsCancellationRequested ? null : new(UnpackError.Timeout, "检查超过总时间上限。");
        }
        catch (UnpackFailureException e) { error = new(e.Code, e.Message); state = ArchiveCheckState.Failed; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { error = new(UnpackError.InputUnavailable, "来源或临时空间不可访问，请检查路径、权限和可用空间。"); }
        catch (Exception) { error = new(UnpackError.UnexpectedError, "检查未完成，请核对归档和支持范围。"); }
        var complete = state is ArchiveCheckState.Completed or ArchiveCheckState.CompletedWithLimitations;
        var limitations = new List<string>();
        if (state == ArchiveCheckState.DirectoryRead) limitations.Add("仅目录读取成功，未解码正文，不能据此认定内容完整。");
        if (complete)
        {
            limitations.AddRange(decoded!.CheckLimitations);
            if (decoded.Warning is not null) limitations.Add(decoded.Warning);
            if (files > 0 && decoded.Evidence.Count == 0) limitations.Add("读取器未提供具体校验证据，不能认定校验和或认证已通过。");
        }
        limitations.Add("范围仅为当前逻辑归档；内嵌压缩包作为普通文件读取，未检查其内部内容。");
        return new(state, request.Scope, format, complete || state == ArchiveCheckState.DirectoryRead ? files : 0,
            budget.Bytes, budget.Attempts, watch.Elapsed, complete ? Array.AsReadOnly(decoded!.Evidence.ToArray()) : [],
            limitations.AsReadOnly(), error, cleanup);
    }
}

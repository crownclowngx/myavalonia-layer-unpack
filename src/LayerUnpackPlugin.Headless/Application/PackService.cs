using System.Diagnostics;
using LayerUnpackPlugin.Headless.Contracts;
using LayerUnpackPlugin.Headless.Infrastructure;

namespace LayerUnpackPlugin.Headless.Application;

public interface IPackService
{
    Task<PackPlan> PrepareAsync(PackRequest request, IProgress<PackProgress>? progress = null, CancellationToken cancellationToken = default);
    Task<PackResult> ExecuteAsync(PackPlan plan, IProgress<PackProgress>? progress = null, CancellationToken cancellationToken = default, PackSecret? secret = null);
}

/// <summary>朴素的创建用例编排：规划器负责源，写入器负责 ZIP，事务负责目标。每次调用的状态均为局部变量。</summary>
public sealed class PackService(PackPlanner planner, IArchiveWriter writer) : IPackService
{
    public PackService() : this(new PackPlanner(), new ZipArchiveWriter()) { }
    public async Task<PackPlan> PrepareAsync(PackRequest request, IProgress<PackProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Limits.Validate();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(request.Limits.Timeout);
        try { return await planner.PrepareAsync(request, progress, timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new PackFailureException(PackError.Timeout, "输入准备超过时间上限。"); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { throw new PackFailureException(PackError.InputUnavailable, "无法准备输入，请检查来源、权限及输出路径的可用性。"); }
    }

    public async Task<PackResult> ExecuteAsync(PackPlan plan, IProgress<PackProgress>? progress = null, CancellationToken cancellationToken = default, PackSecret? secret = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.Request.Options.Encrypt != (secret is not null))
            throw new PackValidationException("加密开关与本次目标密码必须一致，不能自动降级为普通 ZIP。");
        if (secret is not null) _ = secret.Password;
        if (plan.Entries.Count == 0)
            return new(PackState.Skipped, null, 0, 0, 0, TimeSpan.Zero, null, null);
        var watch = Stopwatch.StartNew();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(plan.Request.Limits.Timeout);
        var token = timeout.Token;
        var transaction = new PackFileTransaction(plan.Request.OutputPath);
        PackDiagnostic? error = null;
        var state = PackState.Failed;
        var reading = true;
        try
        {
            token.ThrowIfCancellationRequested();
            planner.VerifyInventory(plan, null, token);
            reading = false;
            long length;
            await using (var file = transaction.Open())
            {
                using var output = new PackOutputStream(file, plan.Request.Limits.MaxArchiveBytes, token);
                await writer.WriteAsync(plan, output, progress, token, secret).ConfigureAwait(false);
                await output.FlushAsync(token).ConfigureAwait(false);
                length = file.Length;
            }
            reading = true;
            planner.VerifyInventory(plan, transaction.TemporaryPath, token);
            reading = false;
            var committed = transaction.Commit(token);
            return new(PackState.Completed, committed, plan.TotalBytes, length, plan.FileCount, watch.Elapsed, null, null);
        }
        catch (Exception) when (token.IsCancellationRequested)
        {
            state = cancellationToken.IsCancellationRequested ? PackState.Cancelled : PackState.Failed;
            if (state == PackState.Failed) error = new(PackError.Timeout, "压缩超过时间上限，未提交 ZIP。");
        }
        catch (PackFailureException e) { error = new(e.Code, e.Message); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { error = new(reading ? PackError.InputUnavailable : PackError.OutputError, reading ? "来源不可用，请重新检查输入。" : "无法写入或提交 ZIP，请检查输出目录、权限、占用和可用空间。"); }
        catch (Exception) { error = new(PackError.UnexpectedError, "创建未完成，请重新检查输入和输出位置。"); }
        return new(state, null, plan.TotalBytes, 0, plan.FileCount, watch.Elapsed, error, transaction.Rollback());
    }
}

using LayerUnpackPlugin.Headless.Application;
using LayerUnpackPlugin.Headless.Contracts;
using Xunit;

namespace LayerUnpackPlugin.Headless.Tests;

public sealed class LifecycleTests
{
    [Fact]
    public async Task 取消排空工作并回滚当前包但保留已提交包()
    {
        using var w = new TestWorkspace();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var extractor = new DelegatingExtractor(async call =>
        {
            var result = await call.WriteAsync([1, 2, 3]);
            if (Path.GetFileName(call.Source) == "b.zip") { started.SetResult(); await Task.Delay(Timeout.Infinite, call.Token); }
            return result;
        });
        await using var session = new UnpackService(extractor).CreateSession(new([w.Zip("a.zip"), w.Zip("b.zip"), w.Zip("c.zip")], w.Output));
        using var cancellation = new CancellationTokenSource();
        var work = session.ExecuteAsync(cancellationToken: cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        cancellation.Cancel();
        var result = await work.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(BatchState.Cancelled, result.State);
        Assert.Equal(new[] { NodeState.Extracted, NodeState.Cancelled, NodeState.Cancelled }, result.Nodes.Select(n => n.State));
        Assert.Single(Directory.GetDirectories(w.Output));
        Assert.Empty(Directory.GetDirectories(w.Output, ".layer-unpack-*"));
    }

    [Fact]
    public async Task 并发释放调用都等待引擎退出且执行期间禁止第二次操作()
    {
        using var w = new TestWorkspace();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var extractor = new DelegatingExtractor(async call =>
        {
            entered.SetResult();
            try { await Task.Delay(Timeout.Infinite, call.Token); }
            finally { cancelled.SetResult(); await release.Task; }
            return new("Test", [], false);
        });
        var session = new UnpackService(extractor).CreateSession(new([w.Zip("a.zip")], w.Output));
        var work = session.ExecuteAsync(cancellationToken: TestContext.Current.CancellationToken);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
            await Assert.ThrowsAsync<InvalidOperationException>(() => session.ExecuteAsync(cancellationToken: TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<InvalidOperationException>(() => session.RetryAsync([Guid.NewGuid()], [], cancellationToken: TestContext.Current.CancellationToken));
            var first = session.DisposeAsync().AsTask();
            var second = session.DisposeAsync().AsTask();
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
            Assert.False(first.IsCompleted); Assert.False(second.IsCompleted);
            release.SetResult();
            await Task.WhenAll(first, second, work).WaitAsync(TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(BatchState.Cancelled, session.Snapshot.State);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => session.ExecuteAsync(cancellationToken: TestContext.Current.CancellationToken));
        }
        finally { release.TrySetResult(); await session.DisposeAsync(); }
    }

    [Fact]
    public async Task 单包超时返回明确诊断并继续后续包()
    {
        using var w = new TestWorkspace();
        var extractor = new DelegatingExtractor(async call =>
        {
            if (Path.GetFileName(call.Source) == "a.zip") await Task.Delay(Timeout.Infinite, call.Token);
            return await call.WriteAsync([1]);
        });
        await using var session = new UnpackService(extractor).CreateSession(new([w.Zip("a.zip"), w.Zip("b.zip")], w.Output,
            limits: new() { ArchiveTimeout = TimeSpan.FromMilliseconds(100) }));
        var result = await session.ExecuteAsync(cancellationToken: TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(BatchState.PartialFailure, result.State);
        Assert.Equal(UnpackError.Timeout, result.Nodes[0].Error?.Code);
        Assert.Equal(NodeState.Extracted, result.Nodes[1].State);
    }

    [Fact]
    public async Task 重试校验源摘要和会话身份不复用旧节点()
    {
        using var w = new TestWorkspace();
        var path = w.CopyFixture("Zip.deflate.pkware.zip");
        await using var session = new UnpackService().CreateSession(new([path], w.Output));
        var first = await session.ExecuteAsync(cancellationToken: TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.RetryAsync([Guid.NewGuid()], ["12345678"], cancellationToken: TestContext.Current.CancellationToken));
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken: TestContext.Current.CancellationToken); bytes[^1] ^= 1; await File.WriteAllBytesAsync(path, bytes, cancellationToken: TestContext.Current.CancellationToken);
        var second = await session.RetryAsync([first.Nodes[0].Id], ["12345678"], cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(UnpackError.InputChanged, second.Nodes[0].Error?.Code);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.RetryAsync([first.Nodes[0].Id], [], cancellationToken: TestContext.Current.CancellationToken));
        Assert.Empty(Directory.GetFileSystemEntries(w.Output));
    }

    [Fact]
    public async Task 候选命中顺序跨包共享但不会穿透到另一会话()
    {
        using var w = new TestWorkspace();
        var attempts = new List<string?>();
        var extractor = new DelegatingExtractor(call =>
        {
            attempts.Add(call.Password);
            if (call.Password != "right") throw new UnpackFailureException(UnpackError.PasswordRequiredOrInvalid, "候选不可用。");
            return call.WriteAsync([1]);
        });
        var inputs = new[] { w.Zip("a.zip"), w.Zip("b.zip") };
        await using (var session = new UnpackService(extractor).CreateSession(new(inputs, w.Output, passwords: ["wrong", "right"])))
            Assert.Equal(2, (await session.ExecuteAsync(cancellationToken: TestContext.Current.CancellationToken)).Succeeded);
        Assert.Equal(new string?[] { null, "wrong", "right", null, "right" }, attempts);
        attempts.Clear();
        await using var fresh = new UnpackService(extractor).CreateSession(new([inputs[0]], w.Output));
        Assert.Equal(BatchState.Failed, (await fresh.ExecuteAsync(cancellationToken: TestContext.Current.CancellationToken)).State);
        Assert.Equal(new string?[] { null }, attempts);
    }

    [Fact]
    public async Task 重试继续消耗旧账本的尝试次数()
    {
        using var w = new TestWorkspace();
        var extractor = new DelegatingExtractor(_ => throw new UnpackFailureException(UnpackError.PasswordRequiredOrInvalid, "候选不可用。"));
        await using var session = new UnpackService(extractor).CreateSession(new([w.Zip("a.zip")], w.Output, limits: new() { MaxAttempts = 2 }));
        var first = await session.ExecuteAsync(cancellationToken: TestContext.Current.CancellationToken);
        var second = await session.RetryAsync([first.Nodes[0].Id], ["new"], cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(UnpackError.BudgetExceeded, second.Nodes[0].Error?.Code);
        Assert.Equal(3, second.AttemptCount);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.RetryAsync([first.Nodes[0].Id], [], cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task 观察者异常不改变已提交结果且快照不随后续改变()
    {
        using var w = new TestWorkspace();
        await using var session = new UnpackService().CreateSession(new([w.Zip("a.zip", ("a", [1]))], w.Output));
        var ready = session.Snapshot;
        var result = await session.ExecuteAsync(new ThrowingProgress(), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(BatchState.Completed, result.State);
        Assert.Equal(BatchState.Ready, ready.State);
        Assert.Equal(NodeState.Queued, ready.Nodes[0].State);
    }

    private sealed class ThrowingProgress : IProgress<UnpackProgress>
    {
        public void Report(UnpackProgress value) => throw new InvalidOperationException("观察者错误");
    }
}

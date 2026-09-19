using LayerUnpackPlugin.Headless.Application;
using LayerUnpackPlugin.Headless.Contracts;
using LayerUnpackPlugin.Headless.Infrastructure;
using Xunit;

namespace LayerUnpackPlugin.Headless.Tests;

public sealed class LayeredSchedulingTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task 层间等长同时间替换续卷仍被父提交摘要拒绝()
    {
        using var w = new TestWorkspace(); var parts = SplitTestData.CopyParts(w);
        var outer = w.Zip("outer.zip", parts.Select(p => (Path.GetFileName(p), File.ReadAllBytes(p))).ToArray());
        var changed = false;
        await using var session = new UnpackService().CreateSession(new([outer], w.Output, 2));
        var result = await session.ExecuteAsync(new Callback(p =>
        {
            var parent = p.Snapshot.Nodes[0];
            if (changed || parent.State != NodeState.Extracted) return;
            changed = true;
            var path = Path.Combine(parent.OutputDirectory!, Path.GetFileName(parts[1])); var time = File.GetLastWriteTimeUtc(path);
            var bytes = File.ReadAllBytes(path); bytes[10] ^= 1; File.WriteAllBytes(path, bytes); File.SetLastWriteTimeUtc(path, time);
        }), Token);
        Assert.Equal(NodeState.Extracted, result.Nodes[0].State);
        Assert.Equal(UnpackError.InputChanged, result.Nodes[1].Error?.Code); Assert.Equal(1, result.AttemptCount);
    }

    [Fact]
    public async Task 多根多分支严格等本层全部结束再进入下一层()
    {
        using var w = new TestWorkspace();
        var a11 = w.Zip("A11.zip", ("value", [1]));
        var a1 = w.Zip("A1.zip", ("A11.zip", File.ReadAllBytes(a11)));
        var a2 = w.Zip("A2.zip", ("value", [2])); var b1 = w.Zip("B1.zip", ("value", [3]));
        var a = w.Zip("A.zip", ("A1.zip", File.ReadAllBytes(a1)), ("A2.zip", File.ReadAllBytes(a2)));
        var b = w.Zip("B.zip", ("B1.zip", File.ReadAllBytes(b1)));
        var events = new List<string>();
        var extractor = new DelegatingExtractor(async call =>
        {
            events.Add("start " + Path.GetFileName(call.Source));
            await Task.Delay(5, call.Token);
            var result = await new ArchiveExtractor().ExtractAsync(call.Source, call.Destination, call.Password, call.Encoding, call.Budget, call.Progress, call.Token);
            events.Add("end " + Path.GetFileName(call.Source)); return result;
        });
        await using var session = new UnpackService(extractor).CreateSession(new([a, b], w.Output, 3));
        Assert.Equal(BatchState.Completed, (await session.ExecuteAsync(cancellationToken: Token)).State);
        Assert.Equal(new[] { "A.zip", "B.zip", "A1.zip", "A2.zip", "B1.zip", "A11.zip" }.SelectMany(n => new[] { "start " + n, "end " + n }), events);
    }

    [Fact]
    public async Task 混合深度重试的新第三层必须先于已选第四层且父节点身份不变()
    {
        using var w = new TestWorkspace();
        var c = w.Zip("C.zip", ("value", [1])); var y = w.Zip("Y.zip", ("value", [2]));
        var x = w.Zip("X.zip", ("Y.zip", File.ReadAllBytes(y)));
        var a = w.Zip("A.zip", ("C.zip", File.ReadAllBytes(c)));
        var b = w.Zip("B.zip", ("X.zip", File.ReadAllBytes(x)));
        var root = w.Zip("root.zip", ("A.zip", File.ReadAllBytes(a)), ("B.zip", File.ReadAllBytes(b)));
        var recovered = false; var events = new List<string>();
        var extractor = new DelegatingExtractor(async call =>
        {
            var name = Path.GetFileName(call.Source); events.Add(name);
            if (!recovered && name is "A.zip" or "Y.zip") throw new UnpackFailureException(UnpackError.PasswordRequiredOrInvalid, "测试补密");
            return await new ArchiveExtractor().ExtractAsync(call.Source, call.Destination, call.Password, call.Encoding, call.Budget, call.Progress, call.Token);
        });
        await using var session = new UnpackService(extractor).CreateSession(new([root], w.Output, 4));
        var first = await session.ExecuteAsync(cancellationToken: Token);
        var failed = first.Nodes.Where(n => n.CanRetry).OrderByDescending(n => n.Depth).Select(n => n.Id).ToArray(); Assert.Equal(2, failed.Length);
        var identities = first.Nodes.Where(n => n.State == NodeState.Extracted).ToDictionary(n => n.Id, n => n.OutputDirectory);
        recovered = true; events.Clear();
        var second = await session.RetryAsync(failed, [], cancellationToken: Token);
        Assert.Equal(new[] { "A.zip", "C.zip", "Y.zip" }, events); Assert.Equal(BatchState.Completed, second.State);
        Assert.Equal(first.AttemptCount + 3, second.AttemptCount);
        Assert.All(identities, pair => Assert.Equal(pair.Value, second.Nodes.Single(n => n.Id == pair.Key).OutputDirectory));
    }

    [Fact]
    public async Task 发现只使用提交清单且新出现的同名卷不能接管()
    {
        using var w = new TestWorkspace(); var parts = SplitTestData.CopyParts(w);
        var outer = w.Zip("outer.zip", parts.Take(4).Select(p => (Path.GetFileName(p), File.ReadAllBytes(p))).ToArray());
        var injected = false;
        await using var session = new UnpackService().CreateSession(new([outer], w.Output, 2));
        var result = await session.ExecuteAsync(new Callback(p =>
        {
            var root = p.Snapshot.Nodes[0];
            if (root.State != NodeState.Extracted || injected) return;
            injected = true; File.Copy(parts[4], Path.Combine(root.OutputDirectory!, Path.GetFileName(parts[4])));
            File.WriteAllBytes(Path.Combine(root.OutputDirectory!, "historical.zip"), [1]);
        }), Token);
        Assert.Equal(2, result.Nodes.Count); Assert.Equal(4, result.Nodes[1].SourceMembers.Count);
        Assert.Equal(UnpackError.InputChanged, result.Nodes[1].Error?.Code); Assert.Equal(NodeState.Extracted, result.Nodes[0].State);
    }

    [Fact]
    public async Task 层间取消保留已提交根且不会解下一层()
    {
        using var w = new TestWorkspace(); var inner = w.Zip("inner.zip", ("value", [1]));
        var outer = w.Zip("outer.zip", ("inner.zip", File.ReadAllBytes(inner)));
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Token);
        await using var session = new UnpackService().CreateSession(new([outer], w.Output, 2));
        var result = await session.ExecuteAsync(new Callback(p => { if (p.Snapshot.Nodes[0].State == NodeState.Extracted) cancel.Cancel(); }), cancel.Token);
        Assert.Equal(BatchState.Cancelled, result.State); Assert.Equal(NodeState.Extracted, result.Nodes[0].State);
        Assert.Equal(1, result.AttemptCount); Assert.Equal(1, result.DiscoveryFailures);
    }

    private sealed class Callback(Action<UnpackProgress> action) : IProgress<UnpackProgress>
    { public void Report(UnpackProgress value) => action(value); }
}

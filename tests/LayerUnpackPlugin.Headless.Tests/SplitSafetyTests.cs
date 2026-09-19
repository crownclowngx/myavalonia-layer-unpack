using LayerUnpackPlugin.Headless.Application;
using LayerUnpackPlugin.Headless.Contracts;
using LayerUnpackPlugin.Headless.Infrastructure;
using Xunit;

namespace LayerUnpackPlugin.Headless.Tests;

public sealed class SplitSafetyTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task 真实解码写出时取消不提交且全部卷可再次独占打开()
    {
        using var w = new TestWorkspace(); var parts = SplitTestData.CopyParts(w);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var extractor = new DelegatingExtractor(call => new ArchiveExtractor().ExtractAsync(call.Source, call.Destination,
            call.Password, call.Encoding, call.Budget, count => { call.Progress(count); cancel.Cancel(); }, call.Token));
        await using var session = new UnpackService(extractor).CreateSession(new(parts, w.Output));
        var result = await session.ExecuteAsync(cancellationToken: cancel.Token);
        Assert.Equal(BatchState.Cancelled, result.State); Assert.Equal(0, result.Succeeded);
        Assert.True(result.TotalWrittenBytes > 0); Assert.Empty(Directory.GetDirectories(w.Output));
        foreach (var part in parts) { using var file = File.Open(part, FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
    }

    [Fact]
    public async Task 输出预算失败不退款且不会继续下一个来源()
    {
        using var w = new TestWorkspace(); var parts = SplitTestData.CopyParts(w); var next = w.Zip("next.zip", ("a", [1]));
        await using var session = new UnpackService().CreateSession(new(parts.Append(next), w.Output,
            limits: new() { MaxFileBytes = 65000, MaxTotalBytes = 65000 }));
        var result = await session.ExecuteAsync(cancellationToken: Token);
        Assert.Equal(UnpackError.BudgetExceeded, result.Nodes[0].Error?.Code);
        Assert.Equal(NodeState.NotRun, result.Nodes[1].State); Assert.True(result.RetryBlocked);
        Assert.Empty(Directory.GetDirectories(w.Output));
    }

    [Fact]
    public async Task 并发独立会话不会覆盖同名输出()
    {
        using var w = new TestWorkspace(); var parts = SplitTestData.CopyParts(w);
        await using var a = new UnpackService().CreateSession(new([parts[0]], w.Output));
        await using var b = new UnpackService().CreateSession(new([parts[1]], w.Output));
        var results = await Task.WhenAll(a.ExecuteAsync(cancellationToken: Token), b.ExecuteAsync(cancellationToken: Token));
        Assert.All(results, r => Assert.Equal(BatchState.Completed, r.State));
        Assert.NotEqual(results[0].Nodes[0].OutputDirectory, results[1].Nodes[0].OutputDirectory);
        foreach (var result in results) SplitTestData.AssertContents(result.Nodes[0].OutputDirectory!);
    }

    [Fact]
    public async Task 非七字格式数字卷不能假借名称交给其他解码器()
    {
        using var w = new TestWorkspace(); var path = w.Zip("fake.7z.001", ("a", [1]));
        await using var session = new UnpackService().CreateSession(new([path], w.Output));
        Assert.Equal(UnpackError.UnsupportedFormat, Assert.Single((await session.ExecuteAsync(cancellationToken: Token)).Nodes).Error?.Code);
    }

    [Fact]
    public async Task 目录检查仍明确拒绝七字分卷而完整检查不会放宽路径策略()
    {
        using var w = new TestWorkspace(); var parts = SplitTestData.CopyParts(w);
        var check = await new ArchiveCheckService().CheckAsync(new(parts[0], ArchiveCheckScope.DirectoryOnly), cancellationToken: Token);
        Assert.Equal(ArchiveCheckState.Failed, check.State); Assert.Equal(UnpackError.UnsupportedFormat, check.Error?.Code);
        var limits = new UnpackLimits { MaxVolumesPerArchive = 4 };
        var full = await new ArchiveCheckService().CheckAsync(new(parts[1], limits: limits), cancellationToken: Token);
        Assert.Equal(UnpackError.BudgetExceeded, full.Error?.Code); Assert.Equal(0, full.Attempts);
    }

    [Fact]
    public async Task 内层成功卷与来源进入整理打包时清单归属保持一致()
    {
        using var w = new TestWorkspace(); var parts = SplitTestData.CopyParts(w);
        var outer = w.Zip("outer.zip", parts.Select(p => (Path.GetFileName(p), File.ReadAllBytes(p))).ToArray());
        await using var session = new UnpackService().CreateSession(new([outer], w.Output, 2));
        var result = await session.ExecuteAsync(cancellationToken: Token);
        var plan = await new OrganizationPlanner().CreateAsync(result, w.FilePath("organized"), cancellationToken: Token);
        Assert.Single(plan.Sources);
        Assert.Equal(8, plan.Mappings.Count(m => m.SourceId == result.Nodes[0].Id && !m.IsDirectory));
        Assert.All(plan.Mappings, m => Assert.Equal(result.Nodes[0].Id, m.SourceId));
        var repack = new RepackService();
        var packPlan = await repack.PrepareAsync(result, w.FilePath("packed"), PackGrouping.Combined, new(), Token);
        var packed = await repack.ExecuteAsync(packPlan, cancellationToken: Token);
        Assert.All(packed.Groups, g => Assert.Equal(RepackState.Completed, g.State));
    }
}

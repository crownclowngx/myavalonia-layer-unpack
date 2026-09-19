using LayerUnpackPlugin.Headless.Application;
using LayerUnpackPlugin.Headless.Contracts;
using LayerUnpackPlugin.Headless.Infrastructure;
using Xunit;

namespace LayerUnpackPlugin.Headless.Tests;

public sealed class SplitSafetyTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task 已有预览不能绕过执行时更严格的卷数预算()
    {
        using var w = new TestWorkspace(); var parts = SplitTestData.CopyParts(w);
        var preview = ArchiveSourceResolver.ResolveInputs(parts, new(), Token);
        await using var session = new UnpackService().CreateSession(new([preview[0].PrimaryPath], w.Output,
            limits: new() { MaxVolumesPerArchive = 4 }, inputSnapshot: preview));
        var result = await session.ExecuteAsync(cancellationToken: Token);
        Assert.Equal(UnpackError.BudgetExceeded, result.Nodes[0].Error?.Code); Assert.Equal(0, result.AttemptCount);
    }

    [Fact]
    public void 发现项上限与卷数配置上限都不能溢出或截断后继续()
    {
        using var w = new TestWorkspace();
        var error = Assert.Throws<UnpackFailureException>(() => ArchiveSourceResolver.ResolveInputs(
            Enumerable.Repeat(w.FilePath("a.7z.001"), ArchiveSourceResolver.MaximumDiscoveredPaths + 1), new(), Token));
        Assert.Equal(UnpackError.BudgetExceeded, error.Code);
        foreach (var count in new[] { 0, 1025, int.MaxValue })
            Assert.Throws<UnpackValidationException>(() => new UnpackLimits { MaxVolumesPerArchive = count }.Validate());
        new UnpackLimits { MaxVolumesPerArchive = 1024 }.Validate();
    }

    [Theory]
    [InlineData(ConversionMode.FormatOnly, 5)]
    [InlineData(ConversionMode.ExpandAndOrganize, 8)]
    public async Task 普通转换保留原卷字节而展开转换包含解出的文件(ConversionMode mode, int expectedFiles)
    {
        using var w = new TestWorkspace(); var parts = SplitTestData.CopyParts(w);
        var outer = w.Zip("outer.zip", parts.Select(p => (Path.GetFileName(p), File.ReadAllBytes(p))).ToArray());
        var result = await new RepackService().ConvertAsync(new([outer], w.Output, mode), cancellationToken: Token);
        var group = Assert.Single(result.Groups); Assert.Equal(RepackState.Completed, group.State);
        using var archive = System.IO.Compression.ZipFile.OpenRead(group.OutputPath!);
        Assert.Equal(expectedFiles, archive.Entries.Count(e => !e.FullName.EndsWith('/')));
        foreach (var part in parts)
        {
            var entry = Assert.Single(archive.Entries, e => e.FullName.EndsWith(Path.GetFileName(part), StringComparison.Ordinal));
            using var stream = entry.Open(); using var data = new MemoryStream(); await stream.CopyToAsync(data, Token);
            Assert.Equal(File.ReadAllBytes(part), data.ToArray());
        }
    }

    [Fact]
    public async Task 两组不同密码互不污染且失败组可补密恢复()
    {
        using var w = new TestWorkspace(); var first = SplitTestData.CopyParts(w, "content-encrypted");
        var single = w.CopyFixture("7Zip.LZMA.Aes.7z"); var bytes = File.ReadAllBytes(single); var parts = new List<string>();
        for (var i = 0; i < bytes.Length; i += 1024)
        {
            var path = w.FilePath($"other.7z.{parts.Count + 1:D3}"); File.WriteAllBytes(path, bytes.AsSpan(i, Math.Min(1024, bytes.Length - i)).ToArray()); parts.Add(path);
        }
        await using var session = new UnpackService().CreateSession(new([first[1], parts[0]], w.Output, passwords: ["volume-test"]));
        var initial = await session.ExecuteAsync(cancellationToken: Token); Assert.Equal(1, initial.Succeeded); Assert.Equal(1, initial.Failed);
        var failed = initial.Nodes.Single(n => n.CanRetry);
        var final = await session.RetryAsync([failed.Id], ["testpassword"], cancellationToken: Token); Assert.Equal(2, final.Succeeded);
        Assert.Equal(initial.Nodes[0].OutputDirectory, final.Nodes[0].OutputDirectory);
    }

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

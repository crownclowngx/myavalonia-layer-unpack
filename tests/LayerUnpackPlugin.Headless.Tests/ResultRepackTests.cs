using System.IO.Compression;
using System.Text.Json;
using LayerUnpackPlugin.Headless.Application;
using LayerUnpackPlugin.Headless.Contracts;
using Xunit;

namespace LayerUnpackPlugin.Headless.Tests;

public sealed class ResultRepackTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    [Theory]
    [InlineData(PackGrouping.Combined)]
    [InlineData(PackGrouping.Separate)]
    public async Task 解压提交清单可检查合成或分别且准备只读(PackGrouping grouping)
    {
        using var w = new TestWorkspace(); var input = await Extract(w, w.Zip("a.zip", ("wrap/a.pdf", [1]), ("empty/", [])), w.Zip("b.zip", ("b.txt", [2])));
        var output = w.FilePath("repack"); var planner = new CommittedPackPlanner();
        var plan = await planner.PrepareAsync(input, output, grouping, cancellationToken: Token);
        Assert.Equal(grouping == PackGrouping.Combined ? 1 : 2, plan.Groups.Count); Assert.False(Directory.Exists(output));
        Assert.All(plan.Groups, g => Assert.NotEmpty(g.Plan.Entries));
        var result = await new RepackService().ExecuteAsync(plan, cancellationToken: Token);
        Assert.Equal(RepackState.Completed, result.State);
        for (var i = 0; i < plan.Groups.Count; i++)
        {
            Assert.Equal(plan.Groups[i].Plan.Entries.Where(e => !e.IsDirectory).ToDictionary(e => e.EntryName, e => e.Sha256).OrderBy(p => p.Key),
                ConversionTests.Digests(result.Groups[i].OutputPath!).OrderBy(p => p.Key));
            using var zip = ZipFile.OpenRead(result.Groups[i].OutputPath!);
            Assert.Equal(plan.Groups[i].Plan.Entries.Select(e => e.EntryName + (e.IsDirectory ? "/" : "")).Order(), zip.Entries.Select(e => e.FullName).Order());
        }
        Assert.All(input.Nodes, n => Assert.True(Directory.Exists(n.OutputDirectory))); Assert.Equal(0, result.Usage.ExpandedBytes);
    }

    [Theory]
    [InlineData(PackGrouping.Combined)]
    [InlineData(PackGrouping.Separate)]
    public async Task 整理成功结果保留来源边界及精确路径而无额外包装层(PackGrouping grouping)
    {
        using var w = new TestWorkspace(); var input = await Extract(w, w.Zip("a.zip", ("wrap/a.pdf", [1]), ("ignored.txt", [7])), w.Zip("b.zip", ("b.pdf", [2])));
        var organization = new OrganizationService();
        var organizedPlan = await organization.PlanAsync(input, w.FilePath("organized"), new(OrganizationFileTypes.Pdf), Token);
        var organized = await organization.ExecuteAsync(organizedPlan, cancellationToken: Token);
        Assert.Equal(OrganizationState.Completed, organized.State); Assert.Equal(2, organized.SourceGroups.Count);
        var plan = await new CommittedPackPlanner().PrepareAsync(organized, w.FilePath("repack"), grouping, cancellationToken: Token);
        Assert.False(Directory.Exists(w.FilePath("repack")));
        var result = await new RepackService().ExecuteAsync(plan, cancellationToken: Token);
        Assert.Equal(RepackState.Completed, result.State); Assert.Equal(grouping == PackGrouping.Combined ? 1 : 2, result.CommittedCount);
        Assert.All(plan.Groups.SelectMany(g => g.Plan.Entries), e => Assert.DoesNotContain("ignored.txt", e.EntryName));
        if (grouping == PackGrouping.Combined)
            Assert.Equal(organized.Entries.Where(e => !e.IsDirectory).ToDictionary(e => e.RelativePath, e => e.Sha256).OrderBy(p => p.Key),
                ConversionTests.Digests(result.Groups[0].OutputPath!).OrderBy(p => p.Key));
        Assert.True(Directory.Exists(organized.OutputDirectory));
    }

    [Theory]
    [InlineData("add")]
    [InlineData("delete")]
    [InlineData("same-length-content")]
    public async Task 预览后的来源变化拒绝写入且不混入历史文件(string change)
    {
        using var w = new TestWorkspace(); var input = await Extract(w, w.Zip("a.zip", ("data", [1, 2, 3])));
        var plan = await new CommittedPackPlanner().PrepareAsync(input, w.FilePath("repack"), cancellationToken: Token);
        var source = plan.Groups[0].Plan.Entries.Single(e => !e.IsDirectory).SourcePath;
        if (change == "add") File.WriteAllText(Path.Combine(input.Nodes[0].OutputDirectory!, "history.txt"), "history");
        else if (change == "delete") File.Delete(source);
        else
        {
            var time = File.GetLastWriteTimeUtc(source); File.WriteAllBytes(source, [3, 2, 1]); File.SetLastWriteTimeUtc(source, time);
        }
        var result = await new RepackService().ExecuteAsync(plan, cancellationToken: Token);
        Assert.Equal(RepackState.Failed, result.State); Assert.Equal("InputChanged", result.Groups[0].Error?.Code);
        Assert.False(Directory.Exists(w.FilePath("repack"))); Assert.True(Directory.Exists(input.Nodes[0].OutputDirectory));
    }

    [Fact]
    public async Task 准备时已有历史条目不能通过重新扫描洗成合法输入()
    {
        using var w = new TestWorkspace(); var input = await Extract(w, w.Zip("a.zip", ("data", [1])));
        File.WriteAllText(Path.Combine(input.Nodes[0].OutputDirectory!, "history.txt"), "history");
        var exception = await Assert.ThrowsAsync<OrganizationFailureException>(() => new CommittedPackPlanner().PrepareAsync(input, w.FilePath("repack"), cancellationToken: Token));
        Assert.Equal(OrganizationError.InputChanged, exception.Code); Assert.False(Directory.Exists(w.FilePath("repack")));
    }

    [Fact]
    public async Task 分别打包一组来源变化不影响其他来源()
    {
        using var w = new TestWorkspace(); var input = await Extract(w, w.Zip("a.zip", ("a", [1])), w.Zip("b.zip", ("b", [2])));
        var plan = await new CommittedPackPlanner().PrepareAsync(input, w.FilePath("repack"), PackGrouping.Separate, cancellationToken: Token);
        File.Delete(plan.Groups[0].Plan.Entries.Single().SourcePath);
        var result = await new RepackService().ExecuteAsync(plan, cancellationToken: Token);
        Assert.Equal(RepackState.PartiallyCompleted, result.State); Assert.Equal(1, result.CommittedCount);
        Assert.Equal(RepackState.Completed, result.Groups[1].State);
    }

    [Fact]
    public async Task 缺失清单拒绝打包且来源目录内不能创建结果()
    {
        using var w = new TestWorkspace(); var input = await Extract(w, w.Zip("a.zip", ("data", [1])));
        var planner = new CommittedPackPlanner();
        var missing = input with { Nodes = [input.Nodes[0] with { CommittedEntries = null }] };
        Assert.Equal(OrganizationError.MissingManifest, (await Assert.ThrowsAsync<OrganizationFailureException>(() => planner.PrepareAsync(missing, w.FilePath("repack"), cancellationToken: Token))).Code);
        Assert.Equal(OrganizationError.UnsafePath, (await Assert.ThrowsAsync<OrganizationFailureException>(() => planner.PrepareAsync(input, input.Nodes[0].OutputDirectory!, cancellationToken: Token))).Code);
    }

    [Fact]
    public async Task 整理分别模式缺失或不完整来源边界不允许静默丢内容()
    {
        using var w = new TestWorkspace(); var input = await Extract(w, w.Zip("a.zip", ("a", [1])), w.Zip("b.zip", ("b", [2])));
        var organization = new OrganizationService(); var organized = await organization.ExecuteAsync(await organization.PlanAsync(input, w.FilePath("organized"), new(), Token), cancellationToken: Token);
        var planner = new CommittedPackPlanner();
        await Assert.ThrowsAsync<PackValidationException>(() => planner.PrepareAsync(organized with { SourceGroups = [] }, w.FilePath("repack"), PackGrouping.Separate, cancellationToken: Token));
        await Assert.ThrowsAsync<PackValidationException>(() => planner.PrepareAsync(organized with { SourceGroups = [organized.SourceGroups[0]] }, w.FilePath("repack"), PackGrouping.Separate, cancellationToken: Token));
        var combined = await planner.PrepareAsync(organized with { SourceGroups = [] }, w.FilePath("repack"), cancellationToken: Token);
        Assert.Equal(2, combined.Groups[0].Plan.FileCount);
    }

    [Fact]
    public async Task 来源认证限制在整理和结果打包后仍保留()
    {
        using var w = new TestWorkspace(); var source = w.CopyFixture("Rar5.encrypted_filesOnly.rar");
        await using var session = new UnpackService().CreateSession(new([source], w.Output, passwords: ["test"]));
        var input = await session.ExecuteAsync(cancellationToken: Token);
        var organization = new OrganizationService();
        var organized = await organization.ExecuteAsync(await organization.PlanAsync(input, w.FilePath("organized"), new(), Token), cancellationToken: Token);
        Assert.NotEmpty(organized.SourceWarnings);
        var plan = await new CommittedPackPlanner().PrepareAsync(organized, w.FilePath("repack"), cancellationToken: Token);
        var result = await new RepackService().ExecuteAsync(plan, cancellationToken: Token);
        Assert.Equal(RepackState.CompletedWithWarnings, result.State); Assert.Equal(1, result.RestrictedCount);
        Assert.Contains("未验证", string.Join("\n", result.Groups[0].Warnings));
        Assert.DoesNotContain("\"Passwords\"", JsonSerializer.Serialize(new { plan, result }));
    }

    [Fact]
    public async Task 取消结果打包只清理目标暂存原解压和整理目录仍完整()
    {
        using var w = new TestWorkspace(); var input = await Extract(w, w.Zip("a.zip", ("a", new byte[300_000])));
        var plan = await new CommittedPackPlanner().PrepareAsync(input, w.FilePath("repack"), cancellationToken: Token);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var result = await new RepackService().ExecuteAsync(plan, progress: new CancellingProgress(cancellation), cancellationToken: cancellation.Token);
        Assert.Equal(RepackState.Cancelled, result.State); Assert.Equal(0, result.CommittedCount);
        Assert.Equal(300_000, new FileInfo(Path.Combine(input.Nodes[0].OutputDirectory!, "a")).Length);
        Assert.Empty(Directory.GetFiles(w.FilePath("repack")));
    }

    private sealed class CancellingProgress(CancellationTokenSource source) : IProgress<RepackProgress>
    { public void Report(RepackProgress value) { if (value.Phase == RepackPhase.Writing) source.Cancel(); } }
    internal static async Task<UnpackResult> Extract(TestWorkspace w, params string[] sources)
    {
        await using var session = new UnpackService().CreateSession(new(sources, w.Output));
        var result = await session.ExecuteAsync(cancellationToken: Token); Assert.Equal(BatchState.Completed, result.State); return result;
    }
}

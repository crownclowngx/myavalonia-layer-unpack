using System.Security.Cryptography;
using LayerUnpackPlugin.Headless.Application;
using LayerUnpackPlugin.Headless.Contracts;
using LayerUnpackPlugin.Headless.Infrastructure;
using Xunit;

namespace LayerUnpackPlugin.Headless.Tests;

/// <summary>通过真实 ZIP→递归解压→整理验证契约，不用伪造文件列表证明复制成功。</summary>
public sealed class OrganizationTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(OrganizationFileTypes.All, 5)]
    [InlineData(OrganizationFileTypes.Pdf, 2)]
    [InlineData(OrganizationFileTypes.Images, 1)]
    [InlineData(OrganizationFileTypes.PdfAndImages, 3)]
    public async Task 多来源按扩展名提取保留来源边界与内容摘要(OrganizationFileTypes types, int expected)
    {
        using var w = new TestWorkspace();
        var result = await OrganizationTestData.ExtractAsync(w, 1,
            w.Zip("甲/资料.zip", ("包装/指南.PDF", "甲"u8.ToArray()), ("照片.JpEg", [3]), ("empty/", [])),
            w.Zip("乙/资料.zip", ("包装/指南.pdf", "乙"u8.ToArray()), ("伪pdf.txt", "%PDF-test"u8.ToArray()), ("子包.zip", File.ReadAllBytes(w.Zip("inner.zip", ("data", [1]))))));
        var plan = await new OrganizationPlanner().CreateAsync(result, w.Output, new(types), cancellationToken: Token);
        var sourceCount = types == OrganizationFileTypes.Images ? 1 : 2;
        Assert.Equal(expected, plan.FileCount); Assert.Equal(sourceCount, plan.Sources.Count);
        Assert.Equal(sourceCount, plan.Sources.Select(s => s.TargetName).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        if (sourceCount > 1) Assert.Contains(plan.Conflicts, c => c.Reason.Contains("不同来源", StringComparison.Ordinal));
        Assert.Equal(types == OrganizationFileTypes.All, plan.Mappings.Any(m => m.TargetRelativePath.EndsWith("empty", StringComparison.Ordinal)));
        Assert.False(Directory.Exists(plan.OutputDirectory));
        await OrganizationTestData.VerifyCopyAsync(plan);
    }

    [Fact]
    public async Task 嵌套包与展开目录各收集一次且归属顶层来源()
    {
        using var w = new TestWorkspace();
        var inner = w.Zip("inner.zip", ("资料/内部.pdf", [1, 2]), ("empty/", []));
        var source = w.Zip("outer.zip", ("包装/inner.zip", File.ReadAllBytes(inner)), ("包装/外部.pdf", [3]));
        var original = File.ReadAllBytes(source);
        var result = await OrganizationTestData.ExtractAsync(w, 2, source);
        Assert.Equal(2, result.Succeeded);
        Assert.DoesNotContain(result.Nodes[0].CommittedEntries!, e => e.RelativePath.Contains("内部.pdf", StringComparison.Ordinal));
        var plan = await new OrganizationPlanner().CreateAsync(result, w.Output, cancellationToken: Token);
        Assert.Single(plan.Sources); Assert.Equal(3, plan.FileCount);
        Assert.Equal(plan.Mappings.Count, plan.Mappings.Select(m => m.SourcePath).Distinct().Count());
        Assert.All(plan.Mappings, m => Assert.Equal(result.Nodes[0].Id, m.SourceId));
        Assert.Contains(plan.Mappings, m => m.SourcePath.EndsWith("inner.zip", StringComparison.Ordinal));
        await OrganizationTestData.VerifyCopyAsync(plan);
        Assert.Equal(original, File.ReadAllBytes(source));
    }

    [Theory]
    [InlineData(false, "")]
    [InlineData(true, "第一层/第二层")]
    public async Task 连续包装链仅显式开启才压平且不改文件名(bool flatten, string removed)
    {
        using var w = new TestWorkspace();
        var input = await OrganizationTestData.ExtractAsync(w, 1, w.Zip("资料.zip", ("第一层/第二层/说明.pdf", [7])));
        var plan = await new OrganizationPlanner().CreateAsync(input, w.Output, new(OrganizationFileTypes.Pdf, flatten), cancellationToken: Token);
        Assert.Equal(removed, Assert.Single(plan.Sources).RemovedPrefix);
        Assert.EndsWith(flatten ? "资料/说明.pdf" : "资料/第一层/第二层/说明.pdf", Assert.Single(plan.Mappings, m => !m.IsDirectory).TargetRelativePath);
        await OrganizationTestData.VerifyCopyAsync(plan);
    }

    [Theory]
    [InlineData("同层.txt")]
    [InlineData("空目录/")]
    public async Task 非匹配同层文件或空目录仍阻止压平(string extra)
    {
        using var w = new TestWorkspace();
        var input = await OrganizationTestData.ExtractAsync(w, 1, w.Zip("资料.zip", ("第一层/第二层/file.pdf", [1]), ($"第一层/{extra}", [])));
        var plan = await new OrganizationPlanner().CreateAsync(input, w.Output, new(OrganizationFileTypes.Pdf, true), cancellationToken: Token);
        Assert.Equal("第一层", Assert.Single(plan.Sources).RemovedPrefix);
        Assert.Contains(plan.Mappings, m => m.TargetRelativePath == "资料/第二层/file.pdf");
        await OrganizationTestData.VerifyCopyAsync(plan);
    }

    [Fact]
    public async Task 空目录来源和无匹配类型不会产生伪成功目录()
    {
        using var w = new TestWorkspace();
        var input = await OrganizationTestData.ExtractAsync(w, 1, w.Zip("empty.zip", ("empty/", [])), w.Zip("text.zip", ("only.txt", [1])));
        var plan = await new OrganizationPlanner().CreateAsync(input, w.Output, new(OrganizationFileTypes.Pdf), cancellationToken: Token);
        Assert.Empty(plan.Mappings);
        var result = await new OrganizationService().ExecuteAsync(plan, cancellationToken: Token);
        Assert.Equal(OrganizationState.NoMatches, result.State); Assert.Null(result.OutputDirectory);
        Assert.False(Directory.Exists(plan.OutputDirectory));
    }

    [Fact]
    public async Task 来源大小写冲突和已有目标在预览中稳定编号()
    {
        using var w = new TestWorkspace();
        var input = await OrganizationTestData.ExtractAsync(w, 1, w.Zip("one/Report.zip", ("a.pdf", [1])), w.Zip("two/report.zip", ("a.pdf", [2])));
        var occupied = Directory.CreateDirectory(Path.Combine(w.Output, "整理结果")).FullName;
        File.WriteAllText(Path.Combine(occupied, "existing.txt"), "keep");
        var planner = new OrganizationPlanner();
        var first = await planner.CreateAsync(input, w.Output, cancellationToken: Token);
        var second = await planner.CreateAsync(input, w.Output, cancellationToken: Token);
        Assert.Equal(first.Mappings, second.Mappings); Assert.Equal(first.OutputDirectory, second.OutputDirectory);
        Assert.Equal(2, first.Conflicts.Count); Assert.EndsWith("整理结果 (1)", first.OutputDirectory);
        await OrganizationTestData.VerifyCopyAsync(first);
        Assert.Equal("keep", File.ReadAllText(Path.Combine(occupied, "existing.txt")));
    }

    [Fact]
    public async Task 清单重复路径只处理一次且预览后调用方集合修改不改变计划()
    {
        using var w = new TestWorkspace();
        var input = await OrganizationTestData.ExtractAsync(w, 1, w.Zip("source.zip", ("a.pdf", [1])));
        var node = input.Nodes[0]; var entries = node.CommittedEntries!.Concat(node.CommittedEntries!).ToList();
        input = input with { Nodes = [node with { CommittedEntries = entries }] };
        var plan = await new OrganizationPlanner().CreateAsync(input, w.Output, cancellationToken: Token);
        entries.Clear(); Assert.Equal(1, plan.FileCount);
        await OrganizationTestData.VerifyCopyAsync(plan);
    }

    [Fact]
    public async Task 只采用当前成功节点不接管历史目录或失败节点()
    {
        using var w = new TestWorkspace();
        Directory.CreateDirectory(w.Output); File.WriteAllText(Path.Combine(w.Output, "history.pdf"), "history");
        var corrupt = w.FilePath("broken.zip"); File.WriteAllBytes(corrupt, [1, 2, 3]);
        await using var session = new UnpackService().CreateSession(new([w.Zip("good.zip", ("a.pdf", [1])), corrupt], w.Output));
        var input = await session.ExecuteAsync(cancellationToken: Token);
        Assert.Equal(BatchState.PartialFailure, input.State);
        Assert.Null(input.Nodes.Single(n => n.State == NodeState.Failed).CommittedEntries);
        var plan = await new OrganizationPlanner().CreateAsync(input, w.Output, cancellationToken: Token);
        Assert.Equal(1, plan.FileCount); await OrganizationTestData.VerifyCopyAsync(plan);
        Assert.Equal("history", File.ReadAllText(Path.Combine(w.Output, "history.pdf")));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("running")]
    [InlineData("parent")]
    [InlineData("escape")]
    public async Task 缺失清单运行中来源不完整与逃逸映射均拒绝(string scenario)
    {
        using var w = new TestWorkspace();
        var input = await OrganizationTestData.ExtractAsync(w, 1, w.Zip("source.zip", ("a.pdf", [1])));
        var node = input.Nodes[0];
        input = scenario switch
        {
            "missing" => input with { Nodes = [node with { CommittedEntries = null }] },
            "running" => input with { State = BatchState.Running },
            "parent" => input with { Nodes = [node with { ParentId = Guid.NewGuid() }] },
            _ => input with { Nodes = [node with { CommittedEntries = [node.CommittedEntries![0] with { RelativePath = "../outside.pdf" }] }] }
        };
        var error = await Assert.ThrowsAsync<OrganizationFailureException>(() => new OrganizationPlanner().CreateAsync(input, w.Output, cancellationToken: Token));
        Assert.Equal(scenario == "escape" ? OrganizationError.UnsafePath : OrganizationError.MissingManifest, error.Code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 来源改变不论在预览前还是预览后均检测同长度内容替换(bool afterPreview)
    {
        using var w = new TestWorkspace();
        var input = await OrganizationTestData.ExtractAsync(w, 1, w.Zip("source.zip", ("a.pdf", [1, 2])));
        var plan = afterPreview ? await new OrganizationPlanner().CreateAsync(input, w.Output, cancellationToken: Token) : null;
        var file = Path.Combine(input.Nodes[0].OutputDirectory!, "a.pdf"); var time = File.GetLastWriteTimeUtc(file);
        File.WriteAllBytes(file, [3, 4]); File.SetLastWriteTimeUtc(file, time);
        if (plan is null)
            Assert.Equal(OrganizationError.InputChanged, (await Assert.ThrowsAsync<OrganizationFailureException>(() => new OrganizationPlanner().CreateAsync(input, w.Output, cancellationToken: Token))).Code);
        else
            Assert.Equal(OrganizationError.InputChanged, (await new OrganizationService().ExecuteAsync(plan, cancellationToken: Token)).Error?.Code);
        Assert.Single(Directory.GetDirectories(w.Output));
    }

    [Theory]
    [InlineData("delete")]
    [InlineData("add")]
    [InlineData("directory")]
    [InlineData("filtered")]
    public async Task 预览后成员变化和筛选外来源变化也拒绝失效映射(string mutation)
    {
        using var w = new TestWorkspace();
        var input = await OrganizationTestData.ExtractAsync(w, 1, w.Zip("source.zip", ("a.pdf", [1]), ("other.txt", [2])));
        var plan = await new OrganizationPlanner().CreateAsync(input, w.Output, new(OrganizationFileTypes.Pdf), cancellationToken: Token);
        var root = input.Nodes[0].OutputDirectory!;
        if (mutation == "delete") File.Delete(Path.Combine(root, "a.pdf"));
        if (mutation == "add") File.WriteAllBytes(Path.Combine(root, "new.pdf"), [3]);
        if (mutation == "directory") { File.Delete(Path.Combine(root, "a.pdf")); Directory.CreateDirectory(Path.Combine(root, "a.pdf")); }
        if (mutation == "filtered") File.WriteAllBytes(Path.Combine(root, "other.txt"), [3]);
        var result = await new OrganizationService().ExecuteAsync(plan, cancellationToken: Token);
        Assert.Equal(OrganizationError.InputChanged, result.Error?.Code); Assert.False(Directory.Exists(plan.OutputDirectory));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task 条目单文件与总字节预算分别生效(int kind)
    {
        using var w = new TestWorkspace();
        var input = await OrganizationTestData.ExtractAsync(w, 1, w.Zip("source.zip", ("a.pdf", new byte[10]), ("b", new byte[10])));
        var limits = kind switch { 0 => new OrganizationLimits { MaxEntries = 1 }, 1 => new OrganizationLimits { MaxFileBytes = 5 }, _ => new OrganizationLimits { MaxFileBytes = 10, MaxTotalBytes = 15 } };
        Assert.Equal(OrganizationError.BudgetExceeded, (await Assert.ThrowsAsync<OrganizationFailureException>(() => new OrganizationPlanner().CreateAsync(input, w.Output, limits: limits, cancellationToken: Token))).Code);
    }

    [Theory]
    [InlineData("")]
    [InlineData("nested")]
    public async Task 输出不能在来源边界内(string child)
    {
        using var w = new TestWorkspace();
        var input = await OrganizationTestData.ExtractAsync(w, 1, w.Zip("source.zip", ("a.pdf", [1])));
        var output = Path.Combine(input.Nodes[0].OutputDirectory!, child);
        Assert.Equal(OrganizationError.UnsafePath, (await Assert.ThrowsAsync<OrganizationFailureException>(() => new OrganizationPlanner().CreateAsync(input, output, cancellationToken: Token))).Code);
    }
}

internal static class OrganizationTestData
{
    internal static async Task<UnpackResult> ExtractAsync(TestWorkspace w, int depth, params string[] archives)
    {
        await using var session = new UnpackService().CreateSession(new(archives, w.Output, depth));
        var result = await session.ExecuteAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(BatchState.Completed, result.State);
        Assert.All(result.Nodes.Where(n => n.State == NodeState.Extracted), n => Assert.NotNull(n.CommittedEntries));
        return result;
    }
    internal static async Task VerifyCopyAsync(OrganizationPlan plan)
    {
        var result = await new OrganizationService().ExecuteAsync(plan, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(OrganizationState.Completed, result.State); Assert.Null(result.CleanupWarning); Assert.Null(result.Error);
        Assert.Equal(plan.OutputDirectory, result.OutputDirectory); Assert.Equal(plan.Mappings.Count, result.Entries.Count);
        foreach (var mapping in plan.Mappings.Where(m => !m.IsDirectory))
        {
            var target = Path.Combine(result.OutputDirectory!, mapping.TargetRelativePath.Replace('/', Path.DirectorySeparatorChar));
            Assert.Equal(File.ReadAllBytes(mapping.SourcePath), File.ReadAllBytes(target));
            Assert.Equal(mapping.Sha256, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(target))));
        }
    }
}

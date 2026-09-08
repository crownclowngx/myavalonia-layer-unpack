using System.Security.Cryptography;
using System.Text.Json;
using ICSharpCode.SharpZipLib.Zip;
using LayerUnpackPlugin.Headless.Application;
using LayerUnpackPlugin.Headless.Contracts;
using LayerUnpackPlugin.Headless.Infrastructure;
using Xunit;

namespace LayerUnpackPlugin.Headless.Tests;

/// <summary>批次测试真实检查包内来源映射；仅调度和故障时机使用写入端替身，仍委托真实 ZIP 写入。</summary>
public sealed class PackBatchTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static string Source(TestWorkspace w, string name, byte[]? data = null) => PackTests.Source(w, name, data);
    private static Task<PackBatchPlan> Prepare(TestWorkspace w, IEnumerable<string> inputs, PackOptions? options = null, PackGrouping grouping = PackGrouping.Separate, PackLimits? limits = null)
        => new PackBatchService().PrepareAsync(new(inputs, w.Output, grouping: grouping, options: options, limits: limits), cancellationToken: Token);

    [Fact]
    public async Task 十个文件夹各自产出一个包并保留所有后代与中文路径()
    {
        using var w = new TestWorkspace();
        var roots = Enumerable.Range(0, 10).Select(i =>
        {
            Source(w, $"资料{i:D2}/子目录/中文.txt", BitConverter.GetBytes(i));
            return w.FilePath($"资料{i:D2}");
        }).ToArray();
        var plan = await Prepare(w, roots);
        Assert.Equal(10, plan.Groups.Count); Assert.False(Directory.Exists(w.Output));
        await using var session = new PackBatchService().CreateSession(plan);
        var result = await session.ExecuteAsync(cancellationToken: Token);
        Assert.Equal(PackBatchState.Completed, result.State); Assert.Equal(10, result.CompletedCount);
        foreach (var group in result.Groups)
        {
            Assert.Equal(group.PlannedOutputPath, group.Result.OutputPath);
            using var zip = new ZipFile(File.OpenRead(group.Result.OutputPath!));
            var file = Assert.Single(zip.Cast<ZipEntry>(), e => !e.IsDirectory);
            Assert.Equal($"资料{group.Index:D2}/子目录/中文.txt", file.Name);
            using var content = zip.GetInputStream(file);
            Assert.Equal(SHA256.HashData(BitConverter.GetBytes(group.Index)), SHA256.HashData(content));
        }
    }

    [Fact]
    public async Task 切换合并与分别保持父子去重收录规则且顺序稳定()
    {
        using var w = new TestWorkspace(); var child = Source(w, "a/子/item.txt"); var other = Source(w, "b/item.txt");
        var inputs = new[] { child, w.FilePath("a"), other, child };
        var separate = await Prepare(w, inputs);
        var combined = await Prepare(w, inputs, grouping: PackGrouping.Combined);
        Assert.Equal(2, separate.Groups.Count); Assert.Single(combined.Groups); Assert.Equal(2, separate.MergedInputs);
        Assert.Equal(combined.Groups[0].Plan!.Entries.Select(e => e.SourcePath).Order(), separate.Groups.SelectMany(g => g.Plan!.Entries).Select(e => e.SourcePath).Order());
        var reversed = await Prepare(w, inputs.Reverse());
        Assert.Equal(separate.Groups.Select(g => g.OutputPath), reversed.Groups.Select(g => g.OutputPath));
        await using var session = new PackBatchService().CreateSession(combined);
        Assert.Equal(1, (await session.ExecuteAsync(cancellationToken: Token)).CompletedCount);
    }

    [Fact]
    public async Task 文件和文件夹同名目标避开现有文件并在提交竞态时继续编号()
    {
        using var w = new TestWorkspace();
        var a = Source(w, "one/资料.txt"); var b = Source(w, "two/资料.md"); Source(w, "three/资料/content.txt");
        Source(w, "output/资料.zip", [7]);
        var plan = await Prepare(w, [a, b, w.FilePath("three/资料")]);
        Assert.Equal(3, plan.Groups.Select(g => g.OutputPath).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.DoesNotContain(plan.Groups, g => g.OutputPath == w.FilePath("output/资料.zip"));
        File.WriteAllBytes(plan.Groups[0].OutputPath, [9]);
        await using var session = new PackBatchService().CreateSession(plan);
        var result = await session.ExecuteAsync(cancellationToken: Token);
        Assert.Equal(3, result.CompletedCount);
        Assert.NotEqual(plan.Groups[0].OutputPath, result.Groups[0].Result.OutputPath);
        Assert.Equal(new byte[] { 9 }, File.ReadAllBytes(plan.Groups[0].OutputPath));
        Assert.Equal(new byte[] { 7 }, File.ReadAllBytes(w.FilePath("output/资料.zip")));
    }

    [Fact]
    public async Task 排除规则按完整名称匹配且目录向后代传播并保留原有空目录()
    {
        using var w = new TestWorkspace();
        Source(w, "src/file.TMP"); Source(w, "src/keep.tmp.txt"); Source(w, "src/.hidden");
        Source(w, "src/nested/OBJ/a.txt"); Source(w, "src/obj2/b.txt"); Directory.CreateDirectory(w.FilePath("src/empty"));
        var plan = await Prepare(w, [w.FilePath("src")], new() { Exclusions = new([".tmp"], ["obj"]) });
        Assert.Equal(2, plan.ExcludedCount);
        var group = plan.Groups[0].Plan!;
        Assert.Contains(group.ExcludedItems, e => e.IsDirectory && e.Reason.Contains("全部后代"));
        Assert.Equal(new[] { "src/.hidden", "src/empty", "src/keep.tmp.txt", "src/obj2/b.txt" }, group.Entries.Where(e => !e.IsDirectory || e.EntryName == "src/empty").Select(e => e.EntryName).Order());
        Assert.DoesNotContain(group.Entries, e => e.EntryName == "src/nested");
        await using var session = new PackBatchService().CreateSession(plan);
        var result = await session.ExecuteAsync(cancellationToken: Token);
        using var zip = new ZipFile(File.OpenRead(result.Groups[0].Result.OutputPath!));
        Assert.True(zip.TestArchive(true)); Assert.NotNull(zip.GetEntry("src/empty/")); Assert.Null(zip.GetEntry("src/file.TMP"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 被规则全部排空的组跳过而成功组继续(bool excludeRoot)
    {
        using var w = new TestWorkspace(); Source(w, "empty/sub/a.log"); var valid = Source(w, "keep.txt");
        var options = new PackOptions { Exclusions = excludeRoot ? new(directoryNames: ["empty"]) : new([".log"]) };
        var plan = await Prepare(w, [w.FilePath("empty"), valid], options);
        Assert.Empty(plan.Groups[0].Plan!.Entries);
        await using var session = new PackBatchService().CreateSession(plan);
        var result = await session.ExecuteAsync(cancellationToken: Token);
        Assert.Equal(1, result.CompletedCount); Assert.Equal(1, result.SkippedCount);
        Assert.False(result.CanRetry); Assert.Single(Directory.GetFiles(w.Output, "*.zip"));
    }

    [Fact]
    public async Task 默认不排除看似临时或隐藏的文件且只有空目录也能打包()
    {
        using var w = new TestWorkspace(); Source(w, "src/a.tmp"); Source(w, "src/.git/config");
        var empty = Directory.CreateDirectory(w.FilePath("empty")).FullName;
        var plan = await Prepare(w, [w.FilePath("src"), empty]);
        Assert.Equal(0, plan.ExcludedCount); Assert.Equal(2, plan.FileCount);
        await using var session = new PackBatchService().CreateSession(plan);
        Assert.Equal(2, (await session.ExecuteAsync(cancellationToken: Token)).CompletedCount);
    }

    [Fact]
    public async Task 准备时来源不可用只使该组失败且没有完整快照不能原地重试()
    {
        using var w = new TestWorkspace(); var good = Source(w, "good.txt");
        var plan = await Prepare(w, [good, w.FilePath("missing.txt")]);
        Assert.Equal(PackError.InputUnavailable, plan.Groups[1].PreparationError?.Code);
        await using var session = new PackBatchService().CreateSession(plan);
        var result = await session.ExecuteAsync(cancellationToken: Token);
        Assert.Equal(PackBatchState.PartiallyCompleted, result.State); Assert.Equal(1, result.CompletedCount); Assert.Equal(1, result.FailedCount);
        Assert.False(result.CanRetry);
    }

    [Fact]
    public async Task 读取失败修复后仅重试失败组且所有成功路径保持原样()
    {
        using var w = new TestWorkspace(); var a = Source(w, "a.txt"); var b = Source(w, "b.txt");
        var plan = await Prepare(w, [a, b]);
        await using var session = new PackBatchService().CreateSession(plan);
        PackBatchResult first;
        using (var locked = new FileStream(a, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            first = await session.ExecuteAsync(cancellationToken: Token);
        Assert.True(first.Groups[0].CanRetry); Assert.Equal(1, first.CompletedCount);
        var successPath = first.Groups[1].Result.OutputPath;
        var second = await session.RetryFailedAsync(cancellationToken: Token);
        Assert.Equal(2, second.CompletedCount); Assert.False(second.CanRetry);
        Assert.Equal(successPath, second.Groups[1].Result.OutputPath);
        await session.RetryFailedAsync(cancellationToken: Token);
        Assert.Equal(2, Directory.GetFiles(w.Output, "*.zip").Length);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.ExecuteAsync(cancellationToken: Token));
    }

    [Fact]
    public async Task 重试前源内容变化即使时间长度不变也拒绝提交且停止允许重试()
    {
        using var w = new TestWorkspace(); var a = Source(w, "a.txt", [1, 2]);
        var plan = await Prepare(w, [a]);
        await using var session = new PackBatchService().CreateSession(plan);
        using (var locked = new FileStream(a, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.True((await session.ExecuteAsync(cancellationToken: Token)).CanRetry);
        var stamp = File.GetLastWriteTimeUtc(a); File.WriteAllBytes(a, [2, 1]); File.SetLastWriteTimeUtc(a, stamp);
        var result = await session.RetryFailedAsync(cancellationToken: Token);
        Assert.Equal(PackError.InputChanged, result.Groups[0].Result.Error?.Code); Assert.False(result.CanRetry);
        Assert.Empty(Directory.GetFiles(w.Output));
    }

    [Fact]
    public async Task 取消当前组和后续组保留先前成功包并且取消项不参与失败重试()
    {
        using var w = new TestWorkspace(); var plan = await Prepare(w, [Source(w, "a.txt"), Source(w, "b.txt"), Source(w, "c.txt")]);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Token);
        await using var session = new PackBatchService().CreateSession(plan);
        var result = await session.ExecuteAsync(new ProgressRelay(p => { if (p.GroupIndex == 1 && p.Progress.State == PackState.Writing) cancel.Cancel(); }), cancel.Token);
        Assert.Equal(PackBatchState.Cancelled, result.State); Assert.Equal(1, result.CompletedCount); Assert.Equal(2, result.CancelledCount);
        Assert.False(result.CanRetry); var success = result.Groups[0].Result.OutputPath;
        var retried = await session.RetryFailedAsync(cancellationToken: Token);
        Assert.Equal(success, retried.Groups[0].Result.OutputPath); Assert.Single(Directory.GetFiles(w.Output));
    }

    [Fact]
    public async Task 会话互斥及关闭等待真实写入退出并清理临时文件()
    {
        using var w = new TestWorkspace(); var plan = await Prepare(w, [Source(w, "a.txt")]);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var exited = false;
        var service = new PackBatchService(new PackService(new(), new Writer(async (_, output, _, token, _) =>
        {
            await output.WriteAsync(new byte[32], token); entered.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, token); } finally { exited = true; }
        })));
        var session = service.CreateSession(plan);
        var active = session.ExecuteAsync(cancellationToken: Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.RetryFailedAsync(cancellationToken: Token));
        await session.DisposeAsync(); await session.DisposeAsync();
        Assert.True(exited); Assert.Equal(PackBatchState.Cancelled, (await active).State); Assert.Empty(Directory.GetFiles(w.Output));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => session.RetryFailedAsync(cancellationToken: Token));
    }

    [Fact]
    public async Task 输出故障可以重试且错误结果不暴露底层异常文本()
    {
        using var w = new TestWorkspace(); var plan = await Prepare(w, [Source(w, "a.txt"), Source(w, "b.txt")]);
        var fail = true;
        var service = new PackBatchService(new PackService(new(), new Writer((p, output, progress, token, secret) =>
        {
            if (fail) throw new IOException("private-sentinel");
            return new ZipArchiveWriter().WriteAsync(p, output, progress, token, secret);
        })));
        await using var session = service.CreateSession(plan);
        var first = await session.ExecuteAsync(cancellationToken: Token);
        Assert.All(first.Groups, g => Assert.True(g.CanRetry)); Assert.DoesNotContain("private-sentinel", JsonSerializer.Serialize(first));
        fail = false;
        Assert.Equal(2, (await session.RetryFailedAsync(cancellationToken: Token)).CompletedCount);
    }

    [Theory]
    [InlineData("*.tmp", true)]
    [InlineData("tmp", true)]
    [InlineData(".tar.gz", true)]
    [InlineData("../obj", false)]
    [InlineData("obj/", false)]
    [InlineData(" obj", false)]
    public void 排除规则拒绝不明确表达式(string value, bool extension)
        => Assert.Throws<PackValidationException>(() => extension ? new PackExclusionRules([value]) : new PackExclusionRules(directoryNames: [value]));

    [Fact]
    public async Task 输入规则和结果集合复制冻结且分别输出不能进入任一来源树()
    {
        using var w = new TestWorkspace(); var a = Source(w, "src/a.txt"); var inputs = new List<string> { w.FilePath("src") }; var extensions = new List<string> { ".log" };
        var request = new PackBatchRequest(inputs, w.Output, grouping: PackGrouping.Separate, options: new() { Exclusions = new(extensions) });
        inputs.Clear(); extensions.Add(".txt");
        var plan = await new PackBatchService().PrepareAsync(request, cancellationToken: Token);
        Assert.Equal(1, plan.FileCount); Assert.Throws<NotSupportedException>(() => ((IList<PackGroupPlan>)plan.Groups).Clear());
        await Assert.ThrowsAsync<PackValidationException>(() => new PackBatchService().PrepareAsync(new([w.FilePath("src")], w.FilePath("src"), grouping: PackGrouping.Separate), cancellationToken: Token));
        Assert.True(File.Exists(a));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task 批次累计来源字节与清单条目有总上限(bool bytes)
    {
        using var w = new TestWorkspace();
        var inputs = new[] { Source(w, "a.txt", new byte[30]), Source(w, "b.txt", new byte[30]) };
        var limits = bytes ? new PackLimits { MaxTotalBytes = 50, MaxFileBytes = 50 } : new PackLimits { MaxEntries = 1 };
        var failure = await Assert.ThrowsAsync<PackFailureException>(() => Prepare(w, inputs, limits: limits));
        Assert.Equal(PackError.BudgetExceeded, failure.Code); Assert.False(Directory.Exists(w.Output));
    }

    [Fact]
    public async Task 整批全部排除只计跳过并且准备中取消不写出任何目标()
    {
        using var w = new TestWorkspace(); var a = Source(w, "a.log"); var b = Source(w, "b.log");
        var plan = await Prepare(w, [a, b], new() { Exclusions = new([".log"]) });
        await using var session = new PackBatchService().CreateSession(plan);
        var result = await session.ExecuteAsync(cancellationToken: Token);
        Assert.Equal(PackBatchState.Skipped, result.State); Assert.Equal(2, result.SkippedCount); Assert.Equal(0, result.CompletedCount);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new PackBatchService().PrepareAsync(new([a, b], w.Output, grouping: PackGrouping.Separate),
            new ProgressRelay(_ => cancel.Cancel()), cancel.Token));
        Assert.False(Directory.Exists(w.Output));
    }

    [Fact]
    public async Task 组超时后继续兄弟组且恢复时只重试该失败组()
    {
        using var w = new TestWorkspace(); var a = Source(w, "a.txt"); var b = Source(w, "b.txt");
        var plan = await Prepare(w, [a, b], limits: new() { Timeout = TimeSpan.FromMilliseconds(200) });
        var first = true;
        var service = new PackBatchService(new PackService(new(), new Writer(async (p, output, progress, token, secret) =>
        {
            if (first) { first = false; await Task.Delay(Timeout.Infinite, token); }
            await new ZipArchiveWriter().WriteAsync(p, output, progress, token, secret);
        })));
        await using var session = service.CreateSession(plan);
        var failed = await session.ExecuteAsync(cancellationToken: Token);
        Assert.Equal(PackError.Timeout, failed.Groups[0].Result.Error?.Code); Assert.True(failed.Groups[0].CanRetry); Assert.Equal(1, failed.CompletedCount);
        var recovered = await session.RetryFailedAsync(cancellationToken: Token);
        Assert.Equal(2, recovered.CompletedCount); Assert.Equal(failed.Groups[1].Result.OutputPath, recovered.Groups[1].Result.OutputPath);
    }

    [Fact]
    public async Task 批次准备总超时与非法模式明确拒绝()
    {
        using var w = new TestWorkspace(); var a = Source(w, "a.txt");
        var service = new PackBatchService(new SlowPreparation());
        var timeout = await Assert.ThrowsAsync<PackFailureException>(() => service.PrepareAsync(new([a], w.Output,
            limits: new() { Timeout = TimeSpan.FromMilliseconds(50) }), cancellationToken: Token));
        Assert.Equal(PackError.Timeout, timeout.Code);
        await Assert.ThrowsAsync<PackValidationException>(() => new PackBatchService().PrepareAsync(new([a], w.Output, grouping: (PackGrouping)99), cancellationToken: Token));
        await Assert.ThrowsAsync<PackValidationException>(() => Prepare(w, [a], new() { Compression = (PackCompression)99 }));
        Assert.False(Directory.Exists(w.Output));
    }

    private sealed class SlowPreparation : IPackService
    {
        public async Task<PackPlan> PrepareAsync(PackRequest request, IProgress<PackProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("仅用于验证批次总超时。");
        }
        public Task<PackResult> ExecuteAsync(PackPlan plan, IProgress<PackProgress>? progress = null, CancellationToken cancellationToken = default, PackSecret? secret = null)
            => throw new InvalidOperationException("准备未完成时不得执行。");
    }

    private sealed class ProgressRelay(Action<PackBatchProgress> action) : IProgress<PackBatchProgress>
    { public void Report(PackBatchProgress value) => action(value); }
    private sealed class Writer(Func<PackPlan, Stream, IProgress<PackProgress>?, CancellationToken, PackSecret?, Task> action) : IArchiveWriter
    { public Task WriteAsync(PackPlan plan, Stream output, IProgress<PackProgress>? progress, CancellationToken cancellationToken, PackSecret? secret = null) => action(plan, output, progress, cancellationToken, secret); }
}

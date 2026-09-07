using System.Diagnostics;
using System.Security.Cryptography;
using ICSharpCode.SharpZipLib.Zip;
using LayerUnpackPlugin.Headless.Application;
using LayerUnpackPlugin.Headless.Contracts;
using LayerUnpackPlugin.Headless.Infrastructure;
using Xunit;

namespace LayerUnpackPlugin.Headless.Tests;

/// <summary>创建格式用真实 ZIP 和另一套读取器验证；故障替身仅用于可控的提交前失败与超时。</summary>
public sealed class PackTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    internal static string Source(TestWorkspace w, string name, byte[]? data = null)
    {
        var path = w.FilePath(name); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, data ?? "中文资料内容"u8.ToArray()); return path;
    }
    private static async Task<PackPlan> Plan(TestWorkspace w, string[] inputs, PackLimits? limits = null, string? output = null)
        => await new PackService().PrepareAsync(new(inputs, output ?? Path.Combine(w.Output, "资料.zip"), limits), cancellationToken: Token);
    private static void CheckArchive(PackPlan plan, PackResult result)
    {
        Assert.Equal(PackState.Completed, result.State); Assert.Null(result.Error); Assert.Null(result.CleanupWarning);
        using var zip = new ZipFile(File.OpenRead(result.OutputPath!));
        Assert.True(zip.TestArchive(testData: true));
        var actual = zip.Cast<ZipEntry>().OrderBy(e => e.Name).ToArray();
        Assert.Equal(plan.Entries.Select(e => e.EntryName + (e.IsDirectory ? "/" : "")).Order(), actual.Select(e => e.Name));
        foreach (var item in plan.Entries.Where(e => !e.IsDirectory))
        {
            using var stream = zip.GetInputStream(zip.GetEntry(item.EntryName));
            Assert.Equal(item.Sha256, Convert.ToHexString(SHA256.HashData(stream)));
        }
        Assert.Equal(plan.TotalBytes, result.SourceBytes);
        Assert.Equal(new FileInfo(result.OutputPath!).Length, result.ArchiveBytes);
    }
    private static void AssertClean(TestWorkspace w)
    {
        Assert.Empty(Directory.EnumerateFiles(w.Root, "*.tmp", SearchOption.AllDirectories));
        if (Directory.Exists(w.Output)) Assert.Empty(Directory.GetFiles(w.Output));
    }

    [Fact]
    public async Task 中文空文件空目录结构通过独立读取器及原解压回读()
    {
        using var w = new TestWorkspace();
        var file = Source(w, "输入/子目录/中文.txt"); Source(w, "输入/空.txt", []);
        Directory.CreateDirectory(w.FilePath("输入/空目录"));
        var plan = await Plan(w, [w.FilePath("输入"), Source(w, "说明.txt")]);
        var result = await new PackService().ExecuteAsync(plan, cancellationToken: Token);
        CheckArchive(plan, result);
        await using var session = new UnpackService().CreateSession(new([result.OutputPath!], w.FilePath("readback")));
        var unpack = await session.ExecuteAsync(cancellationToken: Token);
        Assert.Equal(BatchState.Completed, unpack.State);
        Assert.Equal(File.ReadAllBytes(file), File.ReadAllBytes(Path.Combine(unpack.Nodes[0].OutputDirectory!, "输入/子目录/中文.txt")));
        Assert.True(Directory.Exists(Path.Combine(unpack.Nodes[0].OutputDirectory!, "输入/空目录")));
    }
    [Fact]
    public async Task 父子输入重复合并且同名根稳定编号()
    {
        using var w = new TestWorkspace();
        var child = Source(w, "one/资料/child.txt"); Source(w, "two/资料/child.txt", [2]);
        var first = Source(w, "one/same.txt", [3]); var second = Source(w, "two/same.txt", [4]);
        var paths = new[] { child, w.FilePath("two/资料"), second, w.FilePath("one/资料"), first, child };
        var plan = await Plan(w, paths);
        Assert.Equal(2, plan.MergedInputs);
        Assert.Equal(4, plan.Roots.Count);
        Assert.Equal(4, plan.Roots.Select(r => r.EntryName).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        var reversed = await Plan(w, paths.Reverse().ToArray());
        Assert.Equal(plan.Roots, reversed.Roots);
        CheckArchive(plan, await new PackService().ExecuteAsync(plan, cancellationToken: Token));
    }
    [Fact]
    public async Task 输出在源目录内排除既有目标与本次临时文件()
    {
        using var w = new TestWorkspace();
        Source(w, "input/content.txt"); var existing = Source(w, "input/结果.zip", [1, 2, 3]);
        var plan = await Plan(w, [w.FilePath("input")], output: existing);
        Assert.Equal(1, plan.ExcludedOutputs);
        var result = await new PackService().ExecuteAsync(plan, cancellationToken: Token);
        CheckArchive(plan, result);
        Assert.EndsWith("结果 (1).zip", result.OutputPath);
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(existing));
        Assert.DoesNotContain(plan.Entries, e => e.EntryName.EndsWith(".zip") || e.EntryName.EndsWith(".tmp"));
    }
    [Fact]
    public async Task 外部同名输出并发提交各自成功且不覆盖()
    {
        using var w = new TestWorkspace(); var plan = await Plan(w, [Source(w, "a.txt")]);
        Directory.CreateDirectory(w.Output); File.WriteAllBytes(plan.Request.OutputPath, [9]);
        var service = new PackService();
        var results = await Task.WhenAll(service.ExecuteAsync(plan, cancellationToken: Token), service.ExecuteAsync(plan, cancellationToken: Token));
        foreach (var result in results) CheckArchive(plan, result);
        Assert.NotEqual(results[0].OutputPath, results[1].OutputPath);
        Assert.Equal(new byte[] { 9 }, File.ReadAllBytes(plan.Request.OutputPath));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 准备后改写内容即使保留长度时间也不能提交(bool preserveMetadata)
    {
        using var w = new TestWorkspace(); var source = Source(w, "a.txt", [1, 2, 3]);
        var plan = await Plan(w, [source]); var stamp = File.GetLastWriteTimeUtc(source);
        File.WriteAllBytes(source, preserveMetadata ? [3, 2, 1] : [7]);
        if (preserveMetadata) File.SetLastWriteTimeUtc(source, stamp);
        var result = await new PackService().ExecuteAsync(plan, cancellationToken: Token);
        Assert.Equal(PackError.InputChanged, result.Error?.Code); AssertClean(w);
    }
    [Theory]
    [InlineData("add")]
    [InlineData("remove")]
    public async Task 准备后的源树改变被拒绝(string mutation)
    {
        using var w = new TestWorkspace(); var source = Source(w, "input/a.txt");
        var plan = await Plan(w, [w.FilePath("input")]);
        if (mutation == "add") Source(w, "input/b.txt"); else File.Delete(source);
        var result = await new PackService().ExecuteAsync(plan, cancellationToken: Token);
        Assert.Equal(PackState.Failed, result.State); AssertClean(w);
    }
    [Fact]
    public async Task 真实写入中取消清理临时ZIP并保留来源()
    {
        using var w = new TestWorkspace(); var source = Source(w, "large.bin", RandomNumberGenerator.GetBytes(4 * 1024 * 1024));
        var plan = await Plan(w, [source]); using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var hadTemporaryFile = false;
        var progress = new InlineProgress(p =>
        {
            if (p.State != PackState.Writing || p.ReadBytes == 0) return;
            hadTemporaryFile = Directory.EnumerateFiles(w.Output, "*.tmp").Any(); cancel.Cancel();
        });
        var result = await new PackService().ExecuteAsync(plan, progress, cancel.Token);
        Assert.True(hadTemporaryFile); Assert.Equal(PackState.Cancelled, result.State); Assert.Null(result.Error);
        Assert.True(File.Exists(source)); AssertClean(w);
    }
    [Fact]
    public async Task 归档中央目录收尾取消也不提交()
    {
        using var w = new TestWorkspace(); var plan = await Plan(w, [Source(w, "a.txt")]);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var result = await new PackService().ExecuteAsync(plan, new InlineProgress(p => { if (p.State == PackState.Finalizing) cancel.Cancel(); }), cancel.Token);
        Assert.Equal(PackState.Cancelled, result.State); AssertClean(w);
    }
    [Fact]
    public async Task 准备取消不创建任何输出()
    {
        using var w = new TestWorkspace(); var source = Source(w, "a.bin", new byte[512 * 1024]);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new PackService().PrepareAsync(new([source], Path.Combine(w.Output, "a.zip")), new InlineProgress(_ => cancel.Cancel()), cancel.Token));
        Assert.False(Directory.Exists(w.Output));
    }
    [Theory]
    [InlineData("file")]
    [InlineData("total")]
    [InlineData("entries")]
    [InlineData("depth")]
    public async Task 输入预算限制在落盘前执行(string kind)
    {
        using var w = new TestWorkspace(); Source(w, "src/sub/deep/a.txt", new byte[50]); Source(w, "src/sub/b.txt", new byte[50]);
        var limits = kind switch
        {
            "file" => new PackLimits { MaxFileBytes = 49 },
            "total" => new PackLimits { MaxTotalBytes = 90, MaxFileBytes = 90 },
            "entries" => new PackLimits { MaxEntries = 2 },
            _ => new PackLimits { MaxDirectoryDepth = 1 }
        };
        var error = await Assert.ThrowsAsync<PackFailureException>(() => Plan(w, [w.FilePath("src")], limits));
        Assert.Equal(PackError.BudgetExceeded, error.Code); Assert.False(Directory.Exists(w.Output));
    }
    [Fact]
    public async Task 归档输出预算包含头部与收尾()
    {
        using var w = new TestWorkspace(); var plan = await Plan(w, [Source(w, "empty.txt", [])], new() { MaxArchiveBytes = 32 });
        var result = await new PackService().ExecuteAsync(plan, cancellationToken: Token);
        Assert.Equal(PackError.BudgetExceeded, result.Error?.Code); AssertClean(w);
    }
    [Fact]
    public async Task 写入故障回滚且输出目录不可用返回明确错误()
    {
        using var w = new TestWorkspace(); var plan = await Plan(w, [Source(w, "a.txt")]);
        var service = new PackService(new(), new Writer(async (_, output, _, token) => { await output.WriteAsync(new byte[32], token); throw new IOException("test"); }));
        Assert.Equal(PackError.OutputError, (await service.ExecuteAsync(plan, cancellationToken: Token)).Error?.Code); AssertClean(w);
        var blocked = Source(w, "blocked", [0]);
        var invalidTarget = await Plan(w, [w.FilePath("a.txt")], output: Path.Combine(blocked, "a.zip"));
        Assert.Equal(PackError.OutputError, (await new PackService().ExecuteAsync(invalidTarget, cancellationToken: Token)).Error?.Code);
    }
    [Fact]
    public async Task 超时和外部取消分别呈现()
    {
        using var w = new TestWorkspace(); var plan = await Plan(w, [Source(w, "a.txt")], new() { Timeout = TimeSpan.FromMilliseconds(100) });
        var service = new PackService(new(), new Writer(async (_, _, _, token) => await Task.Delay(Timeout.Infinite, token)));
        var result = await service.ExecuteAsync(plan, cancellationToken: Token);
        Assert.Equal(PackState.Failed, result.State); Assert.Equal(PackError.Timeout, result.Error?.Code); AssertClean(w);
    }
    [Theory]
    [InlineData("empty")]
    [InlineData("extension")]
    [InlineData("self")]
    [InlineData("new-subfolder")]
    public async Task 非法请求与不明确的源内新目录在准备时拒绝(string kind)
    {
        using var w = new TestWorkspace(); var source = Source(w, "src/source.zip", [1]);
        var output = kind switch { "extension" => w.FilePath("bad.rar"), "self" => source, "new-subfolder" => w.FilePath("src/new/out.zip"), _ => w.FilePath("a.zip") };
        var inputs = kind == "empty" ? Array.Empty<string>() : kind == "new-subfolder" ? [w.FilePath("src")] : new[] { source };
        await Assert.ThrowsAsync<PackValidationException>(() => Plan(w, inputs, output: output));
    }
    [Fact]
    public async Task 两千小文件与长路径保持内容且记录性能()
    {
        using var w = new TestWorkspace();
        for (var i = 0; i < 2000; i++) Source(w, $"src/{i:D4}.txt", BitConverter.GetBytes(i));
        Source(w, "src/" + string.Join('/', Enumerable.Repeat(new string('长', 40), 6)) + "/结尾.txt", []);
        var watch = Stopwatch.StartNew(); var plan = await Plan(w, [w.FilePath("src")]);
        var result = await new PackService().ExecuteAsync(plan, cancellationToken: Token); CheckArchive(plan, result);
        var evidence = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/performance/G0008"));
        Directory.CreateDirectory(evidence);
        await File.WriteAllTextAsync(Path.Combine(evidence, "small-files.json"), System.Text.Json.JsonSerializer.Serialize(new { files = plan.FileCount, bytes = plan.TotalBytes, result.ArchiveBytes, elapsedMs = watch.ElapsedMilliseconds }), Token);
    }
    [Fact]
    public async Task 零字节归档统计不要求压缩后更小()
    {
        using var w = new TestWorkspace(); var plan = await Plan(w, [Source(w, "empty.txt", [])]);
        var result = await new PackService().ExecuteAsync(plan, cancellationToken: Token);
        CheckArchive(plan, result); Assert.Equal(0, result.SourceBytes); Assert.True(result.ArchiveBytes > 0);
    }

    [Fact]
    public async Task 源文件独占导致读取失败且空目录可独立打包()
    {
        using var w = new TestWorkspace(); var source = Source(w, "source.txt");
        var plan = await Plan(w, [source]);
        using (var locked = new FileStream(source, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var result = await new PackService().ExecuteAsync(plan, cancellationToken: Token);
            Assert.Equal(PackError.InputUnavailable, result.Error?.Code); AssertClean(w);
        }
        var directory = Directory.CreateDirectory(w.FilePath("empty-folder")).FullName;
        var emptyPlan = await Plan(w, [directory]);
        CheckArchive(emptyPlan, await new PackService().ExecuteAsync(emptyPlan, cancellationToken: Token));
        Assert.Equal(0, emptyPlan.FileCount);
    }
    [Fact]
    public async Task 收尾时源树新增内容使清单失效并回滚()
    {
        using var w = new TestWorkspace(); Source(w, "src/a.txt"); var plan = await Plan(w, [w.FilePath("src")]);
        var progress = new InlineProgress(p => { if (p.State == PackState.Finalizing) Source(w, "src/added.txt"); });
        var result = await new PackService().ExecuteAsync(plan, progress, Token);
        Assert.Equal(PackError.InputChanged, result.Error?.Code); AssertClean(w);
    }
    [Fact]
    public async Task 源目录中的链接拒绝遍历()
    {
        using var w = new TestWorkspace(); Source(w, "src/a.txt"); Source(w, "other/outside.txt");
        var link = w.FilePath("src/link"); var target = w.FilePath("other");
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var info = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                // cmd 的 /c 后需要命令文本；不能把整段命令作为经 ArgumentList 转义的一枚参数。
                info.Arguments = $"/d /c mklink /J \"{link}\" \"{target}\"";
                using var process = Process.Start(info)!; await process.WaitForExitAsync(Token);
                Assert.True(process.ExitCode == 0, await process.StandardError.ReadToEndAsync(Token));
            }
            else Directory.CreateSymbolicLink(link, target);
            var error = await Assert.ThrowsAsync<PackFailureException>(() => Plan(w, [w.FilePath("src")]));
            Assert.Equal(PackError.UnsafePath, error.Code); Assert.False(Directory.Exists(w.Output));
        }
        finally { if (Directory.Exists(link)) Directory.Delete(link); }
    }

    private sealed class InlineProgress(Action<PackProgress> action) : IProgress<PackProgress>
    { public void Report(PackProgress value) => action(value); }
    private sealed class Writer(Func<PackPlan, Stream, IProgress<PackProgress>?, CancellationToken, Task> action) : IArchiveWriter
    { public Task WriteAsync(PackPlan plan, Stream output, IProgress<PackProgress>? progress, CancellationToken cancellationToken) => action(plan, output, progress, cancellationToken); }
}

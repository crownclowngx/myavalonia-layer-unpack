using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using LayerUnpackPlugin.Headless.Application;
using LayerUnpackPlugin.Headless.Contracts;
using Xunit;

namespace LayerUnpackPlugin.Headless.Tests;

[CollectionDefinition("Performance", DisableParallelization = true)]
public sealed class PerformanceCollection;

/// <summary>有限规模的本地基线；记录真实工作量，不以测试进程峰值推导任意归档的内存保证。</summary>
[Collection("Performance")]
public sealed class PerformanceTests
{
    [Fact]
    public async Task 六十四MiB单文件按流解压且内容摘要一致()
    {
        using var w = new TestWorkspace();
        var (input, digest) = CreateLargeZip(w, 64);
        await using var session = new UnpackService().CreateSession(new([input], w.Output));
        var allocated = GC.GetTotalAllocatedBytes();
        var watch = Stopwatch.StartNew();
        var result = await session.ExecuteAsync(cancellationToken: TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), cancellationToken: TestContext.Current.CancellationToken);
        watch.Stop();
        Assert.Equal(BatchState.Completed, result.State);
        Assert.Equal(64L * 1024 * 1024, result.TotalWrittenBytes);
        await using var content = File.OpenRead(Path.Combine(result.Nodes[0].OutputDirectory!, "large.bin"));
        Assert.Equal(digest, await SHA256.HashDataAsync(content, cancellationToken: TestContext.Current.CancellationToken));
        WriteMetric("large-file", new
        {
            inputBytes = new FileInfo(input).Length,
            result.TotalWrittenBytes,
            milliseconds = watch.Elapsed.TotalMilliseconds,
            allocatedBytes = GC.GetTotalAllocatedBytes() - allocated,
            processPeakWorkingSet = Process.GetCurrentProcess().PeakWorkingSet64
        });
    }

    [Fact]
    public async Task 两千小文件与连续十批次保持准确计数和资源释放()
    {
        using var w = new TestWorkspace();
        var input = w.Zip("many.zip", Enumerable.Range(0, 2000).Select(i => ($"folder/{i:D4}.txt", new byte[1024])).ToArray());
        var watch = Stopwatch.StartNew();
        await using (var session = new UnpackService().CreateSession(new([input], w.Output)))
        {
            var result = await session.ExecuteAsync(cancellationToken: TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(BatchState.Completed, result.State);
            Assert.Equal(2000 * 1024L, result.TotalWrittenBytes);
            Assert.Equal(2000, Directory.GetFiles(result.Nodes[0].OutputDirectory!, "*", SearchOption.AllDirectories).Length);
        }
        watch.Stop();
        WriteMetric("many-files", new
        {
            files = 2000,
            bytes = 2000 * 1024L,
            milliseconds = watch.Elapsed.TotalMilliseconds,
            processPeakWorkingSet = Process.GetCurrentProcess().PeakWorkingSet64
        });
        var small = w.Zip("small.zip", ("a", [1]));
        for (var i = 0; i < 10; i++)
        {
            await using var session = new UnpackService().CreateSession(new([small], w.Output));
            Assert.Equal(BatchState.Completed, (await session.ExecuteAsync(cancellationToken: TestContext.Current.CancellationToken)).State);
        }
        Assert.Equal(11, Directory.GetDirectories(w.Output).Length);
        Assert.Empty(Directory.GetDirectories(w.Output, ".layer-unpack-*"));
        // 所有源读取句柄已经释放，源文件现在可以独占打开。
        using var exclusive = new FileStream(input, FileMode.Open, FileAccess.Read, FileShare.None);
        Assert.True(exclusive.CanRead);
    }

    [Fact]
    public async Task 真实大文件写入中取消迅速退出且不留下临时产物()
    {
        using var w = new TestWorkspace();
        var (input, _) = CreateLargeZip(w, 128);
        using var cancellation = new CancellationTokenSource();
        var latency = new Stopwatch();
        var progress = new InlineProgress(value =>
        {
            if (value.Snapshot.TotalWrittenBytes > 0 && value.Snapshot.State == BatchState.Running && !cancellation.IsCancellationRequested)
            { latency.Start(); cancellation.Cancel(); }
        });
        await using var session = new UnpackService().CreateSession(new([input], w.Output));
        var result = await session.ExecuteAsync(progress, cancellation.Token).WaitAsync(TimeSpan.FromSeconds(30), cancellationToken: TestContext.Current.CancellationToken);
        latency.Stop();
        Assert.Equal(BatchState.Cancelled, result.State);
        Assert.InRange(result.TotalWrittenBytes, 1, 128L * 1024 * 1024 - 1);
        Assert.True(latency.Elapsed < TimeSpan.FromSeconds(3));
        Assert.Empty(Directory.GetFileSystemEntries(w.Output));
        WriteMetric("cancellation", new
        {
            expandedBytes = 128L * 1024 * 1024,
            result.TotalWrittenBytes,
            cancellationMilliseconds = latency.Elapsed.TotalMilliseconds
        });
    }

    [Fact]
    public async Task 十六层上限可正常停止且预算记录所有实际展开量()
    {
        using var w = new TestWorkspace();
        var current = w.Zip("level-17.zip", ("leaf.txt", new byte[4096]));
        for (var depth = 16; depth >= 1; depth--)
            current = w.Zip($"level-{depth}.zip", ($"level-{depth + 1}.zip", File.ReadAllBytes(current)));
        var watch = Stopwatch.StartNew();
        await using var session = new UnpackService().CreateSession(new([current], w.Output, 16));
        var result = await session.ExecuteAsync(cancellationToken: TestContext.Current.CancellationToken);
        watch.Stop();
        Assert.Equal(16, result.Succeeded); Assert.Equal(1, result.StoppedByDepth);
        Assert.Equal(16, result.AttemptCount);
        Assert.Equal(BatchState.Completed, result.State);
        WriteMetric("depth-16", new
        {
            result.Succeeded,
            result.StoppedByDepth,
            result.TotalWrittenBytes,
            milliseconds = watch.Elapsed.TotalMilliseconds
        });
    }

    private static (string Path, byte[] Digest) CreateLargeZip(TestWorkspace w, int mebibytes)
    {
        var path = w.FilePath("large.zip");
        var buffer = new byte[65536];
        for (var i = 0; i < buffer.Length; i++) buffer[i] = (byte)(i % 251);
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        using (var output = zip.CreateEntry("large.bin", CompressionLevel.Fastest).Open())
            for (var i = 0; i < mebibytes * 16; i++) { output.Write(buffer); digest.AppendData(buffer); }
        return (path, digest.GetHashAndReset());
    }

    private static void WriteMetric(string name, object value)
    {
        var destination = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/performance"));
        Directory.CreateDirectory(destination);
        File.WriteAllText(Path.Combine(destination, name + ".json"), JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
    }

    private sealed class InlineProgress(Action<UnpackProgress> report) : IProgress<UnpackProgress>
    {
        public void Report(UnpackProgress value) => report(value);
    }
}

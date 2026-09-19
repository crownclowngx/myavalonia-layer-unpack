using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using LayerUnpackPlugin.Headless.Application;
using LayerUnpackPlugin.Headless.Contracts;
using LayerUnpackPlugin.Headless.Infrastructure;
using Xunit;

namespace LayerUnpackPlugin.Headless.Tests;

[CollectionDefinition("分卷资源测量", DisableParallelization = true)]
public sealed class SplitResourceCollection;

/// <summary>与其他测试隔离测量进程指标；这只是合成的 32 MiB 展开样本，不冒充用户 GiB 级文件结论。</summary>
[Collection("分卷资源测量")]
public sealed class SplitResourceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 代表性多卷与密集小文件记录资源及取消排空(bool cancelDuringWrite)
    {
        var token = TestContext.Current.CancellationToken;
        using var w = new TestWorkspace();
        var root = Path.Combine(AppContext.BaseDirectory, "Fixtures", "G0015", "performance");
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "manifest.json")));
        var parts = manifest.RootElement.GetProperty("volumes").EnumerateArray().Select(v =>
        {
            var path = w.FilePath(v.GetProperty("name").GetString()!); File.Copy(Path.Combine(root, Path.GetFileName(path)), path);
            Assert.Equal(v.GetProperty("sha256").GetString(), Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))); return path;
        }).ToArray();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        var watch = Stopwatch.StartNew(); double? cancelledAt = null; long written = 0;
        var extractor = new DelegatingExtractor(call => new ArchiveExtractor().ExtractAsync(call.Source, call.Destination, call.Password,
            call.Encoding, call.Budget, count =>
            {
                call.Progress(count); written += count;
                if (cancelDuringWrite && written >= 1024 * 1024 && cancelledAt is null)
                { cancelledAt = watch.Elapsed.TotalMilliseconds; cancellation.Cancel(); }
            }, call.Token));
        using var process = Process.GetCurrentProcess(); process.Refresh();
        var beforeMemory = process.PrivateMemorySize64; var peakMemory = beforeMemory;
        var beforeHandles = process.HandleCount; var peakHandles = beforeHandles;
        await using var session = new UnpackService(extractor).CreateSession(new(parts, w.Output));
        var work = Task.Run(() => session.ExecuteAsync(cancellationToken: cancellation.Token), token);
        while (!work.IsCompleted)
        {
            process.Refresh(); peakMemory = Math.Max(peakMemory, process.PrivateMemorySize64); peakHandles = Math.Max(peakHandles, process.HandleCount);
            await Task.Delay(5, token);
        }
        var result = await work; watch.Stop(); process.Refresh();
        foreach (var part in parts) { using var exclusive = File.Open(part, FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
        if (cancelDuringWrite)
        {
            Assert.Equal(BatchState.Cancelled, result.State); Assert.NotNull(cancelledAt);
            Assert.Empty(Directory.GetDirectories(w.Output)); Assert.True(watch.Elapsed.TotalMilliseconds - cancelledAt < 30000);
        }
        else
        {
            Assert.Equal(BatchState.Completed, result.State); var output = Assert.Single(result.Nodes).OutputDirectory!;
            foreach (var entry in manifest.RootElement.GetProperty("expected").EnumerateArray())
            {
                var path = Path.Combine(output, entry.GetProperty("path").GetString()!);
                if (entry.GetProperty("directory").GetBoolean()) Assert.True(Directory.Exists(path));
                else Assert.Equal(entry.GetProperty("sha256").GetString(), Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
            }
            Assert.Equal(257, Directory.GetFiles(output, "*", SearchOption.AllDirectories).Length);
        }
        var evidence = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/G0015/resources")); Directory.CreateDirectory(evidence);
        await File.WriteAllTextAsync(Path.Combine(evidence, $"load-cancel-{cancelDuringWrite}.json"), JsonSerializer.Serialize(new
        {
            date = DateTime.UtcNow,
            sourceVolumes = parts.Length,
            sourceBytes = parts.Sum(p => new FileInfo(p).Length),
            result.TotalWrittenBytes,
            milliseconds = watch.Elapsed.TotalMilliseconds,
            cancelledAt,
            cancellationDrainMilliseconds = cancelledAt is null ? (double?)null : watch.Elapsed.TotalMilliseconds - cancelledAt,
            beforeMemory,
            peakMemory,
            afterMemory = process.PrivateMemorySize64,
            beforeHandles,
            peakHandles,
            afterHandles = process.HandleCount,
            note = "进程私有内存/句柄每 5ms 采样，可能漏掉瞬时峰值；合成重复数据压缩率高，未覆盖 GiB 级随机内容"
        }, new JsonSerializerOptions { WriteIndented = true }), token);
    }
}

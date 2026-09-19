using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using LayerUnpackPlugin.Headless.Application;
using LayerUnpackPlugin.Headless.Contracts;
using LayerUnpackPlugin.Headless.Domain;
using LayerUnpackPlugin.Headless.Infrastructure;
using Xunit;

namespace LayerUnpackPlugin.Headless.Tests;

[Collection("Performance")]
public sealed class ArchiveCheckSafetyTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 大包或大量条目中取消可观察退出并完整清理(bool manyEntries)
    {
        using var w = new TestWorkspace(); var path = w.FilePath("large.zip");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            var buffer = new byte[65536];
            for (var i = 0; i < (manyEntries ? 2000 : 1); i++)
            {
                using var output = zip.CreateEntry($"{i}.bin", CompressionLevel.Fastest).Open();
                for (var j = 0; j < (manyEntries ? 1 : 1024); j++) output.Write(buffer);
            }
        }
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token); double? cancelledAt = null;
        var watch = Stopwatch.StartNew();
        var result = await new ArchiveCheckService().CheckAsync(new(path, temporaryDirectory: w.Output), new SyncProgress<ArchiveCheckProgress>(p =>
        {
            if (cancelledAt is null && (manyEntries ? p.Entries >= 30 : p.ExpandedBytes >= 1024 * 1024))
            { cancelledAt = watch.Elapsed.TotalMilliseconds; cancellation.Cancel(); }
        }), cancellation.Token).WaitAsync(TimeSpan.FromSeconds(30), Token);
        Assert.NotNull(cancelledAt); Assert.Equal(ArchiveCheckState.Cancelled, result.State); Assert.Null(result.CleanupWarning);
        Assert.Empty(result.Evidence); Assert.True(result.ExpandedBytes > 0); Assert.Empty(Directory.GetFileSystemEntries(w.Output));
        using var exclusive = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
        Metric(manyEntries ? "check-many-cancel" : "check-large-cancel", new { manyEntries, result.ExpandedBytes, result.Elapsed, cancellationMilliseconds = watch.Elapsed.TotalMilliseconds - cancelledAt, processPeakWorkingSet = Process.GetCurrentProcess().PeakWorkingSet64 });
    }

    [Fact]
    public async Task 两千条目完整检查和连续十次调用不遗留目录或句柄()
    {
        using var w = new TestWorkspace(); var path = w.Zip("many.zip", Enumerable.Range(0, 2000).Select(i => ($"{i}.txt", new byte[1024])).ToArray());
        var service = new ArchiveCheckService(); var watch = Stopwatch.StartNew();
        var result = await service.CheckAsync(new(path, temporaryDirectory: w.Output), cancellationToken: Token).WaitAsync(TimeSpan.FromSeconds(30), Token);
        Assert.Equal(ArchiveCheckState.Completed, result.State); Assert.Equal(2000, result.FilesRead); Assert.Equal(2000 * 1024, result.ExpandedBytes);
        Metric("check-many-complete", new { result.FilesRead, result.ExpandedBytes, milliseconds = watch.Elapsed.TotalMilliseconds, processPeakWorkingSet = Process.GetCurrentProcess().PeakWorkingSet64 });
        var small = w.Zip("small.zip", ("a", [1]));
        for (var i = 0; i < 10; i++) Assert.Equal(ArchiveCheckState.Completed, (await service.CheckAsync(new(small, temporaryDirectory: w.Output), cancellationToken: Token)).State);
        Assert.Empty(Directory.GetFileSystemEntries(w.Output));
        using var exclusive = new FileStream(small, FileMode.Open, FileAccess.Read, FileShare.None);
    }

    [Fact]
    public async Task 总超时和预取消不会报告成功()
    {
        using var w = new TestWorkspace(); var input = w.Zip("a.zip", ("a", [1]));
        var extractor = new Stub(async (_, _, _, _, _, _, token) => { await Task.Delay(Timeout.Infinite, token); return new("Zip", [], false); });
        var result = await new ArchiveCheckService(extractor, new ArchiveBrowseService()).CheckAsync(
            new(input, limits: new() { ArchiveTimeout = TimeSpan.FromMilliseconds(30) }, temporaryDirectory: w.Output), cancellationToken: Token);
        Assert.Equal(UnpackError.Timeout, result.Error?.Code); Assert.Empty(Directory.GetFileSystemEntries(w.Output));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        result = await new ArchiveCheckService().CheckAsync(new(input, temporaryDirectory: w.Output), cancellationToken: cancelled.Token);
        Assert.Equal(ArchiveCheckState.Cancelled, result.State); Assert.Empty(result.Evidence);
    }

    [Fact]
    public async Task 失败密码尝试消耗共享预算且不退款()
    {
        using var w = new TestWorkspace(); var path = w.Zip("a.zip", ("a", [1])); var calls = 0;
        var extractor = new Stub((_, _, _, _, budget, _, _) =>
        {
            calls++; budget.AddBytes(6, 6);
            throw new UnpackFailureException(UnpackError.PasswordRequiredOrInvalid, "测试密码或内容损坏");
        });
        var result = await new ArchiveCheckService(extractor, new ArchiveBrowseService()).CheckAsync(new(path, passwords: ["one", "two"],
            limits: new() { MaxTotalBytes = 10, MaxFileBytes = 10 }, temporaryDirectory: w.Output), cancellationToken: Token);
        Assert.Equal(2, calls); Assert.Equal(6, result.ExpandedBytes); Assert.Equal(UnpackError.BudgetExceeded, result.Error?.Code);
        Assert.Empty(Directory.GetFileSystemEntries(w.Output));
    }

    [Fact]
    public async Task 清理残留阻断后续密码尝试且错误不携带原始异常()
    {
        using var w = new TestWorkspace(); var path = w.Zip("a.zip", ("a", [1])); FileStream? held = null; var calls = 0;
        var extractor = new Stub((_, destination, _, _, _, _, _) =>
        {
            calls++; var file = Path.Combine(destination, "held"); File.WriteAllText(file, "temp");
            held = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            // Unix 不依赖占用禁止删除；制造同样有明确所有权的目录故障用现有注入替身验证 Windows 本地边界。
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(destination, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            throw new UnpackFailureException(UnpackError.PasswordRequiredOrInvalid, "缺少密码或损坏");
        });
        ArchiveCheckResult result;
        try
        {
            result = await new ArchiveCheckService(extractor, new ArchiveBrowseService()).CheckAsync(new(path, passwords: ["next"], temporaryDirectory: w.Output), cancellationToken: Token);
        }
        finally
        {
            held?.Dispose();
            if (!OperatingSystem.IsWindows()) foreach (var directory in Directory.GetDirectories(w.Output)) File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        Assert.Equal(1, calls); Assert.NotNull(result.CleanupWarning); Assert.Equal(UnpackError.OutputError, result.Error?.Code);
    }

    [Fact]
    public async Task 越界条目被拒绝且原有目标保持不变()
    {
        using var w = new TestWorkspace(); var keep = PackTests.Source(w, "keep.txt", "keep"u8.ToArray());
        var source = w.Zip("unsafe.zip", ("../keep.txt", "changed"u8.ToArray()));
        var result = await new ArchiveCheckService().CheckAsync(new(source, temporaryDirectory: w.Output), cancellationToken: Token);
        Assert.Equal(UnpackError.UnsafePath, result.Error?.Code); Assert.Equal("keep", File.ReadAllText(keep)); Assert.Empty(Directory.GetFileSystemEntries(w.Output));
    }

    private static void Metric(string name, object value)
    {
        var directory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/performance")); Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "r07-" + name + ".json"), JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
    }
    private sealed class Stub(Func<string, string, string?, LegacyNameEncoding, ExecutionBudget, Action<long>, CancellationToken, Task<ExtractedArchive>> run) : IArchiveExtractor
    {
        public Task<ExtractedArchive> ExtractAsync(ArchiveSource logicalSource, string destination, string? password, LegacyNameEncoding legacyNameEncoding, ExecutionBudget budget, Action<long> progress, CancellationToken cancellationToken)
            => run(logicalSource.PrimaryPath, destination, password, legacyNameEncoding, budget, progress, cancellationToken);
    }
}

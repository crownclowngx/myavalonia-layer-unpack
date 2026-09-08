using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using ICSharpCode.SharpZipLib.Zip;
using LayerUnpackPlugin.Headless.Application;
using LayerUnpackPlugin.Headless.Contracts;
using Xunit;

namespace LayerUnpackPlugin.Headless.Tests;

/// <summary>大目录证据隔离并行负载；不将进程内存峰值当作任意 ZIP 的空间保证。</summary>
[Collection("Performance")]
public sealed class BrowseEdgeTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static BrowseSelection All(IArchiveBrowseSession session) => new(session.Catalog.Entries.Where(e => !e.IsSynthetic).Select(e => e.Id));

    [Fact]
    public async Task ZIP64六万五千余条目录分页选择和内容正确并记录本地基线()
    {
        using var w = new TestWorkspace();
        var path = w.Zip("zip64.zip", Enumerable.Range(0, 65_536).Select(i => ($"folder/{i:D5}.txt", new byte[] { (byte)(i % 251) })).ToArray());
        var allocated = GC.GetTotalAllocatedBytes(); var watch = Stopwatch.StartNew();
        await using var session = await new ArchiveBrowseService().OpenAsync(new(path), cancellationToken: Token);
        var loadMs = watch.Elapsed.TotalMilliseconds; watch.Restart();
        var page = session.Catalog.GetPage(".txt", 65_500, cancellationToken: Token);
        Assert.Equal(65_536, page.TotalMatches); Assert.Equal(36, page.Entries.Count); Assert.False(page.HasNext);
        var pageMs = watch.Elapsed.TotalMilliseconds; var readAtLoad = session.ReadBytes;
        var result = await session.ExtractAsync(new(page.Entries.Take(2).Select(e => e.Id)), w.Output, cancellationToken: Token);
        Assert.Equal(BrowseExtractState.Completed, result.State); Assert.Equal(2, result.Files.Count);
        for (var i = 0; i < 2; i++) Assert.Equal(new byte[] { (byte)((65_500 + i) % 251) }, File.ReadAllBytes(Path.Combine(result.OutputDirectory!, result.Files[i])));
        var directory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/performance/G0010")); Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "zip64-directory.json"), JsonSerializer.Serialize(new
        {
            entries = 65_536,
            rows = session.Catalog.Entries.Count,
            inputBytes = new FileInfo(path).Length,
            loadMilliseconds = loadMs,
            pageMilliseconds = pageMs,
            readBytesAtLoad = readAtLoad,
            result.ReadBytes,
            result.ExpandedBytes,
            allocatedBytes = GC.GetTotalAllocatedBytes() - allocated,
            processPeakWorkingSet = Process.GetCurrentProcess().PeakWorkingSet64
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    [Fact]
    public async Task 旧GB18030目录和提取路径保持同一编码()
    {
        using var w = new TestWorkspace(); var path = w.FilePath("legacy.zip"); Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        using (var output = new ZipOutputStream(File.Create(path), StringCodec.FromEncoding(Encoding.GetEncoding(54936)).WithForcedLegacyEncoding()))
        {
            output.PutNextEntry(new ZipEntry("资料/说明.txt") { IsUnicodeText = false }); output.Write("公开内容"u8); output.CloseEntry(); output.Finish();
        }
        await using var session = await new ArchiveBrowseService().OpenAsync(new(path), cancellationToken: Token);
        Assert.Contains(session.Catalog.Entries, e => e.Path == "资料/说明.txt");
        var result = await session.ExtractAsync(All(session), w.Output, cancellationToken: Token);
        Assert.Equal(BrowseExtractState.Completed, result.State); Assert.Equal("公开内容", File.ReadAllText(Path.Combine(result.OutputDirectory!, "资料/说明.txt")));
    }

    [Fact]
    public async Task ZIP链接条目可解释且不能写入()
    {
        using var w = new TestWorkspace(); var path = w.FilePath("link.zip");
        using (var zip = System.IO.Compression.ZipFile.Open(path, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry("link"); entry.ExternalAttributes = unchecked((int)0xA1FF0000); using var output = entry.Open(); output.Write("../outside"u8);
        }
        await using var session = await new ArchiveBrowseService().OpenAsync(new(path), cancellationToken: Token);
        Assert.Equal(UnpackError.UnsafePath, Assert.Single(session.Catalog.Entries).Problem?.Code);
        Assert.Equal(UnpackError.UnsafePath, (await session.ExtractAsync(All(session), w.Output, cancellationToken: Token)).Error?.Code);
        Assert.False(Directory.Exists(w.Output));
    }

    [Fact]
    public async Task 单文件预算和尝试次数限制明确阻止后续写入()
    {
        using var w = new TestWorkspace(); var path = w.Zip("limits.zip", ("data", new byte[100]));
        await using var session = await new ArchiveBrowseService().OpenAsync(new(path, Limits: new() { Extraction = new() { MaxFileBytes = 99 } }), cancellationToken: Token);
        var first = await session.ExtractAsync(All(session), w.Output, cancellationToken: Token);
        var second = await session.ExtractAsync(All(session), w.Output, cancellationToken: Token);
        Assert.Equal(UnpackError.BudgetExceeded, first.Error?.Code); Assert.Equal(first.ReadBytes, second.ReadBytes); Assert.Empty(Directory.GetFileSystemEntries(w.Output));
        await using var attempts = await new ArchiveBrowseService().OpenAsync(new(path, Limits: new() { Extraction = new() { MaxAttempts = 1 } }), cancellationToken: Token);
        Assert.Equal(BrowseExtractState.Completed, (await attempts.ExtractAsync(All(attempts), w.Output, cancellationToken: Token)).State);
        Assert.Equal(UnpackError.BudgetExceeded, (await attempts.ExtractAsync(All(attempts), w.Output, cancellationToken: Token)).Error?.Code);
    }

    [Fact]
    public async Task 提交前发现源变化会回滚并失效选择()
    {
        using var w = new TestWorkspace(); var path = w.Zip("changed.zip", ("data", [1]));
        await using var session = await new ArchiveBrowseService().OpenAsync(new(path), cancellationToken: Token);
        var modified = File.GetLastWriteTimeUtc(path);
        var result = await session.ExtractAsync(All(session), w.Output, progress: new Callback(p =>
        {
            if (p.Operation == BrowseOperation.Extracting) File.SetLastWriteTimeUtc(path, modified.AddSeconds(2));
        }), cancellationToken: Token);
        Assert.Equal(UnpackError.InputChanged, result.Error?.Code); Assert.True(session.IsInvalidated); Assert.Empty(Directory.GetFileSystemEntries(w.Output));
    }

    [Fact]
    public async Task 清理占用报告自有残留并阻止再次提取()
    {
        using var w = new TestWorkspace(); var path = w.Zip("cleanup.zip", ("data", [1])); FileStream? locked = null;
        await using var session = await new ArchiveBrowseService().OpenAsync(new(path), cancellationToken: Token);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Token);
        try
        {
            var result = await session.ExtractAsync(All(session), w.Output, progress: new Callback(p =>
            {
                if (p.Operation != BrowseOperation.Committing) return;
                var staging = Assert.Single(Directory.GetDirectories(w.Output)); locked = new FileStream(Path.Combine(staging, "data"), FileMode.Open, FileAccess.Read, FileShare.None); cancel.Cancel();
            }), cancellationToken: cancel.Token);
            Assert.Equal(BrowseExtractState.Cancelled, result.State); Assert.NotNull(result.CleanupWarning); Assert.StartsWith(w.Output, result.CleanupWarning);
            var again = await session.ExtractAsync(All(session), w.Output, cancellationToken: Token);
            Assert.Equal(BrowseExtractState.Failed, again.State); Assert.Equal(result.CleanupWarning, again.CleanupWarning); Assert.Single(Directory.GetDirectories(w.Output));
        }
        finally { locked?.Dispose(); }
    }

    [Fact]
    public async Task 分卷标记拒绝且过深路径受预算约束()
    {
        using var w = new TestWorkspace(); var path = w.Zip("volume.zip", ("data", [1]));
        var bytes = File.ReadAllBytes(path); BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(bytes.Length - 22 + 4), 1); File.WriteAllBytes(path, bytes);
        Assert.Equal(UnpackError.MissingVolume, (await Assert.ThrowsAsync<UnpackFailureException>(() => new ArchiveBrowseService().OpenAsync(new(path), cancellationToken: Token))).Code);
        var deep = w.Zip("deep.zip", (string.Join('/', Enumerable.Repeat("a", 130)) + "/data", [1]));
        Assert.Equal(UnpackError.BudgetExceeded, (await Assert.ThrowsAsync<UnpackFailureException>(() => new ArchiveBrowseService().OpenAsync(new(deep), cancellationToken: Token))).Code);
        Assert.False(Directory.Exists(w.Output));
    }

    [Fact]
    public async Task 输出位置是文件时归一化故障且源不受影响()
    {
        using var w = new TestWorkspace(); var path = w.Zip("source.zip", ("data", [1])); File.WriteAllText(w.Output, "existing");
        await using var session = await new ArchiveBrowseService().OpenAsync(new(path), cancellationToken: Token);
        var result = await session.ExtractAsync(All(session), w.Output, cancellationToken: Token);
        Assert.Equal(UnpackError.OutputError, result.Error?.Code); Assert.Equal("existing", File.ReadAllText(w.Output)); Assert.True(File.Exists(path));
    }

    private sealed class Callback(Action<BrowseProgress> action) : IProgress<BrowseProgress> { public void Report(BrowseProgress value) => action(value); }
}

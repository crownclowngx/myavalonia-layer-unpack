using System.IO.Compression;
using LayerUnpackPlugin.Headless.Application;
using LayerUnpackPlugin.Headless.Contracts;
using LayerUnpackPlugin.Headless.Domain;
using LayerUnpackPlugin.Headless.Infrastructure;
using Xunit;

namespace LayerUnpackPlugin.Headless.Tests;

/// <summary>替身只控制失败时机；成功产物始终由真实写入器生成并回读，不用模拟结果冒充事务证据。</summary>
public sealed class RepackSafetyTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    [Theory]
    [InlineData(RepackPhase.Reading)]
    [InlineData(RepackPhase.Planning)]
    [InlineData(RepackPhase.Writing)]
    [InlineData(RepackPhase.Verifying)]
    [InlineData(RepackPhase.Committed)]
    public async Task 各阶段取消清理暂存并保留已提交目标和用户已有目录(RepackPhase phase)
    {
        using var w = new TestWorkspace(); var source = w.Zip("source.zip", ("a.pdf", new byte[300_000]));
        var next = w.Zip("next.zip", ("b.pdf", [2])); Directory.CreateDirectory(w.Output);
        var user = Path.Combine(w.Output, "用户已有目录"); Directory.CreateDirectory(user); File.WriteAllText(Path.Combine(user, "keep.txt"), "keep");
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var result = await new RepackService().ConvertAsync(new([source, next], w.Output), progress: new Relay(p =>
        { if (p.Phase == phase) cancel.Cancel(); }), cancellationToken: cancel.Token);
        Assert.Equal(RepackState.Cancelled, result.State); Assert.Empty(result.CleanupWarnings);
        Assert.Equal(phase == RepackPhase.Committed ? 1 : 0, result.CommittedCount);
        Assert.Equal("keep", File.ReadAllText(Path.Combine(user, "keep.txt"))); Assert.Equal(user, Assert.Single(Directory.GetDirectories(w.Output)));
        Assert.All(result.Groups.Where(g => g.OutputPath is not null), g => Assert.True(File.Exists(g.OutputPath)));
    }

    [Fact]
    public async Task 总字节账本覆盖展开加ZIP写入而非每阶段重新计额()
    {
        using var w = new TestWorkspace(); var source = w.Zip("source.zip", ("a.bin", new byte[1000]));
        var result = await new RepackService().ConvertAsync(new([source], w.Output, options: new() { Compression = PackCompression.Store },
            limits: new() { MaxWrittenBytes = 1500 }), cancellationToken: Token);
        Assert.Equal(RepackState.Failed, result.State); Assert.Equal("BudgetExceeded", result.Groups[0].Error?.Code);
        Assert.Equal(1000, result.Usage.ExpandedBytes); Assert.InRange(result.Usage.WrittenBytes, 1000, 1500);
        Assert.Empty(Directory.GetFileSystemEntries(w.Output));
    }

    [Theory]
    [InlineData("bytes")]
    [InlineData("entries")]
    [InlineData("attempts")]
    public async Task 不同来源继续消耗同一解压账本不能重置(string dimension)
    {
        using var w = new TestWorkspace(); var a = w.Zip("a.zip", ("a", new byte[1000])); var b = w.Zip("b.zip", ("b", new byte[1000]));
        var unpack = dimension switch
        {
            "bytes" => new UnpackLimits { MaxTotalBytes = 1500, MaxFileBytes = 1000 },
            "entries" => new UnpackLimits { MaxEntries = 1 },
            _ => new UnpackLimits { MaxAttempts = 1 }
        };
        var result = await new RepackService().ConvertAsync(new([a, b], w.Output, limits: new() { Unpack = unpack }), cancellationToken: Token);
        Assert.Equal(RepackState.PartiallyCompleted, result.State); Assert.Equal(1, result.CommittedCount);
        Assert.Equal("BudgetExceeded", result.Groups[1].Error?.Code); Assert.Empty(Directory.GetDirectories(w.Output));
    }

    [Fact]
    public async Task 分别包的写入条目额度也在整项累计()
    {
        using var w = new TestWorkspace(); var a = w.Zip("a.zip", ("a", [1])); var b = w.Zip("b.zip", ("b", [2]));
        // 规划需额外容纳来源根凭据；每源一项加根，所以这里用两个文件让两个包合计超过三个写入条目。
        a = w.Zip("aa.zip", ("a1", [1]), ("a2", [1])); b = w.Zip("bb.zip", ("b1", [2]), ("b2", [2]));
        var result = await new RepackService().ConvertAsync(new([a, b], w.Output, limits: new() { Pack = new() { MaxEntries = 3 } }), cancellationToken: Token);
        Assert.Equal(1, result.CommittedCount); Assert.Equal("BudgetExceeded", result.Groups[1].Error?.Code);
        Assert.Equal(2, result.Usage.PackedEntries);
    }

    [Fact]
    public async Task ZIP写入故障保留前后独立成功产物()
    {
        using var w = new TestWorkspace(); var a = w.Zip("a.zip", ("ok1", [1])); var b = w.Zip("b.zip", ("bad", [2])); var c = w.Zip("c.zip", ("ok2", [3]));
        var service = new RepackService(new ArchiveExtractor(), new(), new RepackWriter(new(), new FaultWriter()));
        var result = await service.ConvertAsync(new([a, b, c], w.Output), cancellationToken: Token);
        Assert.Equal(RepackState.PartiallyCompleted, result.State); Assert.Equal(2, result.CommittedCount);
        Assert.Equal("OutputError", result.Groups[1].Error?.Code); Assert.Empty(result.CleanupWarnings);
        Assert.Equal(2, Directory.GetFiles(w.Output).Length); Assert.Empty(Directory.GetDirectories(w.Output));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 写入器遗漏条目或篡改内容必须在提交前回读拒绝(bool tamper)
    {
        using var w = new TestWorkspace(); var source = w.Zip("source.zip", ("expected", [1, 2, 3]));
        var service = new RepackService(new ArchiveExtractor(), new(), new RepackWriter(new(), new WrongWriter(tamper)));
        var result = await service.ConvertAsync(new([source], w.Output), cancellationToken: Token);
        Assert.Equal(RepackState.Failed, result.State); Assert.Equal("OutputError", result.Groups[0].Error?.Code);
        Assert.Empty(Directory.GetFileSystemEntries(w.Output));
    }

    [Fact]
    public async Task 整项超时准确报告并排空引擎及暂存()
    {
        using var w = new TestWorkspace(); var source = w.Zip("source.zip", ("a", [1])); var extractor = new PausingExtractor();
        var service = new RepackService(extractor, new(), new RepackWriter(new(), new ZipArchiveWriter()));
        var result = await service.ConvertAsync(new([source], w.Output, limits: new() { Timeout = TimeSpan.FromMilliseconds(100) }), cancellationToken: Token);
        Assert.True(extractor.Stopped); Assert.Equal(RepackState.Failed, result.State); Assert.Equal("Timeout", result.Groups[0].Error?.Code);
        Assert.Empty(Directory.GetFileSystemEntries(w.Output));
    }

    [Fact]
    public async Task 普通转换不能暗中带入整理排除和错误秘密()
    {
        using var w = new TestWorkspace(); var source = w.Zip("a.zip", ("a", [1])); var service = new RepackService();
        await Assert.ThrowsAsync<PackValidationException>(() => service.ConvertAsync(new([source], w.Output, rules: new(OrganizationFileTypes.Pdf)), cancellationToken: Token));
        await Assert.ThrowsAsync<PackValidationException>(() => service.ConvertAsync(new([source], w.Output, options: new() { Exclusions = new([".tmp"]) }), cancellationToken: Token));
        await Assert.ThrowsAsync<PackValidationException>(() => service.ConvertAsync(new([source], w.Output, options: new() { Encrypt = true }), ["A"], cancellationToken: Token));
        using var secret = new PackSecret("B");
        await Assert.ThrowsAsync<PackValidationException>(() => service.ConvertAsync(new([source], w.Output), targetSecret: secret, cancellationToken: Token));
        Assert.False(Directory.Exists(w.Output));
    }

    [Fact]
    public async Task 并发转换使用独立账本与不覆盖提交()
    {
        using var w = new TestWorkspace(); var source = w.Zip("source.zip", ("a", [1])); var service = new RepackService();
        var results = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => service.ConvertAsync(new([source], w.Output), cancellationToken: Token)));
        Assert.All(results, r => Assert.Equal(RepackState.Completed, r.State));
        Assert.Equal(3, results.SelectMany(r => r.Groups).Select(g => g.OutputPath).Distinct().Count());
        Assert.All(results, r => Assert.Equal(1, r.Usage.ExpandedBytes)); Assert.Empty(Directory.GetDirectories(w.Output));
    }

    [Fact]
    public async Task 中间文件清理残留准确报告但不撤销已提交ZIP()
    {
        using var w = new TestWorkspace(); var source = w.Zip("source.zip", ("data", [1])); FileStream? locked = null;
        try
        {
            var result = await new RepackService().ConvertAsync(new([source], w.Output), progress: new Relay(p =>
            {
                if (p.Phase != RepackPhase.Committed) return;
                var workspace = Assert.Single(Directory.GetDirectories(w.Output, ".layer-unpack-*"));
                locked = new FileStream(Directory.GetFiles(workspace, "data", SearchOption.AllDirectories).Single(), FileMode.Open, FileAccess.Read, FileShare.Read);
            }), cancellationToken: Token);
            Assert.NotNull(locked); Assert.Equal(1, result.CommittedCount); Assert.True(File.Exists(result.Groups[0].OutputPath));
            Assert.Equal(RepackState.CompletedWithWarnings, result.State);
            var residue = Assert.Single(result.CleanupWarnings); Assert.True(PathPolicy.IsWithin(w.Output, residue)); Assert.True(Directory.Exists(residue));
        }
        finally { locked?.Dispose(); }
    }

    private sealed class Relay(Action<RepackProgress> action) : IProgress<RepackProgress> { public void Report(RepackProgress value) => action(value); }
    private sealed class FaultWriter : IArchiveWriter
    {
        public Task WriteAsync(PackPlan plan, Stream output, IProgress<PackProgress>? progress, CancellationToken cancellationToken, PackSecret? secret = null)
        {
            if (plan.Entries.Any(e => e.EntryName == "bad")) { output.WriteByte(1); throw new IOException("private engine text"); }
            return new ZipArchiveWriter().WriteAsync(plan, output, progress, cancellationToken, secret);
        }
    }
    private sealed class WrongWriter(bool tamper) : IArchiveWriter
    {
        public Task WriteAsync(PackPlan plan, Stream output, IProgress<PackProgress>? progress, CancellationToken cancellationToken, PackSecret? secret = null)
        {
            using var zip = new ZipArchive(output, ZipArchiveMode.Create, true);
            if (tamper) { using var stream = zip.CreateEntry("expected").Open(); stream.Write([3, 2, 1]); }
            return Task.CompletedTask;
        }
    }
    private sealed class PausingExtractor : IArchiveExtractor
    {
        public bool Stopped { get; private set; }
        public async Task<ExtractedArchive> ExtractAsync(ArchiveSource logicalSource, string destination, string? password, LegacyNameEncoding legacyNameEncoding,
            ExecutionBudget budget, Action<long> progress, CancellationToken cancellationToken)
        {
            try { await Task.Delay(Timeout.Infinite, cancellationToken); throw new InvalidOperationException(); }
            finally { Stopped = true; }
        }
    }
}

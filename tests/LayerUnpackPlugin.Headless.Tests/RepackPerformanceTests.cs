using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using LayerUnpackPlugin.Headless.Application;
using LayerUnpackPlugin.Headless.Contracts;
using Xunit;

namespace LayerUnpackPlugin.Headless.Tests;

/// <summary>用完整复合流程记录资源与内容证据，包含解压、来源摘要、规则映射、ZIP 写入和回读。
/// 记录本机指标而不设置脆弱的速度断言；真正的门禁是预期清单、预算计量和清理完成。</summary>
[Collection("Performance")]
public sealed class RepackPerformanceTests
{
    [Fact]
    public async Task 两个来源两层整理两千文件逐项核对并记录累计资源()
    {
        using var w = new TestWorkspace(); var token = TestContext.Current.CancellationToken;
        var payload = Enumerable.Range(0, 64).Select(i => (byte)i).ToArray();
        var nested = w.Zip("nested.zip", Enumerable.Range(0, 1000).Select(i => ($"wrap/{i:D4}.pdf", payload)).ToArray());
        var a = w.Zip("left.zip", ("nested.zip", File.ReadAllBytes(nested)), ("top.pdf", [1]), ("skip.txt", [4]));
        var b = w.Zip("right.zip", ("nested.zip", File.ReadAllBytes(nested)), ("top.pdf", [2]), ("empty/", []));
        var before = GC.GetTotalAllocatedBytes(true); var watch = Stopwatch.StartNew();
        var result = await new RepackService().ConvertAsync(new([a, b], w.Output, ConversionMode.ExpandAndOrganize, 2,
            new(OrganizationFileTypes.Pdf)), cancellationToken: token);
        watch.Stop(); var allocated = GC.GetTotalAllocatedBytes(true) - before;
        Assert.Equal(RepackState.Completed, result.State); Assert.Equal(2, result.CommittedCount);
        var digest = Convert.ToHexString(SHA256.HashData(payload));
        var expected = Enumerable.Range(0, 1000).ToDictionary(i => $"nested/wrap/{i:D4}.pdf", _ => digest);
        for (var i = 0; i < 2; i++)
        {
            expected["top.pdf"] = Convert.ToHexString(SHA256.HashData(new[] { (byte)(i + 1) }));
            Assert.Equal(expected.OrderBy(p => p.Key), ConversionTests.Digests(result.Groups[i].OutputPath!).OrderBy(p => p.Key));
        }
        Assert.Empty(result.CleanupWarnings); Assert.Empty(Directory.GetDirectories(w.Output));
        Assert.Equal(result.Groups.Sum(g => g.ArchiveBytes), result.Usage.ArchiveBytes);
        var destination = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/performance/G0012")); Directory.CreateDirectory(destination);
        await File.WriteAllTextAsync(Path.Combine(destination, "repack.json"), JsonSerializer.Serialize(new
        {
            sources = 2,
            depth = 2,
            files = 2002,
            elapsedMilliseconds = watch.Elapsed.TotalMilliseconds,
            allocatedBytes = allocated,
            processPeakWorkingSet = Process.GetCurrentProcess().PeakWorkingSet64,
            usage = result.Usage,
            validation = "每来源 1001 个文件，逐路径 SHA-256 独立回读；暂存全部清理"
        }, new JsonSerializerOptions { WriteIndented = true }), token);
    }
}

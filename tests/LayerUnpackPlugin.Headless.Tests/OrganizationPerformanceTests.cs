using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using LayerUnpackPlugin.Headless.Application;
using LayerUnpackPlugin.Headless.Contracts;
using Xunit;

namespace LayerUnpackPlugin.Headless.Tests;

/// <summary>记录完整验证与复制的实际成本，包含 SHA-256 多次读取；不把小文件测试推导成任意输入的性能保证。</summary>
[Collection("Performance")]
public sealed class OrganizationPerformanceTests
{
    [Fact]
    public async Task 两千文件复杂来源真实样本可复现且预览复制清单摘要一致()
    {
        using var w = new TestWorkspace(); var token = TestContext.Current.CancellationToken;
        var inner = w.Zip("inner.zip", ("单层/第二层/内部.pdf", "内层资料"u8.ToArray()), ("单层/第二层/empty/", []));
        var first = w.Zip("甲/课程.zip", Enumerable.Range(0, 2000).Select(i => ($"包装/资料/{i:D4}.PDF", new byte[64]))
            .Concat(new[] { ("包装/inner.zip", File.ReadAllBytes(inner)), ("包装/说明.txt", "说明"u8.ToArray()) }).ToArray());
        var second = w.Zip("乙/课程.zip", ("外层/包装/内部.pdf", "另一来源"u8.ToArray()), ("外层/包装/empty/", []));
        var input = await OrganizationTestData.ExtractAsync(w, 2, first, second);
        var allocated = GC.GetTotalAllocatedBytes(); var watch = Stopwatch.StartNew();
        var plan = await new OrganizationPlanner().CreateAsync(input, w.Output, new(OrganizationFileTypes.Pdf, true), cancellationToken: token);
        var planMs = watch.Elapsed.TotalMilliseconds; watch.Restart();
        var result = await new OrganizationService().ExecuteAsync(plan, cancellationToken: token);
        var executeMs = watch.Elapsed.TotalMilliseconds;
        var allocatedBytes = GC.GetTotalAllocatedBytes() - allocated;
        Assert.Equal(2002, plan.FileCount); Assert.Equal(OrganizationState.Completed, result.State);
        Assert.Equal(2002, result.Entries.Count(e => !e.IsDirectory)); Assert.Null(result.CleanupWarning);
        foreach (var mapping in plan.Mappings.Where(m => !m.IsDirectory))
        {
            var actual = File.ReadAllBytes(Path.Combine(result.OutputDirectory!, mapping.TargetRelativePath));
            Assert.Equal(mapping.Sha256, Convert.ToHexString(SHA256.HashData(actual)));
        }
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts"));
        var metrics = Path.Combine(root, "performance/G0011"); Directory.CreateDirectory(metrics);
        File.WriteAllText(Path.Combine(metrics, "organization.json"), JsonSerializer.Serialize(new
        {
            files = plan.FileCount,
            mappings = plan.Mappings.Count,
            sources = plan.Sources.Count,
            sourceBytes = plan.TotalBytes,
            planningMilliseconds = planMs,
            executionMilliseconds = executeMs,
            allocatedBytes,
            processPeakWorkingSet = Process.GetCurrentProcess().PeakWorkingSet64,
            digestChecks = "预览验证；执行前验证；复制同步验证；提交前源验证和目标清单验证"
        }, new JsonSerializerOptions { WriteIndented = true }));
        // 样本保存在忽略目录，供人工复查。测试每次重新生成真实 ZIP，故无需依赖下载或提交大型二进制夹具。
        var samples = Path.Combine(root, "G0011/acceptance-sample");
        Directory.CreateDirectory(Path.Combine(samples, "甲")); Directory.CreateDirectory(Path.Combine(samples, "乙"));
        File.Copy(first, Path.Combine(samples, "甲/课程.zip"), true); File.Copy(second, Path.Combine(samples, "乙/课程.zip"), true);
        File.WriteAllText(Path.Combine(samples, "expected.json"), JsonSerializer.Serialize(new
        {
            unpackDepth = 2,
            rules = plan.Rules,
            files = plan.FileCount,
            sources = plan.Sources.Select(s => new { s.TargetName, s.RemovedPrefix }),
            mappings = plan.Mappings.Select(m => new { m.TargetRelativePath, m.IsDirectory, m.Length, m.Sha256 })
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
}

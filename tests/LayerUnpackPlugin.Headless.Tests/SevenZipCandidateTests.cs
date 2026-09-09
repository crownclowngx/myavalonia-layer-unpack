using System.Security.Cryptography;
using System.Text.Json;
using LayerUnpackPlugin.Headless.Application;
using LayerUnpackPlugin.Headless.Contracts;
using SharpCompress.Common;
using SharpCompress.Writers.SevenZip;
using Xunit;

namespace LayerUnpackPlugin.Headless.Tests;

/// <summary>独立候选评估，不接入产品写入器。0.50.4 真实混合条目样本未通过回读和 libarchive 互操作，
/// 记录可复现输入，防止未来仅看到上游 Writer API 就把 7z 加入可创建菜单。</summary>
public sealed class SevenZipCandidateTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task 锁定版本混合条目样本验证失败因此7z创建保持关闭(bool compressHeader)
    {
        using var w = new TestWorkspace(); var path = w.FilePath("candidate.7z");
        await using (var output = File.Create(path))
        await using (var writer = new SevenZipWriter(output, new SevenZipWriterOptions(CompressionType.LZMA2) { CompressHeader = compressHeader }))
        {
            await writer.WriteDirectoryAsync("资料", null, TestContext.Current.CancellationToken);
            await writer.WriteDirectoryAsync("资料/" + new string('a', 105), null, TestContext.Current.CancellationToken);
            using var data = new MemoryStream(Enumerable.Range(0, 4096).Select(i => (byte)(i % 251)).ToArray());
            await writer.WriteAsync("资料/" + new string('a', 105) + "/data.bin", data, null, TestContext.Current.CancellationToken);
            using var empty = new MemoryStream(); await writer.WriteAsync("资料/empty.txt", empty, null, TestContext.Current.CancellationToken);
            using var text = new MemoryStream("R07 中文内容\n"u8.ToArray()); await writer.WriteAsync("资料/说明.txt", text, null, TestContext.Current.CancellationToken);
            await writer.WriteDirectoryAsync("资料/空目录", null, TestContext.Current.CancellationToken);
        }
        var check = await new ArchiveCheckService().CheckAsync(new(path, temporaryDirectory: w.Output), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(ArchiveCheckState.Failed, check.State); Assert.Empty(check.Evidence);
        Assert.DoesNotContain(ArchiveCapabilities.Creation, c => c.Format == PackFormat.SevenZip);
        var input = PackTests.Source(w, "a.txt");
        await Assert.ThrowsAsync<PackValidationException>(() => new PackService().PrepareAsync(new([input], w.FilePath("out.7z"), options: new() { Format = PackFormat.SevenZip }), cancellationToken: TestContext.Current.CancellationToken));
        Directory.CreateDirectory(FormatCreationTests.EvidenceDirectory);
        var artifact = Path.Combine(FormatCreationTests.EvidenceDirectory, $"candidate-header-{compressHeader}.7z"); File.Copy(path, artifact, true);
        File.WriteAllText(artifact + ".json", JsonSerializer.Serialize(new
        { dependency = "SharpCompress 0.50.4", compressHeader, sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))), check.State, check.Error }, new JsonSerializerOptions { WriteIndented = true }));
    }
}

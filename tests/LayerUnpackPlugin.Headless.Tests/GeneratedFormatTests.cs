using System.Text;
using LayerUnpackPlugin.Headless.Application;
using LayerUnpackPlugin.Headless.Contracts;
using Xunit;

namespace LayerUnpackPlugin.Headless.Tests;

public sealed class GeneratedFormatTests
{
    [Theory]
    [InlineData("Generated.utf8.tar")]
    [InlineData("Generated.utf8.tar.gz")]
    [InlineData("Generated.utf8.tar.bz2")]
    [InlineData("Generated.utf8.tar.xz")]
    public async Task 标准库生成的UTF8组合归档保持中文内容且只占一层(string fixture)
    {
        using var w = new TestWorkspace();
        await using var session = new UnpackService().CreateSession(new([w.CopyFixture(fixture)], w.Output));
        var result = await session.ExecuteAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(BatchState.Completed, result.State);
        var node = Assert.Single(result.Nodes);
        Assert.Equal(1, node.Depth);
        Assert.Equal("层解 V1 / UTF-8 / 递归验证\n", await File.ReadAllTextAsync(Path.Combine(node.OutputDirectory!, "普通目录", "中文.txt"), cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(0, new FileInfo(Path.Combine(node.OutputDirectory!, "empty.txt")).Length);
        Assert.Equal(2, Directory.GetFiles(node.OutputDirectory!, "*", SearchOption.AllDirectories).Length);
    }

    [Theory]
    [InlineData("gz")]
    [InlineData("bz2")]
    [InlineData("xz")]
    public async Task 单文件压缩流保持内容且按原文件名输出(string extension)
    {
        using var w = new TestWorkspace();
        await using var session = new UnpackService().CreateSession(new([w.CopyFixture("Generated.single.txt." + extension)], w.Output));
        var result = await session.ExecuteAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(BatchState.Completed, result.State);
        var node = Assert.Single(result.Nodes);
        Assert.Equal("层解 V1 / UTF-8 / 递归验证\n", await File.ReadAllTextAsync(Path.Combine(node.OutputDirectory!, "Generated.single.txt"), cancellationToken: TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("Tar.tar.gz")]
    [InlineData("Tar.tar.bz2")]
    [InlineData("Tar.tar.xz")]
    public async Task 旧编码TAR明确报告而不是提交乱码名称(string fixture)
    {
        using var w = new TestWorkspace();
        await using var session = new UnpackService().CreateSession(new([w.CopyFixture(fixture)], w.Output));
        var result = await session.ExecuteAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(UnpackError.InvalidNameEncoding, Assert.Single(result.Nodes).Error?.Code);
        Assert.Empty(Directory.GetFileSystemEntries(w.Output));
    }

    [Theory]
    [InlineData(LegacyNameEncoding.Gb18030, 54936)]
    [InlineData(LegacyNameEncoding.Utf8, 65001)]
    public async Task 中文ZIP编码由显式选项正确解释(LegacyNameEncoding option, int codePage)
    {
        using var w = new TestWorkspace();
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var path = w.FilePath("中文.zip");
        using (var zip = new System.IO.Compression.ZipArchive(File.Create(path), System.IO.Compression.ZipArchiveMode.Create,
                   false, Encoding.GetEncoding(codePage)))
        {
            using var content = zip.CreateEntry("资料/中文.txt").Open();
            content.Write("中文内容"u8);
        }
        await using var session = new UnpackService().CreateSession(new([path], w.Output, legacyNameEncoding: option));
        var result = await session.ExecuteAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(BatchState.Completed, result.State);
        Assert.Equal("中文内容", await File.ReadAllTextAsync(Path.Combine(result.Nodes[0].OutputDirectory!, "资料", "中文.txt"), cancellationToken: TestContext.Current.CancellationToken));
    }
}

using System.IO.Compression;
using System.Text.Json;
using LayerUnpackPlugin.Headless.Application;
using LayerUnpackPlugin.Headless.Contracts;
using Xunit;

namespace LayerUnpackPlugin.Headless.Tests;

public sealed class RecursiveTests
{
    [Theory]
    [InlineData("test", "Rar.encrypted_filesOnly.rar", "test", "Zip.deflate.WinzipAES.zip", "test")]
    [InlineData("outer-password", "7Zip.LZMA.Aes.7z", "testpassword", "Zip.deflate.pkware.zip", "12345678")]
    public async Task 加密外层与加密后代共享或分别命中候选(string outerPassword, string innerFixture, string innerPassword, string otherFixture, string otherPassword)
    {
        using var w = new TestWorkspace();
        var inner = w.CopyFixture(innerFixture);
        var root = w.FilePath("outer.zip");
        using (var writer = new ICSharpCode.SharpZipLib.Zip.ZipOutputStream(File.Create(root)))
        {
            writer.Password = outerPassword;
            writer.PutNextEntry(new ICSharpCode.SharpZipLib.Zip.ZipEntry("folder/" + innerFixture)
            { AESKeySize = 256, Size = new FileInfo(inner).Length });
            writer.Write(File.ReadAllBytes(inner));
            writer.CloseEntry();
        }
        var other = w.CopyFixture(otherFixture);
        await using var session = new UnpackService().CreateSession(new([root, other], w.Output, 2,
            ["wrong", outerPassword, innerPassword, otherPassword]));
        var result = await session.ExecuteAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(BatchState.Completed, result.State);
        Assert.Equal(3, result.Succeeded);
        var child = Assert.Single(result.Nodes, n => n.ParentId is not null);
        Assert.Equal(2, child.Depth);
        Assert.Equal(3, Directory.GetFiles(child.OutputDirectory!, "*", SearchOption.AllDirectories).Length);
    }
    [Theory]
    [InlineData(1, 1, 2)]
    [InlineData(2, 3, 1)]
    [InlineData(3, 4, 0)]
    public async Task 深度只计算归档且按所有分支执行(int depth, int successes, int stopped)
    {
        using var w = new TestWorkspace();
        var third = w.Zip("third.zip", ("完成.txt", "内容"u8.ToArray()));
        var second = w.Zip("second.zip", ("普通/多级目录/third.zip", File.ReadAllBytes(third)));
        var root = w.Zip("root.zip", ("branch/second.zip", File.ReadAllBytes(second)), ("other.zip", File.ReadAllBytes(third)));
        var original = File.ReadAllBytes(root);
        await using var session = new UnpackService().CreateSession(new([root], w.Output, depth));
        var result = await session.ExecuteAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(BatchState.Completed, result.State);
        Assert.Equal(successes, result.Succeeded);
        Assert.Equal(stopped, result.StoppedByDepth);
        Assert.All(result.Nodes.Where(n => n.Depth > depth), n => Assert.Equal(NodeState.DepthLimit, n.State));
        Assert.All(result.Nodes.Where(n => n.ParentId is not null), n => Assert.Equal(n.Depth - 1, result.Nodes.Single(p => p.Id == n.ParentId).Depth));
        Assert.Equal(original, File.ReadAllBytes(root));
    }

    [Fact]
    public async Task 多个真实密码穿透不同格式和分支且补密只重试失败项()
    {
        using var w = new TestWorkspace();
        var rar = w.CopyFixture("Rar.encrypted_filesOnly.rar");
        var seven = w.CopyFixture("7Zip.LZMA.Aes.7z");
        var zip = w.CopyFixture("Zip.deflate.pkware.zip");
        var root = w.Zip("root.zip", ("a.rar", File.ReadAllBytes(rar)), ("folder/b.7z", File.ReadAllBytes(seven)), ("c.zip", File.ReadAllBytes(zip)));
        await using var session = new UnpackService().CreateSession(new([root, rar], w.Output, 2, ["test", "testpassword"]));
        var first = await session.ExecuteAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(BatchState.PartialFailure, first.State);
        Assert.Equal(4, first.Succeeded);
        var failed = Assert.Single(first.Nodes, n => n.State == NodeState.Failed);
        var committed = first.Nodes.Where(n => n.State == NodeState.Extracted).ToDictionary(n => n.Id, n => n.OutputDirectory);
        var second = await session.RetryAsync([failed.Id], ["12345678"], cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(BatchState.Completed, second.State);
        Assert.Equal(5, second.Succeeded);
        Assert.Equal(first.Nodes.Count, second.Nodes.Count);
        Assert.All(committed, pair => Assert.Equal(pair.Value, second.Nodes.Single(n => n.Id == pair.Key).OutputDirectory));
        Assert.True(second.TotalWrittenBytes > first.TotalWrittenBytes);
        Assert.DoesNotContain("12345678", JsonSerializer.Serialize(second));
        Assert.Empty(Directory.GetDirectories(w.Output, ".layer-unpack-*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task 失败子包不改变父包成功且其他分支继续()
    {
        using var w = new TestWorkspace();
        var good = w.Zip("good.zip", ("a.txt", "good"u8.ToArray()));
        var root = w.Zip("root.zip", ("bad.rar", "corrupt"u8.ToArray()), ("good.zip", File.ReadAllBytes(good)));
        await using var session = new UnpackService().CreateSession(new([root], w.Output, 2));
        var result = await session.ExecuteAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(BatchState.PartialFailure, result.State);
        Assert.Equal(2, result.Succeeded);
        Assert.Equal(NodeState.Extracted, result.Nodes[0].State);
        Assert.Equal(1, result.Failed);
        Assert.True(File.Exists(Path.Combine(result.Nodes[0].OutputDirectory!, "bad.rar")));
    }

    [Fact]
    public async Task 到达深度后加密或损坏子包都不尝试解码()
    {
        using var w = new TestWorkspace();
        var root = w.Zip("root.zip", ("broken.7z", "invalid"u8.ToArray()), ("secret.rar", File.ReadAllBytes(w.CopyFixture("Rar5.encrypted_filesAndHeader.rar"))));
        await using var session = new UnpackService().CreateSession(new([root], w.Output, 1));
        var result = await session.ExecuteAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(1, result.AttemptCount);
        Assert.Equal(2, result.StoppedByDepth);
        Assert.Equal(0, result.Failed);
    }

    [Fact]
    public async Task Gzip包裹ZIP计两层而不是TAR组合规则()
    {
        using var w = new TestWorkspace();
        var zip = w.Zip("inner.zip", ("a", "x"u8.ToArray()));
        var input = w.FilePath("inner.zip.gz");
        await using (var output = new GZipStream(File.Create(input), CompressionLevel.Fastest))
            await output.WriteAsync(await File.ReadAllBytesAsync(zip, cancellationToken: TestContext.Current.CancellationToken), cancellationToken: TestContext.Current.CancellationToken);
        await using var session = new UnpackService().CreateSession(new([input], w.Output, 2));
        var result = await session.ExecuteAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(2, result.Succeeded);
        Assert.Equal(new[] { 1, 2 }, result.Nodes.Select(n => n.Depth));
    }

    [Fact]
    public async Task 发现阶段节点预算耗尽不会误报完成或制造假子包()
    {
        using var w = new TestWorkspace();
        var root = w.Zip("root.zip", ("a.zip", "x"u8.ToArray()), ("b.zip", "x"u8.ToArray()));
        await using var session = new UnpackService().CreateSession(new([root], w.Output, 2, limits: new() { MaxArchives = 2 }));
        var result = await session.ExecuteAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(BatchState.PartialFailure, result.State);
        Assert.Equal(2, result.Nodes.Count);
        Assert.Equal(NodeState.Extracted, result.Nodes[0].State);
        Assert.Equal(UnpackError.BudgetExceeded, result.Nodes[0].Error?.Code);
        Assert.Equal(1, result.DiscoveryFailures);
        Assert.Equal(NodeState.NotRun, result.Nodes[1].State);
    }

}

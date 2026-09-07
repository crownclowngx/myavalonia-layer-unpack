using System.Formats.Tar;
using System.IO.Compression;
using LayerUnpackPlugin.Headless.Application;
using LayerUnpackPlugin.Headless.Contracts;
using LayerUnpackPlugin.Headless.Infrastructure;
using Xunit;

namespace LayerUnpackPlugin.Headless.Tests;

public sealed class SafetyTests
{
    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("folder/../../escape.txt")]
    [InlineData("/rooted.txt")]
    [InlineData("C:/outside.txt")]
    [InlineData("\\\\server\\share\\file")]
    [InlineData("name:stream")]
    [InlineData("CON.txt")]
    [InlineData("aux")]
    [InlineData("COM1.log")]
    [InlineData("LPT9.txt")]
    [InlineData("folder./file")]
    [InlineData("folder /file")]
    [InlineData("a//b")]
    [InlineData("a\0b")]
    public async Task 不安全条目拒绝并清理所有临时写入(string name)
    {
        using var w = new TestWorkspace();
        var input = w.Zip("unsafe.zip", ("first.txt", "first"u8.ToArray()), (name, "bad"u8.ToArray()));
        var original = File.ReadAllBytes(input);
        await using var session = new UnpackService().CreateSession(new([input], w.Output));
        var result = await session.ExecuteAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(BatchState.Failed, result.State);
        Assert.Equal(UnpackError.UnsafePath, result.Nodes[0].Error?.Code);
        Assert.Empty(Directory.GetFileSystemEntries(w.Output));
        Assert.Equal(original, File.ReadAllBytes(input));
        Assert.False(File.Exists(w.FilePath("escape.txt")));
    }

    [Theory]
    [InlineData("same.txt", "same.txt")]
    [InlineData("Same.txt", "same.txt")]
    [InlineData("x/../same.txt", "same.txt")]
    public async Task 重复和大小写别名不能覆盖先写入条目(string first, string second)
    {
        using var w = new TestWorkspace();
        await using var session = new UnpackService().CreateSession(new([w.Zip("a.zip", (first, [1]), (second, [2]))], w.Output));
        var result = await session.ExecuteAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(BatchState.Failed, result.State);
        Assert.Empty(Directory.GetFileSystemEntries(w.Output));
    }

    [Theory]
    [InlineData(TarEntryType.SymbolicLink)]
    [InlineData(TarEntryType.HardLink)]
    [InlineData(TarEntryType.Fifo)]
    public async Task TAR链接和设备不能落盘(TarEntryType type)
    {
        using var w = new TestWorkspace();
        var path = w.FilePath("link.tar");
        using (var writer = new TarWriter(File.Create(path)))
        {
            var entry = new PaxTarEntry(type, "link");
            if (type is TarEntryType.SymbolicLink or TarEntryType.HardLink) entry.LinkName = "../outside";
            writer.WriteEntry(entry);
        }
        await using var session = new UnpackService().CreateSession(new([path], w.Output));
        var result = await session.ExecuteAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(UnpackError.UnsafePath, result.Nodes[0].Error?.Code);
        Assert.Empty(Directory.GetFileSystemEntries(w.Output));
    }

    [Fact]
    public async Task ZIP中心目录中的链接属性被拒绝()
    {
        using var w = new TestWorkspace();
        var path = w.FilePath("link.zip");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry("link");
            entry.ExternalAttributes = unchecked((int)0xA1FF0000);
            using var data = entry.Open(); data.Write("../outside"u8);
        }
        await using var session = new UnpackService().CreateSession(new([path], w.Output));
        Assert.Equal(UnpackError.UnsafePath, (await session.ExecuteAsync(cancellationToken: TestContext.Current.CancellationToken)).Nodes[0].Error?.Code);
        Assert.Empty(Directory.GetFileSystemEntries(w.Output));
    }

    [Fact]
    public async Task 实际输出累计超预算会回滚且禁止继续和重试()
    {
        using var w = new TestWorkspace();
        var input = w.Zip("a.zip", ("a", new byte[60]), ("b", new byte[60]));
        var other = w.Zip("b.zip", ("c", [1]));
        await using var session = new UnpackService().CreateSession(new([input, other], w.Output,
            limits: new() { MaxTotalBytes = 100, MaxFileBytes = 100 }));
        var result = await session.ExecuteAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(UnpackError.BudgetExceeded, result.Nodes[0].Error?.Code);
        Assert.Equal(NodeState.NotRun, result.Nodes[1].State);
        Assert.InRange(result.TotalWrittenBytes, 1, 100);
        Assert.Empty(Directory.GetFileSystemEntries(w.Output));
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.RetryAsync([result.Nodes[0].Id], [], cancellationToken: TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task 单文件与条目预算均可阻止写入(bool fileLimit)
    {
        using var w = new TestWorkspace();
        var input = w.Zip("a.zip", ("a", new byte[20]), ("b", new byte[20]));
        var limits = fileLimit ? new UnpackLimits { MaxFileBytes = 10 } : new UnpackLimits { MaxEntries = 1 };
        await using var session = new UnpackService().CreateSession(new([input], w.Output, limits: limits));
        Assert.Equal(UnpackError.BudgetExceeded, (await session.ExecuteAsync(cancellationToken: TestContext.Current.CancellationToken)).Nodes[0].Error?.Code);
        Assert.Empty(Directory.GetFileSystemEntries(w.Output));
    }

    [Fact]
    public async Task 同名并发提交为各会话分配独立目录且保留既有内容()
    {
        using var w = new TestWorkspace();
        var input = w.Zip("same.zip", ("file", "new"u8.ToArray()));
        var existing = Path.Combine(w.Output, "same");
        Directory.CreateDirectory(existing);
        await File.WriteAllTextAsync(Path.Combine(existing, "file"), "existing", cancellationToken: TestContext.Current.CancellationToken);
        await using var a = new UnpackService().CreateSession(new([input], w.Output));
        await using var b = new UnpackService().CreateSession(new([input], w.Output));
        var results = await Task.WhenAll(Task.Run(() => a.ExecuteAsync(cancellationToken: TestContext.Current.CancellationToken), cancellationToken: TestContext.Current.CancellationToken), Task.Run(() => b.ExecuteAsync(cancellationToken: TestContext.Current.CancellationToken), cancellationToken: TestContext.Current.CancellationToken));
        Assert.All(results, r => Assert.Equal(BatchState.Completed, r.State));
        Assert.NotEqual(results[0].Nodes[0].OutputDirectory, results[1].Nodes[0].OutputDirectory);
        Assert.Equal("existing", await File.ReadAllTextAsync(Path.Combine(existing, "file"), cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(3, Directory.GetDirectories(w.Output).Length);
    }

    [Fact]
    public async Task CRC损坏和中心目录截断不能报告成功()
    {
        using var w = new TestWorkspace();
        var path = w.Zip("crc.zip", ("file", "known-content"u8.ToArray()));
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken: TestContext.Current.CancellationToken);
        var original = bytes.ToArray();
        var dataOffset = bytes.AsSpan().IndexOf("known-content"u8);
        Assert.True(dataOffset > 0);
        bytes[dataOffset] ^= 0x11;
        await File.WriteAllBytesAsync(path, bytes, cancellationToken: TestContext.Current.CancellationToken);
        var truncated = w.FilePath("truncated.zip");
        await File.WriteAllBytesAsync(truncated, original[..^22], cancellationToken: TestContext.Current.CancellationToken);
        await using var session = new UnpackService().CreateSession(new([path, truncated], w.Output));
        var result = await session.ExecuteAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(2, result.Failed);
        Assert.Empty(Directory.GetFileSystemEntries(w.Output));
    }

    [Fact]
    public async Task 清理失败报告本事务残留且不开放重试()
    {
        using var w = new TestWorkspace();
        FileStream? locked = null;
        var extractor = new DelegatingExtractor(async call =>
        {
            await call.WriteAsync([1]);
            locked = new FileStream(Path.Combine(call.Destination, "result.txt"), FileMode.Open, FileAccess.Read, FileShare.None);
            throw new InvalidOperationException("private-sentinel");
        });
        try
        {
            await using var session = new UnpackService(extractor).CreateSession(new([w.Zip("a.zip")], w.Output));
            var result = await session.ExecuteAsync(cancellationToken: TestContext.Current.CancellationToken);
            var residue = Assert.Single(result.Nodes[0].CleanupWarnings);
            Assert.True(PathPolicy.IsWithin(w.Output, residue));
            Assert.DoesNotContain("private-sentinel", System.Text.Json.JsonSerializer.Serialize(result));
            await Assert.ThrowsAsync<InvalidOperationException>(() => session.RetryAsync([result.Nodes[0].Id], [], cancellationToken: TestContext.Current.CancellationToken));
        }
        finally { locked?.Dispose(); }
    }

    [Fact]
    public void 路径包含关系按分隔符判断避免目录前缀碰撞()
    {
        using var w = new TestWorkspace();
        Assert.False(PathPolicy.IsWithin(w.Output, w.Output + "-other"));
        Assert.True(PathPolicy.IsWithin(w.Output, Path.Combine(w.Output, "child")));
    }
}

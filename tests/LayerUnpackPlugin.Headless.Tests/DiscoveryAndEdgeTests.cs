using System.IO.Compression;
using LayerUnpackPlugin.Headless.Application;
using LayerUnpackPlugin.Headless.Contracts;
using LayerUnpackPlugin.Headless.Infrastructure;
using Xunit;

namespace LayerUnpackPlugin.Headless.Tests;

public sealed class DiscoveryAndEdgeTests
{
    [Fact]
    public async Task 目录扫描去重排除输出且识别无扩展名签名()
    {
        using var w = new TestWorkspace();
        var nested = w.Zip("folder/a.zip", ("a", [1]));
        var noExtension = w.Zip("payload", ("a", [1]));
        w.Zip("output/old.zip", ("a", [1]));
        await File.WriteAllTextAsync(w.FilePath("ordinary.txt"), "ordinary", cancellationToken: TestContext.Current.CancellationToken);
        var found = await InputDiscovery.DiscoverAsync([w.Root, nested, w.Root], w.Output, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(new[] { Path.GetFullPath(nested), noExtension }, found.Files);
        Assert.Single(found.Warnings);
    }

    [Fact]
    public async Task 扫描和诊断列表有上限且取消不返回半份成功结果()
    {
        using var w = new TestWorkspace();
        var paths = Enumerable.Range(0, 3).Select(i => w.Zip($"{i}.zip")).ToArray();
        var limited = await InputDiscovery.DiscoverAsync(paths, null, 2, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(2, limited.Files.Count); Assert.Single(limited.Warnings);
        var warnings = await InputDiscovery.DiscoverAsync(Enumerable.Range(0, 150).Select(i => w.FilePath($"missing-{i}")), null, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(100, warnings.Warnings.Count);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => InputDiscovery.DiscoverAsync(paths, null, cancellationToken: cancelled.Token));
    }

    [Fact]
    public async Task 输出和扫描拒绝真实目录链接()
    {
        using var w = new TestWorkspace();
        var input = w.Zip("target/a.zip");
        var link = w.FilePath("link");
        if (OperatingSystem.IsWindows())
        {
            // Junction 同样是重解析点，但创建它不依赖开发者模式或符号链接特权；两个路径都由测试独占。
            var target = Path.GetDirectoryName(input)!;
            Assert.True(PathPolicy.IsWithin(w.Root, target)); Assert.True(PathPolicy.IsWithin(w.Root, link));
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe")
            {
                Arguments = $"/c mklink /J \"{link}\" \"{target}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            })!;
            await process.WaitForExitAsync(TestContext.Current.CancellationToken);
            Assert.Equal(0, process.ExitCode);
        }
        else Directory.CreateSymbolicLink(link, Path.GetDirectoryName(input)!);
        Assert.Throws<UnpackFailureException>(() => new UnpackService().CreateSession(new([input], link)));
        var found = await InputDiscovery.DiscoverAsync([link], null, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Empty(found.Files); Assert.Single(found.Warnings);
        Assert.Throws<UnpackFailureException>(() => PathPolicy.EntryPath(w.Root, "link/a.zip", false));
        Assert.True(File.Exists(input));
        // 先删除本测试创建的链接本身；不让测试根的递归清理碰到 Windows Junction 的只读属性差异。
        Directory.Delete(link);
    }

    [Theory]
    [InlineData("Rar4.multi.part01.rar")]
    [InlineData("Rar5.multi.part01.rar")]
    [InlineData("Rar.multi.solid.part01.rar")]
    public async Task 缺卷真实样本明确失败且不读取其他卷(string name)
    {
        using var w = new TestWorkspace();
        await using var session = new UnpackService().CreateSession(new([w.CopyFixture(name)], w.Output));
        var result = await session.ExecuteAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(UnpackError.MissingVolume, result.Nodes[0].Error?.Code);
        Assert.Empty(Directory.GetFileSystemEntries(w.Output));
    }

    [Theory]
    [InlineData("zip")]
    [InlineData("tar")]
    [InlineData("tar.gz")]
    public async Task 空归档也可得到独立空目录(string extension)
    {
        using var w = new TestWorkspace();
        var path = w.FilePath("empty." + extension);
        if (extension == "zip") w.Zip("empty.zip");
        else if (extension == "tar") await File.WriteAllBytesAsync(path, new byte[1024], cancellationToken: TestContext.Current.CancellationToken);
        else
        {
            await using var stream = new GZipStream(File.Create(path), CompressionLevel.Fastest);
            await stream.WriteAsync(new byte[1024], cancellationToken: TestContext.Current.CancellationToken);
        }
        await using var session = new UnpackService().CreateSession(new([path], w.Output));
        var result = await session.ExecuteAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(BatchState.Completed, result.State);
        Assert.Empty(Directory.GetFileSystemEntries(result.Nodes[0].OutputDirectory!));
    }

    [Fact]
    public async Task 中文长路径输出不会被按旧式260字符截断()
    {
        using var w = new TestWorkspace();
        var entry = string.Join('/', Enumerable.Repeat(new string('长', 45), 5)) + "/文件.txt";
        await using var session = new UnpackService().CreateSession(new([w.Zip("long.zip", (entry, "content"u8.ToArray()))], w.Output));
        var result = await session.ExecuteAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(BatchState.Completed, result.State);
        var output = Path.Combine(result.Nodes[0].OutputDirectory!, entry.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(output.Length > 260);
        Assert.Equal("content", await File.ReadAllTextAsync(output, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task 输入移除和输出被文件占用返回诊断而保留源包()
    {
        using var w = new TestWorkspace();
        var path = w.Zip("a.zip");
        await using (var missing = new UnpackService().CreateSession(new([path], w.Output)))
        {
            File.Delete(path);
            Assert.Equal(UnpackError.InputUnavailable, (await missing.ExecuteAsync(cancellationToken: TestContext.Current.CancellationToken)).Nodes[0].Error?.Code);
        }
        path = w.Zip("a.zip");
        await using var occupied = new UnpackService().CreateSession(new([path], w.Output));
        await File.WriteAllTextAsync(w.Output, "existing-file", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(UnpackError.OutputError, (await occupied.ExecuteAsync(cancellationToken: TestContext.Current.CancellationToken)).Nodes[0].Error?.Code);
        Assert.Equal("existing-file", await File.ReadAllTextAsync(w.Output, cancellationToken: TestContext.Current.CancellationToken));
        Assert.True(File.Exists(path));
    }

    [Theory]
    [InlineData("Zip.deflate.WinzipAES.zip")]
    [InlineData("7Zip.LZMA.Aes.7z")]
    [InlineData("Rar.encrypted_filesOnly.rar")]
    [InlineData("Rar5.encrypted_filesAndHeader.rar")]
    public async Task 错误候选耗尽不能提交输出(string name)
    {
        using var w = new TestWorkspace();
        await using var session = new UnpackService().CreateSession(new([w.CopyFixture(name)], w.Output, passwords: ["wrong-one", "wrong-two"]));
        var result = await session.ExecuteAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(BatchState.Failed, result.State);
        Assert.Equal(UnpackError.PasswordRequiredOrInvalid, result.Nodes[0].Error?.Code);
        Assert.Equal(3, result.AttemptCount);
        Assert.Empty(Directory.GetFileSystemEntries(w.Output));
    }
}

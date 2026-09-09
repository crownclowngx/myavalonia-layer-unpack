using System.Security.Cryptography;
using System.Text.Json;
using LayerUnpackPlugin.Headless.Application;
using LayerUnpackPlugin.Headless.Contracts;
using Xunit;

namespace LayerUnpackPlugin.Headless.Tests;

/// <summary>新增容器必须检查实际产物清单、正文摘要、空目录和回滚，不能只断言扩展名或写入 API 返回成功。</summary>
public sealed class FormatCreationTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    internal static string EvidenceDirectory => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/format-r07"));
    [Theory]
    [InlineData("R07.created.tar")]
    [InlineData("R07.created.tar.gz")]
    public async Task 已归档真实夹具逐文件摘要持续回归(string fixture)
    {
        using var w = new TestWorkspace(); var source = w.CopyFixture(fixture);
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures/r07-fixtures.json")));
        var record = manifest.RootElement.EnumerateArray().Single(r => r.GetProperty("file").GetString() == fixture);
        await using var session = new UnpackService().CreateSession(new([source], w.Output));
        var result = await session.ExecuteAsync(cancellationToken: Token); Assert.Equal(BatchState.Completed, result.State);
        foreach (var entry in record.GetProperty("entries").EnumerateArray())
        {
            var path = Path.Combine(result.Nodes[0].OutputDirectory!, entry.GetProperty("path").GetString()!);
            if (entry.GetProperty("directory").GetBoolean()) Assert.True(Directory.Exists(path));
            else { using var file = File.OpenRead(path); Assert.Equal(entry.GetProperty("sha256").GetString(), Convert.ToHexString(SHA256.HashData(file))); }
        }
    }

    [Theory]
    [InlineData(PackFormat.Zip)]
    [InlineData(PackFormat.Tar)]
    [InlineData(PackFormat.TarGZip)]
    public async Task 新容器中文长路径空文件空目录完整回读并输出互操作样本(PackFormat format)
    {
        using var w = new TestWorkspace();
        var root = Directory.CreateDirectory(w.FilePath("资料")).FullName;
        Directory.CreateDirectory(Path.Combine(root, "空目录"));
        PackTests.Source(w, "资料/empty.txt", []);
        PackTests.Source(w, "资料/说明.txt", "R07 中文内容\n"u8.ToArray());
        PackTests.Source(w, "资料/" + new string('a', 105) + "/data.bin", Enumerable.Range(0, 4096).Select(i => (byte)(i % 251)).ToArray());
        var output = Path.Combine(w.Output, "sample" + ArchiveCapabilities.For(format).Extension);
        var service = new PackService();
        var plan = await service.PrepareAsync(new([root], output, options: new() { Format = format }), cancellationToken: Token);
        var result = await service.ExecuteAsync(plan, cancellationToken: Token);
        Assert.True(result.State == PackState.Completed, JsonSerializer.Serialize(result));
        Directory.CreateDirectory(EvidenceDirectory);
        File.Copy(result.OutputPath!, Path.Combine(EvidenceDirectory, Path.GetFileName(output)), true);
        await using var session = new UnpackService().CreateSession(new([result.OutputPath!], w.FilePath("readback")));
        var read = await session.ExecuteAsync(cancellationToken: Token);
        Assert.True(read.State == BatchState.Completed, JsonSerializer.Serialize(read));
        var actual = read.Nodes[0].OutputDirectory!;
        Assert.Equal(plan.FileCount, Directory.GetFiles(actual, "*", SearchOption.AllDirectories).Length);
        foreach (var entry in plan.Entries)
        {
            var path = Path.Combine(actual, entry.EntryName);
            if (entry.IsDirectory) Assert.True(Directory.Exists(path), entry.EntryName);
            else { using var file = File.OpenRead(path); Assert.Equal(entry.Sha256, Convert.ToHexString(SHA256.HashData(file))); }
        }
        Directory.CreateDirectory(EvidenceDirectory);
        var artifact = Path.Combine(EvidenceDirectory, Path.GetFileName(output)); File.Copy(result.OutputPath!, artifact, true);
        File.WriteAllText(artifact + ".json", JsonSerializer.Serialize(new
        {
            format,
            archive = Path.GetFileName(artifact),
            sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(artifact))),
            entries = plan.Entries.Select(e => new { path = e.EntryName, directory = e.IsDirectory, bytes = e.Length, sha256 = e.Sha256 })
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    [Theory]
    [InlineData(PackFormat.Tar)]
    [InlineData(PackFormat.TarGZip)]
    public async Task 新格式拒绝密码错误扩展名和无效偏好(PackFormat format)
    {
        using var w = new TestWorkspace(); var input = PackTests.Source(w, "a.txt"); var service = new PackService();
        var output = Path.Combine(w.Output, "a" + ArchiveCapabilities.For(format).Extension);
        await Assert.ThrowsAsync<PackValidationException>(() => service.PrepareAsync(new([input], output, options: new() { Format = format, Encrypt = true }), cancellationToken: Token));
        await Assert.ThrowsAsync<PackValidationException>(() => service.PrepareAsync(new([input], output, options: new() { Format = format, Compression = PackCompression.Store }), cancellationToken: Token));
        await Assert.ThrowsAsync<PackValidationException>(() => service.PrepareAsync(new([input], w.FilePath("wrong.zip"), options: new() { Format = format }), cancellationToken: Token));
        var plan = await service.PrepareAsync(new([input], output, options: new() { Format = format }), cancellationToken: Token);
        using var secret = new PackSecret("private-sentinel");
        await Assert.ThrowsAsync<PackValidationException>(() => service.ExecuteAsync(plan, cancellationToken: Token, secret: secret));
        Assert.False(Directory.Exists(w.Output));
    }

    [Theory]
    [InlineData(PackFormat.Tar)]
    [InlineData(PackFormat.TarGZip)]
    public async Task 分别打包及并发冲突编号保留真实复合扩展名(PackFormat format)
    {
        using var w = new TestWorkspace(); var input = PackTests.Source(w, "a.txt"); var extension = ArchiveCapabilities.For(format).Extension;
        var service = new PackBatchService();
        var plan = await service.PrepareAsync(new([input], w.Output, grouping: PackGrouping.Separate, options: new() { Format = format }), cancellationToken: Token);
        await using var first = service.CreateSession(plan); await using var second = service.CreateSession(plan);
        var results = await Task.WhenAll(first.ExecuteAsync(cancellationToken: Token), second.ExecuteAsync(cancellationToken: Token));
        Assert.All(results, r => Assert.Equal(PackBatchState.Completed, r.State));
        Assert.Equal(new[] { "a" + extension, "a (1)" + extension }.Order(), results.Select(r => Path.GetFileName(r.Groups[0].Result.OutputPath)).Order());
        Assert.Empty(Directory.GetFiles(w.Output, ".layer-pack-*"));
    }

    [Theory]
    [InlineData(PackFormat.Tar)]
    [InlineData(PackFormat.TarGZip)]
    public async Task 新格式来源等长变化输出预算与写入收尾取消均不提交(PackFormat format)
    {
        using var w = new TestWorkspace(); var source = PackTests.Source(w, "large.bin", new byte[1024 * 1024]); var service = new PackService();
        var output = Path.Combine(w.Output, "a" + ArchiveCapabilities.For(format).Extension);
        var plan = await service.PrepareAsync(new([source], output, options: new() { Format = format }), cancellationToken: Token);
        var time = File.GetLastWriteTimeUtc(source); File.WriteAllBytes(source, Enumerable.Repeat((byte)1, 1024 * 1024).ToArray()); File.SetLastWriteTimeUtc(source, time);
        Assert.Equal(PackError.InputChanged, (await service.ExecuteAsync(plan, cancellationToken: Token)).Error?.Code);
        plan = await service.PrepareAsync(new([source], output, options: new() { Format = format }), cancellationToken: Token);
        foreach (var phase in new[] { PackState.Writing, PackState.Finalizing })
        {
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
            var result = await service.ExecuteAsync(plan, new SyncProgress<PackProgress>(p => { if (p.State == phase) cancellation.Cancel(); }), cancellation.Token);
            Assert.Equal(PackState.Cancelled, result.State); Assert.Null(result.CleanupWarning);
        }
        var limited = await service.PrepareAsync(new([source], output, new() { MaxArchiveBytes = 20 }, new() { Format = format }), cancellationToken: Token);
        Assert.Equal(PackError.BudgetExceeded, (await service.ExecuteAsync(limited, cancellationToken: Token)).Error?.Code);
        Assert.Empty(Directory.GetFileSystemEntries(w.Output));
    }

    [Theory]
    [InlineData(PackCompression.Fast)]
    [InlineData(PackCompression.High)]
    public async Task TarGZip额外偏好实际内容可检查(PackCompression compression)
    {
        using var w = new TestWorkspace(); var source = PackTests.Source(w, "a.txt"); var service = new PackService();
        var plan = await service.PrepareAsync(new([source], w.FilePath("a.tar.gz"), options: new() { Format = PackFormat.TarGZip, Compression = compression }), cancellationToken: Token);
        var result = await service.ExecuteAsync(plan, cancellationToken: Token);
        Assert.Equal(PackState.Completed, result.State);
        var check = await new ArchiveCheckService().CheckAsync(new(result.OutputPath!, temporaryDirectory: w.Output), cancellationToken: Token);
        Assert.Equal(ArchiveCheckState.CompletedWithLimitations, check.State); Assert.Equal(1, check.FilesRead);
    }
}

internal sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}

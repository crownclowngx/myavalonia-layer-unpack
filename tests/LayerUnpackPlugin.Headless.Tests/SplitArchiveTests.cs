using System.Security.Cryptography;
using System.Text.Json;
using LayerUnpackPlugin.Headless.Application;
using LayerUnpackPlugin.Headless.Contracts;
using LayerUnpackPlugin.Headless.Infrastructure;
using Xunit;

namespace LayerUnpackPlugin.Headless.Tests;

/// <summary>所有正向读取均使用独立 7-Zip 夹具，故障用真实卷的增删、等长替换和重新分段构造。</summary>
public sealed class SplitArchiveTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    [Theory]
    [InlineData("plain", null)]
    [InlineData("solid", null)]
    [InlineData("content-encrypted", "volume-test")]
    [InlineData("header-encrypted", "volume-test")]
    public async Task 外层包提交后同目录分卷作为一个第二层节点完整解出(string variant, string? password)
    {
        using var w = new TestWorkspace(); var parts = SplitTestData.CopyParts(w, variant);
        var outer = w.Zip("outer.zip", parts.Select(p => ("多级/目录/" + Path.GetFileName(p), File.ReadAllBytes(p))).ToArray());
        await using var session = new UnpackService().CreateSession(new([outer], w.Output, 2, password is null ? [] : ["wrong", password]));
        var result = await session.ExecuteAsync(cancellationToken: Token);
        Assert.True(result.State == BatchState.Completed, JsonSerializer.Serialize(result)); Assert.Equal(2, result.Succeeded);
        var child = Assert.Single(result.Nodes, n => n.ParentId is not null);
        Assert.True(child.IsSplitSource); Assert.Equal(5, child.SourceMembers.Count); Assert.Equal(2, child.Depth);
        Assert.Equal(variant, Path.GetFileName(child.OutputDirectory)); SplitTestData.AssertContents(child.OutputDirectory!);
        Assert.All(child.SourceMembers, p => Assert.True(File.Exists(p))); Assert.True(File.Exists(outer));
        Assert.DoesNotContain("volume-test", JsonSerializer.Serialize(result));
        Assert.Empty(Directory.GetDirectories(w.Output, ".layer-unpack-*", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(4)]
    public async Task 任意卷及重复全选都只有一个逻辑节点(int selection)
    {
        using var w = new TestWorkspace(); var parts = SplitTestData.CopyParts(w);
        var found = await InputDiscovery.DiscoverAsync([parts[selection], Path.GetDirectoryName(parts[0])!], w.Output, 1, Token);
        Assert.Single(found.Files); Assert.Empty(found.Warnings); Assert.Equal(5, Assert.Single(found.Sources).Members.Count);
        await using var session = new UnpackService().CreateSession(new(new[] { parts[selection] }.Concat(parts).Concat(parts), w.Output, limits: new() { MaxArchives = 1 }));
        var result = await session.ExecuteAsync(cancellationToken: Token);
        Assert.Equal(1, result.AttemptCount); SplitTestData.AssertContents(Assert.Single(result.Nodes).OutputDirectory!);
        foreach (var part in parts) { using var exclusive = new FileStream(part, FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
    }

    [Theory]
    [InlineData(0, "001")]
    [InlineData(2, "003")]
    public async Task 确定缺口直接报告且不尝试密码(int missing, string number)
    {
        using var w = new TestWorkspace(); var parts = SplitTestData.CopyParts(w); File.Delete(parts[missing]);
        await using var session = new UnpackService().CreateSession(new([parts[1]], w.Output, passwords: ["wrong"]));
        var result = await session.ExecuteAsync(cancellationToken: Token); var node = Assert.Single(result.Nodes);
        Assert.Equal(UnpackError.MissingVolume, node.Error?.Code); Assert.Contains(number, node.Error!.Message);
        Assert.Equal(0, result.AttemptCount); Assert.False(node.CanRetry); Assert.False(Directory.Exists(w.Output));
    }

    [Theory]
    [InlineData("tail")]
    [InlineData("truncate")]
    [InlineData("corrupt")]
    public async Task 不确定尾卷或损坏不能产生已提交输出(string damage)
    {
        using var w = new TestWorkspace(); var parts = SplitTestData.CopyParts(w);
        if (damage == "tail") File.Delete(parts[^1]);
        else if (damage == "truncate") { using var f = File.OpenWrite(parts[^1]); f.SetLength(3); }
        else { var bytes = File.ReadAllBytes(parts[1]); bytes[30] ^= 0xff; File.WriteAllBytes(parts[1], bytes); }
        await using var session = new UnpackService().CreateSession(new([parts[0]], w.Output));
        var result = await session.ExecuteAsync(cancellationToken: Token); var node = Assert.Single(result.Nodes);
        Assert.Equal(NodeState.Failed, node.State); Assert.Null(node.OutputDirectory);
        Assert.Contains(node.Error!.Code, new[] { UnpackError.MissingVolumeOrCorruptArchive, UnpackError.CorruptArchive, UnpackError.MissingVolume });
        Assert.Empty(Directory.GetDirectories(w.Output));
    }

    [Theory]
    [InlineData("000")]
    [InlineData("0001")]
    [InlineData("0006")]
    [InlineData("999999999999999999999")]
    public async Task 歧义编号拒绝而不任意选卷(string suffix)
    {
        using var w = new TestWorkspace(); var parts = SplitTestData.CopyParts(w);
        File.Copy(parts[0], Path.Combine(Path.GetDirectoryName(parts[0])!, "plain.7z." + suffix));
        var source = Assert.Single(ArchiveSourceResolver.ResolveInputs(parts, new(), Token));
        Assert.Equal(UnpackError.InvalidVolumeSet, source.Error?.Code);
    }

    [Fact]
    public void 普通数字文件和不同目录不会被合并()
    {
        using var w = new TestWorkspace(); var a = SplitTestData.CopyParts(w, directory: "a"); var b = SplitTestData.CopyParts(w, directory: "b");
        var plain = w.FilePath("plain.001"); File.WriteAllText(plain, "plain");
        var sources = ArchiveSourceResolver.ResolveInputs([a[2], b[3], plain], new(), Token);
        Assert.Equal(3, sources.Count); Assert.All(sources.Take(2), s => Assert.Equal(5, s.Members.Count)); Assert.False(sources[2].IsSplit);
    }

    [Fact]
    public async Task 不等长且头部跨卷与十进位排序均能读取()
    {
        using var w = new TestWorkspace(); var parts = SplitTestData.CopyParts(w);
        var bytes = parts.SelectMany(File.ReadAllBytes).ToArray(); var offset = 0; var index = 1;
        var root = Directory.CreateDirectory(w.FilePath("uneven")).FullName;
        while (offset < bytes.Length)
        {
            var count = Math.Min(index < 9 ? 1 : 7000, bytes.Length - offset);
            File.WriteAllBytes(Path.Combine(root, $"test.7z.{index:D3}"), bytes.AsSpan(offset, count).ToArray()); offset += count; index++;
        }
        await using var session = new UnpackService().CreateSession(new([Path.Combine(root, "test.7z.010")], w.Output));
        var result = await session.ExecuteAsync(cancellationToken: Token); Assert.True(result.State == BatchState.Completed, JsonSerializer.Serialize(result));
        SplitTestData.AssertContents(Assert.Single(result.Nodes).OutputDirectory!);
    }

    [Theory]
    [InlineData("content")]
    [InlineData("add")]
    [InlineData("delete")]
    public async Task 重试前更改续卷或成员必须开始新任务(string change)
    {
        using var w = new TestWorkspace(); var parts = SplitTestData.CopyParts(w, "header-encrypted");
        await using var session = new UnpackService().CreateSession(new([parts[0]], w.Output));
        var first = await session.ExecuteAsync(cancellationToken: Token); var node = Assert.Single(first.Nodes); Assert.True(node.CanRetry);
        if (change == "content")
        {
            var time = File.GetLastWriteTimeUtc(parts[1]); var bytes = File.ReadAllBytes(parts[1]); bytes[3] ^= 1;
            File.WriteAllBytes(parts[1], bytes); File.SetLastWriteTimeUtc(parts[1], time);
        }
        else if (change == "add") File.Copy(parts[0], parts[0][..^3] + "006");
        else File.Delete(parts[1]);
        var second = await session.RetryAsync([node.Id], ["volume-test"], cancellationToken: Token);
        Assert.Equal(UnpackError.InputChanged, Assert.Single(second.Nodes).Error?.Code); Assert.Equal(first.AttemptCount, second.AttemptCount);
    }

    [Fact]
    public async Task 仅补密码沿用节点且所有句柄在操作结束后释放()
    {
        using var w = new TestWorkspace(); var parts = SplitTestData.CopyParts(w, "header-encrypted");
        await using var session = new UnpackService().CreateSession(new([parts[0]], w.Output));
        var first = await session.ExecuteAsync(cancellationToken: Token); var id = Assert.Single(first.Nodes).Id;
        var second = await session.RetryAsync([id], ["volume-test"], cancellationToken: Token);
        Assert.Equal(id, Assert.Single(second.Nodes).Id); SplitTestData.AssertContents(second.Nodes[0].OutputDirectory!);
        Assert.True(second.AttemptCount > first.AttemptCount);
        foreach (var part in parts) { using var exclusive = File.Open(part, FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
    }

    [Theory]
    [InlineData("bytes")]
    [InlineData("volumes")]
    public async Task 整组预算在任何密码尝试前拒绝(string limit)
    {
        using var w = new TestWorkspace(); var parts = SplitTestData.CopyParts(w);
        var limits = limit == "bytes" ? new UnpackLimits { MaxInputBytes = parts.Sum(p => new FileInfo(p).Length) - 1 } : new() { MaxVolumesPerArchive = 4 };
        await using var session = new UnpackService().CreateSession(new(parts, w.Output, limits: limits));
        var result = await session.ExecuteAsync(cancellationToken: Token);
        Assert.Equal(UnpackError.BudgetExceeded, Assert.Single(result.Nodes).Error?.Code); Assert.True(result.RetryBlocked); Assert.Equal(0, result.AttemptCount);
    }

    [Fact]
    public async Task 读取边界等于整组大小可通过且独占续卷可恢复()
    {
        using var w = new TestWorkspace(); var parts = SplitTestData.CopyParts(w);
        await using var session = new UnpackService().CreateSession(new(parts, w.Output, limits: new() { MaxInputBytes = parts.Sum(p => new FileInfo(p).Length), MaxVolumesPerArchive = 5 }));
        UnpackResult first;
        using (File.Open(parts[2], FileMode.Open, FileAccess.ReadWrite, FileShare.None)) first = await session.ExecuteAsync(cancellationToken: Token);
        Assert.True(Assert.Single(first.Nodes).CanRetry);
        var result = await session.RetryAsync([first.Nodes[0].Id], [], cancellationToken: Token); Assert.True(result.State == BatchState.Completed, JsonSerializer.Serialize(result));
    }

    [Fact]
    public async Task 深度停止仅归组而不尝试读取损坏续卷()
    {
        using var w = new TestWorkspace(); var parts = SplitTestData.CopyParts(w); File.WriteAllBytes(parts[2], [1]);
        var outer = w.Zip("outer.zip", parts.Select(p => ("目录/" + Path.GetFileName(p), File.ReadAllBytes(p))).ToArray());
        await using var session = new UnpackService().CreateSession(new([outer], w.Output));
        var result = await session.ExecuteAsync(cancellationToken: Token); Assert.Equal(1, result.AttemptCount);
        Assert.Equal(NodeState.DepthLimit, result.Nodes[1].State); Assert.Equal(5, result.Nodes[1].SourceMembers.Count);
    }

    [Fact]
    public async Task 完整检查和转换共用逻辑组()
    {
        using var w = new TestWorkspace(); var parts = SplitTestData.CopyParts(w);
        var check = await new ArchiveCheckService().CheckAsync(new(parts[2], ArchiveCheckScope.FullContent), cancellationToken: Token);
        Assert.Equal(ArchiveCheckState.Completed, check.State);
        var converted = await new RepackService().ConvertAsync(new(parts.Concat(parts), w.Output), cancellationToken: Token);
        Assert.Single(converted.Groups); Assert.Equal(RepackState.Completed, converted.Groups[0].State);
        Assert.Equal("plain.zip", Path.GetFileName(converted.Groups[0].OutputPath));
    }
}

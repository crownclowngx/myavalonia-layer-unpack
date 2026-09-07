using System.Text.Json;
using LayerUnpackPlugin.Headless.Application;
using LayerUnpackPlugin.Headless.Contracts;
using LayerUnpackPlugin.Headless.Domain;
using Xunit;

namespace LayerUnpackPlugin.Headless.Tests;

public sealed class DomainTests
{
    [Fact]
    public void 候选保留空格大小写去重且优先复用成功密码()
    {
        using var pool = new PasswordPool();
        pool.Add(PasswordPool.ParseLines("a\r\n A \r\nA\n\na"));
        pool.MarkSuccessful("A");
        Assert.Equal(new string?[] { null, "A", "a", " A " }, pool.Attempts());
        pool.Dispose();
        Assert.Equal(new string?[] { null }, pool.Attempts());
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("a\rb")]
    public void 单个密码不能含换行且异常不回显密码(string value)
    {
        var error = Assert.Throws<UnpackValidationException>(() => PasswordPool.Normalize(["private-sentinel" + value]));
        Assert.DoesNotContain("private-sentinel", error.ToString());
    }

    [Fact]
    public void 非法追加不会半更新候选池()
    {
        using var pool = new PasswordPool();
        pool.Add(["retained"]);
        Assert.Throws<UnpackValidationException>(() => pool.Add(Enumerable.Range(0, 64).Select(x => x.ToString())));
        Assert.Equal(new string?[] { null, "retained" }, pool.Attempts());
        Assert.Throws<UnpackValidationException>(() => pool.Add([new string('x', 1025)]));
    }

    [Fact]
    public void 请求复制输入密码而且序列化不包含密码()
    {
        var paths = new[] { "first" };
        var passwords = new[] { "private-sentinel" };
        var request = new UnpackRequest(paths, "output", passwords: passwords);
        paths[0] = "changed"; passwords[0] = "changed";
        Assert.Equal("first", Assert.Single(request.Inputs));
        Assert.Equal("private-sentinel", Assert.Single(request.Passwords));
        Assert.DoesNotContain("private-sentinel", JsonSerializer.Serialize(request));
        Assert.DoesNotContain("private-sentinel", request.ToString());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(17)]
    [InlineData(int.MaxValue)]
    public void 非法深度在创建会话时拒绝且不创建输出(int depth)
    {
        using var w = new TestWorkspace();
        Assert.Throws<UnpackValidationException>(() => new UnpackService().CreateSession(new([w.FilePath("a.zip")], w.Output, depth)));
        Assert.False(Directory.Exists(w.Output));
    }

    [Fact]
    public void 路径和预算都必须在开始前有效()
    {
        using var w = new TestWorkspace();
        var service = new UnpackService();
        Assert.Throws<UnpackValidationException>(() => service.CreateSession(new(["relative.zip"], w.Output)));
        Assert.Throws<UnpackValidationException>(() => service.CreateSession(new([w.FilePath("a.zip")], "relative")));
        Assert.Throws<UnpackValidationException>(() => service.CreateSession(new([], w.Output)));
        foreach (var limits in new[] { new UnpackLimits { MaxEntries = 0 }, new() { MaxTotalBytes = 1 },
                     new() { MaxArchives = 0 }, new() { MaxAttempts = -1 }, new() { ArchiveTimeout = TimeSpan.Zero } })
            Assert.Throws<UnpackValidationException>(() => service.CreateSession(new([w.FilePath("a.zip")], w.Output, limits: limits)));
        Assert.False(Directory.Exists(w.Output));
    }

    [Fact]
    public void 核心程序集不依赖Avalonia或插件SDK()
    {
        Assert.DoesNotContain(typeof(UnpackService).Assembly.GetReferencedAssemblies(), n =>
            n.Name!.StartsWith("Avalonia", StringComparison.Ordinal) || n.Name.Contains("PluginSdk", StringComparison.Ordinal));
    }
}

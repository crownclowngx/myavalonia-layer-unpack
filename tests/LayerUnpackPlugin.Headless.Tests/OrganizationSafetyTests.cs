using System.Diagnostics;
using LayerUnpackPlugin.Headless.Application;
using LayerUnpackPlugin.Headless.Contracts;
using LayerUnpackPlugin.Headless.Infrastructure;
using Xunit;

namespace LayerUnpackPlugin.Headless.Tests;

public sealed class OrganizationSafetyTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 预览后目标被占用包括复制期间也不覆盖不偷偷改名(bool duringCopy)
    {
        using var w = new TestWorkspace();
        var input = await OrganizationTestData.ExtractAsync(w, 1, w.Zip("source.zip", ("data.pdf", new byte[200_000])));
        var plan = await new OrganizationPlanner().CreateAsync(input, w.Output, cancellationToken: Token);
        void Occupy() { Directory.CreateDirectory(plan.OutputDirectory); File.WriteAllText(Path.Combine(plan.OutputDirectory, "keep"), "existing"); }
        var copier = new CallbackCopier(async (m, target, p, token) => { await new OrganizationCopier().CopyAsync(m, target, p, token); Occupy(); });
        if (!duringCopy) Occupy();
        var result = await new OrganizationService(new(), copier).ExecuteAsync(plan, cancellationToken: Token);
        Assert.Equal(OrganizationError.OutputConflict, result.Error?.Code); Assert.Null(result.OutputDirectory); Assert.Null(result.CleanupWarning);
        Assert.Equal("existing", File.ReadAllText(Path.Combine(plan.OutputDirectory, "keep")));
        Assert.Empty(Directory.GetDirectories(w.Output, ".layer-unpack-*"));
        Assert.False(Directory.Exists(plan.OutputDirectory + " (1)"));
    }

    [Fact]
    public async Task 同一预览并发执行至多一个提交且另一事务清理()
    {
        using var w = new TestWorkspace();
        var input = await OrganizationTestData.ExtractAsync(w, 1, w.Zip("source.zip", ("data.pdf", new byte[200_000])));
        var plan = await new OrganizationPlanner().CreateAsync(input, w.Output, cancellationToken: Token);
        var service = new OrganizationService();
        var results = await Task.WhenAll(service.ExecuteAsync(plan, cancellationToken: Token), service.ExecuteAsync(plan, cancellationToken: Token));
        Assert.Single(results, r => r.State == OrganizationState.Completed); Assert.Single(results, r => r.State == OrganizationState.Failed);
        Assert.All(results, r => Assert.Null(r.CleanupWarning)); Assert.Empty(Directory.GetDirectories(w.Output, ".layer-unpack-*"));
        Assert.Single(Directory.GetFiles(plan.OutputDirectory, "*", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 真实复制已写入后失败或取消仅清理本次暂存(bool cancel)
    {
        using var w = new TestWorkspace(); using var cts = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var archive = w.Zip("source.zip", ("data.pdf", new byte[300_000])); var original = File.ReadAllBytes(archive);
        var input = await OrganizationTestData.ExtractAsync(w, 1, archive);
        var plan = await new OrganizationPlanner().CreateAsync(input, w.Output, cancellationToken: Token);
        var copiedBytes = 0L;
        var copier = new CallbackCopier((m, target, p, token) => new OrganizationCopier().CopyAsync(m, target, bytes =>
        {
            copiedBytes += bytes;
            if (cancel) cts.Cancel(); else throw new IOException("private-io-details");
        }, token));
        var result = await new OrganizationService(new(), copier).ExecuteAsync(plan, cancellationToken: cts.Token);
        Assert.True(copiedBytes > 0); Assert.Equal(cancel ? OrganizationState.Cancelled : OrganizationState.Failed, result.State);
        Assert.Null(result.CleanupWarning); Assert.False(Directory.Exists(plan.OutputDirectory)); Assert.Empty(result.Entries);
        Assert.Empty(Directory.GetDirectories(w.Output, ".layer-unpack-*")); Assert.Equal(original, File.ReadAllBytes(archive));
        Assert.Equal(300_000, new FileInfo(Assert.Single(plan.Mappings, m => !m.IsDirectory).SourcePath).Length);
        Assert.DoesNotContain("private-io-details", System.Text.Json.JsonSerializer.Serialize(result));
    }

    [Fact]
    public async Task 清理失败返回本次残留绝对位置且不报告成功()
    {
        using var w = new TestWorkspace(); FileStream? locked = null;
        var input = await OrganizationTestData.ExtractAsync(w, 1, w.Zip("source.zip", ("data.pdf", [1])));
        var plan = await new OrganizationPlanner().CreateAsync(input, w.Output, cancellationToken: Token);
        var copier = new CallbackCopier(async (m, target, p, token) =>
        {
            await new OrganizationCopier().CopyAsync(m, target, p, token);
            locked = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.None);
            throw new IOException();
        });
        try
        {
            var result = await new OrganizationService(new(), copier).ExecuteAsync(plan, cancellationToken: Token);
            Assert.Equal(OrganizationState.Failed, result.State);
            Assert.NotNull(result.CleanupWarning); Assert.True(PathPolicy.IsWithin(w.Output, result.CleanupWarning));
            Assert.True(Directory.Exists(result.CleanupWarning)); Assert.Null(result.OutputDirectory);
        }
        finally { locked?.Dispose(); }
    }

    [Fact]
    public async Task 复制器返回错误内容会由提交清单复核阻止成功()
    {
        using var w = new TestWorkspace();
        var input = await OrganizationTestData.ExtractAsync(w, 1, w.Zip("source.zip", ("data.pdf", [1])));
        var plan = await new OrganizationPlanner().CreateAsync(input, w.Output, cancellationToken: Token);
        var copier = new CallbackCopier((m, target, p, token) => File.WriteAllBytesAsync(target, new byte[] { 2 }, token));
        var result = await new OrganizationService(new(), copier).ExecuteAsync(plan, cancellationToken: Token);
        Assert.Equal(OrganizationError.OutputError, result.Error?.Code); Assert.False(Directory.Exists(plan.OutputDirectory));
    }

    [Fact]
    public async Task 已复制文件在后续复制期间被修改会导致整次回滚()
    {
        using var w = new TestWorkspace();
        var input = await OrganizationTestData.ExtractAsync(w, 1, w.Zip("source.zip", ("a.pdf", [1]), ("b.pdf", [2])));
        var plan = await new OrganizationPlanner().CreateAsync(input, w.Output, cancellationToken: Token);
        string? first = null;
        var copier = new CallbackCopier(async (m, target, p, token) =>
        {
            await new OrganizationCopier().CopyAsync(m, target, p, token);
            if (first is null) first = m.SourcePath; else File.WriteAllBytes(first, [8]);
        });
        var result = await new OrganizationService(new(), copier).ExecuteAsync(plan, cancellationToken: Token);
        Assert.Equal(OrganizationError.InputChanged, result.Error?.Code); Assert.False(Directory.Exists(plan.OutputDirectory));
        Assert.Empty(Directory.GetDirectories(w.Output, ".layer-unpack-*"));
    }

    [Fact]
    public async Task 超时与预取消均有终态且观察者异常不改变提交事实()
    {
        using var w = new TestWorkspace();
        var input = await OrganizationTestData.ExtractAsync(w, 1, w.Zip("source.zip", ("data.pdf", [1])));
        var plan = await new OrganizationPlanner().CreateAsync(input, w.Output, limits: new() { Timeout = TimeSpan.FromSeconds(1) }, cancellationToken: Token);
        var copier = new CallbackCopier((m, target, p, token) => Task.Delay(Timeout.Infinite, token));
        var result = await new OrganizationService(new(), copier).ExecuteAsync(plan, cancellationToken: Token);
        Assert.Equal(OrganizationError.Timeout, result.Error?.Code); Assert.Null(result.CleanupWarning);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert.Equal(OrganizationState.Cancelled, (await new OrganizationService().ExecuteAsync(plan, cancellationToken: cancelled.Token)).State);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new OrganizationPlanner().CreateAsync(input, w.Output, cancellationToken: cancelled.Token));
        Assert.Equal(OrganizationState.Completed, (await new OrganizationService().ExecuteAsync(plan, new ThrowingProgress(), Token)).State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 预览后来源目录或输出祖先被替换为链接时拒绝(bool outputLink)
    {
        using var w = new TestWorkspace();
        var input = await OrganizationTestData.ExtractAsync(w, 1, w.Zip("source.zip", ("folder/data.pdf", [1])));
        var parent = Directory.CreateDirectory(w.FilePath("organized")).FullName;
        var plan = await new OrganizationPlanner().CreateAsync(input, parent, cancellationToken: Token);
        var link = outputLink ? parent : Path.Combine(input.Nodes[0].OutputDirectory!, "folder");
        var target = w.FilePath("link-target");
        Directory.Move(link, target);
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var info = new ProcessStartInfo("cmd.exe") { Arguments = $"/d /c mklink /J \"{link}\" \"{target}\"", CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
                using var process = Process.Start(info)!; await process.WaitForExitAsync(Token); Assert.Equal(0, process.ExitCode);
            }
            else Directory.CreateSymbolicLink(link, target);
            var result = await new OrganizationService().ExecuteAsync(plan, cancellationToken: Token);
            Assert.Equal(OrganizationError.UnsafePath, result.Error?.Code); Assert.Null(result.OutputDirectory);
            if (!outputLink) Assert.Equal(new byte[] { 1 }, File.ReadAllBytes(Path.Combine(target, "data.pdf")));
        }
        finally { if (Directory.Exists(link)) Directory.Delete(link); }
    }

    private sealed class CallbackCopier(Func<OrganizationMapping, string, Action<long>, CancellationToken, Task> copy) : IOrganizationCopier
    {
        public Task CopyAsync(OrganizationMapping mapping, string destination, Action<long> progress, CancellationToken cancellationToken) => copy(mapping, destination, progress, cancellationToken);
    }
    private sealed class ThrowingProgress : IProgress<OrganizationProgress> { public void Report(OrganizationProgress value) => throw new InvalidOperationException(); }
}

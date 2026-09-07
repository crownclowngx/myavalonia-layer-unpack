using Avalonia.Headless.XUnit;
using LayerUnpackPlugin.Features.Unpack;
using LayerUnpackPlugin.Headless.Application;
using LayerUnpackPlugin.Headless.Contracts;
using LayerUnpackPlugin.Headless.Tests;
using MyAvaloniaManagement.PluginSdk;
using Xunit;

namespace LayerUnpackPlugin.Tests;

public sealed class DocumentTests
{
    [AvaloniaFact]
    public async Task 初始化使用标题且关闭令牌禁止迟到初始化()
    {
        using var lifetime = new TestLifetime();
        await using var document = new UnpackDocument(new UnpackService(), lifetime);
        var notifications = 0;
        document.PresentationChanged += (_, _) => notifications++;
        await document.InitializeAsync(new NewDocumentActivation("当前任务"), CancellationToken.None);
        Assert.Equal("当前任务", document.Presentation.Title);
        Assert.Equal(1, notifications);
        lifetime.Close();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => document.InitializeAsync(new NewDocumentActivation("迟到"), CancellationToken.None).AsTask());
    }

    [AvaloniaFact]
    public async Task 实际Document与Headless执行结果一致且清空重开无历史()
    {
        using var w = new TestWorkspace();
        using var lifetime = new TestLifetime();
        var inner = w.Zip("inner.zip", ("资料.txt", "内容"u8.ToArray()));
        var outer = w.Zip("outer.zip", ("inner.zip", File.ReadAllBytes(inner)));
        await using var direct = new UnpackService().CreateSession(new([outer], Path.Combine(w.Root, "direct"), 2));
        var expected = await direct.ExecuteAsync();
        await using var document = new UnpackDocument(new UnpackService(), lifetime) { OutputDirectory = w.Output, MaxDepth = 2 };
        Assert.False(document.StartCommand.CanExecute(null));
        await document.AddPathsAsync([outer, outer]);
        Assert.Single(document.Inputs);
        Assert.True(document.StartCommand.CanExecute(null));
        await document.StartCommand.ExecuteAsync(null);
        Assert.Equal(expected.State, document.CurrentResult?.State);
        Assert.Equal(expected.Nodes.Select(n => (n.Depth, n.State, n.Format)), document.CurrentResult!.Nodes.Select(n => (n.Depth, n.State, n.Format)));
        Assert.Single(Assert.Single(document.Roots).Children);
        Assert.False(document.IsBusy);
        Assert.Contains("处理完成", document.Message);
        await document.ClearCommand.ExecuteAsync(null);
        Assert.Empty(document.Inputs); Assert.Empty(document.Roots); Assert.Null(document.CurrentResult);
        Assert.True(Directory.Exists(w.Output));
        await using var fresh = new UnpackDocument(new UnpackService(), lifetime);
        Assert.Empty(fresh.Inputs); Assert.Empty(fresh.Roots); Assert.Empty(fresh.PasswordText); Assert.Empty(fresh.OutputDirectory);
    }

    [AvaloniaFact]
    public async Task 真实加密ZIP可选择失败节点补密重试且成功后禁用重试()
    {
        using var w = new TestWorkspace();
        using var lifetime = new TestLifetime();
        await using var document = new UnpackDocument(new UnpackService(), lifetime) { OutputDirectory = w.Output };
        await document.AddPathsAsync([w.CopyFixture("Zip.deflate.pkware.zip")]);
        await document.StartCommand.ExecuteAsync(null);
        document.SelectedNode = Assert.Single(document.Roots);
        Assert.True(document.RetryCommand.CanExecute(null));
        var id = document.SelectedNode.Id;
        document.PasswordText = "12345678";
        await document.RetryCommand.ExecuteAsync(null);
        Assert.Equal(BatchState.Completed, document.CurrentResult?.State);
        Assert.Equal(id, Assert.Single(document.Roots).Id);
        Assert.Empty(document.PasswordText);
        Assert.False(document.RetryCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public async Task 运行期间命令门禁生效且关闭同步释放不等待UI续体()
    {
        using var w = new TestWorkspace();
        using var lifetime = new TestLifetime();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = false;
        var extractor = new DelegatingExtractor(async call =>
        {
            started.SetResult();
            try { await Task.Delay(Timeout.Infinite, call.Token); }
            finally { stopped = true; }
            return new("Test", [], false);
        });
        using var document = new UnpackDocument(new UnpackService(extractor), lifetime) { OutputDirectory = w.Output };
        await document.AddPathsAsync([w.Zip("a.zip")]);
        var work = document.StartCommand.ExecuteAsync(null);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(document.IsBusy); Assert.False(document.CanEdit);
        Assert.False(document.StartCommand.CanExecute(null)); Assert.False(document.ClearCommand.CanExecute(null));
        Assert.True(document.CancelCommand.CanExecute(null));
        lifetime.Close();
        document.Dispose();
        Assert.True(stopped); Assert.True(document.IsClosed);
        var message = document.Message;
        await work.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(message, document.Message);
        Assert.Empty(document.PasswordText);
        Assert.Empty(Directory.GetDirectories(w.Output));
    }

    [AvaloniaFact]
    public async Task 参数错误保留可检查的旧结果且移除只作用于输入()
    {
        using var w = new TestWorkspace();
        using var lifetime = new TestLifetime();
        await using var document = new UnpackDocument(new UnpackService(), lifetime) { OutputDirectory = w.Output };
        await document.AddPathsAsync([w.Zip("a.zip", ("a", [1]))]);
        await document.StartCommand.ExecuteAsync(null);
        var previous = document.CurrentResult;
        document.MaxDepth = 0;
        await document.StartCommand.ExecuteAsync(null);
        Assert.Same(previous, document.CurrentResult);
        Assert.Contains("1–16", document.Message);
        document.Inputs[0].IsSelected = true;
        document.RemoveSelectedCommand.Execute(null);
        Assert.Empty(document.Inputs);
        Assert.Single(document.Roots);
        Assert.True(Directory.Exists(previous!.Nodes[0].OutputDirectory));
    }
}

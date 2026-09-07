using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LayerUnpackPlugin.Features.Unpack;
using LayerUnpackPlugin.Headless.Application;
using LayerUnpackPlugin.Headless.Contracts;
using LayerUnpackPlugin.Headless.Tests;
using Xunit;

namespace LayerUnpackPlugin.Tests;

/// <summary>R01 的用户行为回归。真实归档验证默认输出和递归，替身只控制失败类型及取消时序。</summary>
public sealed class ExperienceTests
{
    [AvaloniaFact]
    public async Task 同目录自动建议且多来源留空移除后恢复建议()
    {
        using var w = new TestWorkspace(); using var lifetime = new TestLifetime();
        await using var document = new UnpackDocument(new UnpackService(), lifetime);
        await document.AddPathsAsync([w.Zip("a.zip", ("a.txt", [1])), w.Zip("b.zip")]);
        Assert.Equal(Path.Combine(w.Root, "解压结果"), document.OutputDirectory);
        Assert.False(Directory.Exists(document.OutputDirectory));
        Assert.True(document.StartCommand.CanExecute(null));
        var other = w.Zip("other/c.zip");
        await document.AddPathsAsync([other]);
        Assert.Empty(document.OutputDirectory);
        Assert.False(document.StartCommand.CanExecute(null));
        document.Inputs.Single(i => i.Path == Path.GetFullPath(other)).IsSelected = true;
        document.RemoveSelectedCommand.Execute(null);
        Assert.Equal(Path.Combine(w.Root, "解压结果"), document.OutputDirectory);
        await document.StartCommand.ExecuteAsync(null);
        Assert.Equal(BatchState.Completed, document.CurrentResult?.State);
        Assert.Equal(new byte[] { 1 }, File.ReadAllBytes(Path.Combine(document.CurrentResult!.Nodes[0].OutputDirectory!, "a.txt")));
        Assert.Equal(document.OutputDirectory, document.ResultOutputPath);
    }

    [AvaloniaFact]
    public async Task 显式确认相同建议后添加新来源也不覆盖且清空解除选择()
    {
        using var w = new TestWorkspace(); using var lifetime = new TestLifetime();
        await using var document = new UnpackDocument(new UnpackService(), lifetime);
        await document.AddPathsAsync([w.Zip("a.zip")]);
        var chosen = document.OutputDirectory;
        document.ChooseOutputDirectory(chosen);
        await document.AddPathsAsync([w.Zip("other/b.zip")]);
        Assert.Equal(chosen, document.OutputDirectory);
        document.OutputDirectory = "";
        await document.AddPathsAsync([w.Zip("c.zip")]);
        Assert.Empty(document.OutputDirectory);
        document.IsRecursive = true; document.RecursiveDepth = 4;
        document.PasswordsExpanded = true; document.OptionsExpanded = true;
        await document.ClearCommand.ExecuteAsync(null);
        Assert.Equal(1, document.MaxDepth); Assert.Equal(2, document.RecursiveDepth);
        Assert.False(document.PasswordsExpanded); Assert.False(document.OptionsExpanded);
        await document.AddPathsAsync([w.FilePath("a.zip")]);
        Assert.Equal(chosen, document.OutputDirectory);
    }

    [AvaloniaFact]
    public async Task 默认一层且递归开关展开所有二层分支并保留有效选择()
    {
        using var w = new TestWorkspace(); using var lifetime = new TestLifetime();
        var third = w.Zip("third.zip", ("最终.txt", "内容"u8.ToArray()));
        var inner = w.Zip("inner.zip", ("third.zip", File.ReadAllBytes(third)));
        var outer = w.Zip("outer.zip", ("a.zip", File.ReadAllBytes(inner)), ("b.zip", File.ReadAllBytes(inner)));
        await using var document = new UnpackDocument(new UnpackService(), lifetime);
        await document.AddPathsAsync([outer]);
        await document.StartCommand.ExecuteAsync(null);
        Assert.Equal(1, document.CurrentResult!.Succeeded);
        Assert.False(document.IsRecursive);
        document.IsRecursive = true;
        Assert.Equal(2, document.MaxDepth);
        await document.StartCommand.ExecuteAsync(null);
        Assert.Equal(3, document.CurrentResult!.Succeeded);
        Assert.Equal(2, document.CurrentResult.StoppedByDepth);
        document.RecursiveDepth = 4;
        document.IsRecursive = false;
        Assert.Equal(1, document.MaxDepth);
        document.IsRecursive = true;
        Assert.Equal(4, document.MaxDepth);
    }

    [AvaloniaFact]
    public async Task 混合批次问题可见补密不重做成功包且打开结果不跟随新参数()
    {
        using var w = new TestWorkspace(); using var lifetime = new TestLifetime();
        await using var document = new UnpackDocument(new UnpackService(), lifetime) { OutputDirectory = w.Output };
        await document.AddPathsAsync([w.Zip("a.zip", ("a.txt", [7])), w.CopyFixture("Zip.deflate.pkware.zip")]);
        await document.StartCommand.ExecuteAsync(null);
        Assert.Equal(BatchState.PartialFailure, document.CurrentResult!.State);
        Assert.False(document.ResultsExpanded); Assert.False(document.PasswordsExpanded);
        var issue = Assert.Single(document.Issues);
        Assert.True(issue.NeedsPassword);
        var firstOutput = document.CurrentResult.Nodes.Single(n => n.State == NodeState.Extracted).OutputDirectory;
        Assert.Equal(firstOutput, document.ResultOutputPath);
        document.SelectedNode = issue;
        document.ShowPasswordEntryCommand.Execute(null);
        Assert.True(document.PasswordsExpanded);
        document.PasswordText = "12345678";
        document.OutputDirectory = w.FilePath("future-output");
        document.MaxDepth = 3; document.SelectedNameEncoding = UnpackDocument.NameEncodings[1];
        await document.RetryCommand.ExecuteAsync(null);
        Assert.Equal(BatchState.Completed, document.CurrentResult.State);
        Assert.Empty(document.Issues); Assert.Empty(document.PasswordText);
        Assert.All(document.CurrentResult.Nodes, n => Assert.Equal(1, n.Depth));
        Assert.Equal(firstOutput, document.CurrentResult.Nodes.Single(n => Path.GetFileName(n.SourcePath) == "a.zip").OutputDirectory);
        Assert.Equal(w.Output, document.ResultOutputPath);
        Assert.False(Directory.Exists(document.OutputDirectory));
        Assert.Equal(2, Directory.GetDirectories(w.Output).Length);
    }

    [AvaloniaFact]
    public async Task 编码失败只导航新批次而不擅自重试或丢弃原结果()
    {
        using var w = new TestWorkspace(); using var lifetime = new TestLifetime();
        var extractor = new DelegatingExtractor(_ => throw new UnpackFailureException(UnpackError.InvalidNameEncoding, "文件名编码不匹配。"));
        await using var document = new UnpackDocument(new UnpackService(extractor), lifetime);
        await document.AddPathsAsync([w.Zip("a.zip")]);
        await document.StartCommand.ExecuteAsync(null);
        document.SelectedNode = Assert.Single(document.Issues);
        var before = document.CurrentResult;
        Assert.True(document.SelectedNode.RequiresNewBatch);
        Assert.False(document.RetryCommand.CanExecute(null));
        document.PrepareNewBatchCommand.Execute(null);
        Assert.True(document.OptionsExpanded);
        Assert.Same(before, document.CurrentResult);
        Assert.Equal("开始新批次", document.StartLabel);
        Assert.Contains("原批次", document.Message);
    }

    [AvaloniaFact]
    public async Task 取消呈现持续到清理退出并禁止重复取消()
    {
        using var w = new TestWorkspace(); using var lifetime = new TestLifetime();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var extractor = new DelegatingExtractor(async call =>
        {
            entered.SetResult();
            try { await Task.Delay(Timeout.Infinite, call.Token); }
            finally { await cleanup.Task; }
            return new("Test", [], false);
        });
        await using var document = new UnpackDocument(new UnpackService(extractor), lifetime);
        await document.AddPathsAsync([w.Zip("a.zip")]);
        var work = document.StartCommand.ExecuteAsync(null);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            document.CancelCommand.Execute(null);
            Assert.True(document.IsBusy); Assert.True(document.IsCancelling);
            Assert.Equal("正在取消", document.StatusTitle);
            Assert.False(document.CancelCommand.CanExecute(null));
            Assert.False(document.StartCommand.CanExecute(null));
        }
        finally { cleanup.TrySetResult(); }
        await work.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.False(document.IsBusy); Assert.False(document.IsCancelling);
        Assert.Equal("已取消", document.StatusTitle);
        Assert.Contains("已取消", document.Summary);
    }

    [AvaloniaTheory]
    [InlineData(false, 900, 760)]
    [InlineData(true, 640, 520)]
    public async Task 默认界面无高级配置且隐藏数值控件不会触发递归(bool dark, int width, int height)
    {
        using var w = new TestWorkspace(); using var lifetime = new TestLifetime();
        await using var document = new UnpackDocument(new UnpackService(), lifetime);
        var view = new UnpackView { DataContext = document };
        var window = new Window { Content = view, Width = width, Height = height, RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light };
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            Assert.True(view.FindControl<Border>("EmptyState")!.IsVisible);
            Capture(window, $"empty-{dark}-{width}");
            await document.AddPathsAsync([w.Zip("资料.zip", ("内容.txt", [1]))]);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(1, document.MaxDepth);
            Assert.False(view.FindControl<StackPanel>("DepthOptions")!.IsVisible);
            foreach (var name in new[] { "InputsExpander", "PasswordsExpander", "OptionsExpander", "ResultsExpander" })
                Assert.False(view.FindControl<Expander>(name)!.IsExpanded);
            var output = view.FindControl<TextBox>("OutputEditor")!;
            Assert.True(output.Focus());
            window.KeyPress(Key.Tab, Avalonia.Input.RawInputModifiers.None, PhysicalKey.Tab, null);
            Assert.NotSame(output, window.FocusManager!.GetFocusedElement());
            Capture(window, $"ready-{dark}-{width}");
            // 使用真实键盘事件触发 CheckBox，验证键盘可达与双向绑定，避免只验证赋值后的镜像状态。
            Assert.True(view.FindControl<CheckBox>("RecursiveToggle")!.Focus());
            window.KeyPress(Key.Space, Avalonia.Input.RawInputModifiers.None, PhysicalKey.Space, null);
            window.KeyRelease(Key.Space, Avalonia.Input.RawInputModifiers.None, PhysicalKey.Space, null);
            Assert.Equal(2, document.MaxDepth);
            view.FindControl<CheckBox>("RecursiveToggle")!.IsChecked = false;
            Assert.Equal(1, document.MaxDepth);
            await document.StartCommand.ExecuteAsync(null);
            Dispatcher.UIThread.RunJobs();
            Assert.True(document.CanOpenResult);
            Assert.False(document.ResultsExpanded);
            Capture(window, $"completed-{dark}-{width}");
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task 折叠结果树仍可点击失败项补密并将焦点带到密码区()
    {
        using var w = new TestWorkspace(); using var lifetime = new TestLifetime();
        await using var document = new UnpackDocument(new UnpackService(), lifetime);
        var view = new UnpackView { DataContext = document };
        var window = new Window { Content = view, Width = 800, Height = 600 };
        try
        {
            window.Show();
            await document.AddPathsAsync([w.Zip("普通资料.zip", ("说明.txt", [1])), w.CopyFixture("Zip.deflate.pkware.zip")]);
            await document.StartCommand.ExecuteAsync(null);
            Dispatcher.UIThread.RunJobs();
            Assert.False(document.ResultsExpanded);
            Capture(window, "partial-failure-800");
            var issueButton = Assert.Single(view.GetVisualDescendants().OfType<Button>(), b => b.Tag is ArchiveNodeViewModel);
            issueButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.Same(Assert.Single(document.Issues), document.SelectedNode);
            var passwordButton = Assert.Single(view.GetVisualDescendants().OfType<Button>(), b => Equals(b.Content, "补充密码"));
            passwordButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.True(document.PasswordsExpanded);
            Assert.Same(view.FindControl<TextBox>("PasswordEditor"), window.FocusManager!.GetFocusedElement());
            window.KeyTextInput("12345678");
            Assert.Equal("12345678", document.PasswordText);
            await document.RetryCommand.ExecuteAsync(null);
            Assert.Equal(BatchState.Completed, document.CurrentResult!.State);
            Assert.Empty(document.PasswordText); Assert.Empty(document.Issues);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task 已解压但校验受限的提示不会被成功摘要隐藏()
    {
        using var w = new TestWorkspace(); using var lifetime = new TestLifetime();
        var extractor = new DelegatingExtractor(async call => (await call.WriteAsync([1])) with { Warning = "当前格式的认证尚未验证。" });
        await using var document = new UnpackDocument(new UnpackService(extractor), lifetime);
        await document.AddPathsAsync([w.Zip("a.zip")]);
        await document.StartCommand.ExecuteAsync(null);
        Assert.Equal(BatchState.Completed, document.CurrentResult!.State);
        Assert.Contains("认证尚未验证", Assert.Single(document.Issues).Details);
        Assert.True(document.CanOpenResult);
        Assert.DoesNotContain("失败 0", document.Summary);
    }

    private static void Capture(Window window, string name)
    {
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        var destination = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/ui/G0007"));
        Directory.CreateDirectory(destination);
        frame.Save(Path.Combine(destination, name + ".png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
    }
}

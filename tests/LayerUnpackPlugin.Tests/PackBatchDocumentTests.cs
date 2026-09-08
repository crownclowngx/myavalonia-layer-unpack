using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.Threading;
using LayerUnpackPlugin.Features.Pack;
using LayerUnpackPlugin.Features.Unpack;
using LayerUnpackPlugin.Headless.Application;
using LayerUnpackPlugin.Headless.Contracts;
using LayerUnpackPlugin.Headless.Infrastructure;
using LayerUnpackPlugin.Headless.Tests;
using Xunit;

namespace LayerUnpackPlugin.Tests;

/// <summary>用真实 Document 与 Headless 覆盖表单快照、密码寿命和逐组恢复；截图只证明自动渲染，不替代原生 DPI 验收。</summary>
public sealed class PackBatchDocumentTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static string Source(TestWorkspace w, string name)
    {
        var path = w.FilePath(name); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "公开测试资料"); return path;
    }

    [AvaloniaFact]
    public async Task 默认合包不增加必选项并且更多选项全部有明确默认值()
    {
        using var w = new TestWorkspace(); using var lifetime = new TestLifetime();
        await using var document = new PackDocument(new PackBatchService(), lifetime);
        Assert.False(document.MoreOptionsExpanded); Assert.False(document.SeparateArchives); Assert.False(document.EncryptionEnabled);
        Assert.Equal(0, document.CompressionIndex); Assert.False(document.ExcludeTemporaryFiles); Assert.False(document.ShowPassword);
        await document.AddPathsAsync([Source(w, "source.txt")]);
        Assert.False(document.DetailsExpanded); await document.StartCommand.ExecuteAsync(null);
        Assert.Equal(1, document.BatchResult?.CompletedCount); Assert.Single(document.GroupResults);
    }

    [AvaloniaFact]
    public async Task 改为分别打包必须先展示新映射且排除明细可检查()
    {
        using var w = new TestWorkspace(); using var lifetime = new TestLifetime();
        await using var document = new PackDocument(new PackBatchService(), lifetime) { OutputDirectory = w.Output };
        Source(w, "a/drop.LOG"); Source(w, "b/keep.txt");
        await document.AddPathsAsync([w.FilePath("a"), w.FilePath("b")]);
        document.SeparateArchives = true; document.ExcludeLogs = true;
        await document.StartCommand.ExecuteAsync(null);
        Assert.Null(document.BatchResult); Assert.True(document.DetailsExpanded); Assert.False(Directory.Exists(w.Output));
        Assert.Single(document.ExcludedMappings); Assert.Contains(document.RootMappings, line => line.Contains("无可打包内容"));
        Assert.Contains(document.EntryMappings, line => line.Contains("b/keep.txt"));
        await document.StartCommand.ExecuteAsync(null);
        Assert.Equal(1, document.BatchResult?.CompletedCount); Assert.Equal(1, document.BatchResult?.SkippedCount); Assert.True(document.HasOutput);
    }

    [AvaloniaFact]
    public async Task 密码重复输入错误阻止执行并且预览不要求密码()
    {
        using var w = new TestWorkspace(); using var lifetime = new TestLifetime();
        await using var document = new PackDocument(new PackBatchService(), lifetime) { OutputDirectory = w.Output, EncryptionEnabled = true };
        await document.AddPathsAsync([Source(w, "source.txt")]);
        Assert.Single(document.RootMappings); Assert.Empty(document.TargetPassword);
        await document.StartCommand.ExecuteAsync(null); Assert.Contains("密码", document.Message); Assert.False(document.HasOutput);
        document.TargetPassword = "public-first"; document.ConfirmPassword = "public-second";
        await document.StartCommand.ExecuteAsync(null); Assert.Contains("不一致", document.Message); Assert.False(Directory.Exists(w.Output));
        document.ConfirmPassword = "public-first";
        await document.StartCommand.ExecuteAsync(null);
        Assert.Equal(1, document.BatchResult?.CompletedCount); Assert.Empty(document.TargetPassword); Assert.Empty(document.ConfirmPassword);
        Assert.False(document.ShowPassword);
    }

    [AvaloniaFact]
    public async Task 两个压缩页面和解压页面的分组密码均独立且密码不进入序列化()
    {
        using var w = new TestWorkspace(); using var aLife = new TestLifetime(); using var bLife = new TestLifetime(); using var uLife = new TestLifetime();
        await using var a = new PackDocument(new PackBatchService(), aLife) { OutputDirectory = w.Output, SeparateArchives = true, EncryptionEnabled = true, TargetPassword = "public-a", ConfirmPassword = "public-a" };
        await using var b = new PackDocument(new PackBatchService(), bLife) { OutputDirectory = w.FilePath("other") };
        await using var unpack = new UnpackDocument(new UnpackService(), uLife);
        unpack.PasswordText = "public-unpack";
        var source = Source(w, "source.txt"); await a.AddPathsAsync([source]); await b.AddPathsAsync([source]);
        Assert.False(b.SeparateArchives); Assert.False(b.EncryptionEnabled); Assert.Empty(b.TargetPassword);
        var json = JsonSerializer.Serialize(a); Assert.DoesNotContain("public-a", json); Assert.DoesNotContain("public-unpack", json);
        await Task.WhenAll(a.StartCommand.ExecuteAsync(null), b.StartCommand.ExecuteAsync(null));
        Assert.Equal(1, a.BatchResult?.CompletedCount); Assert.Equal(1, b.BatchResult?.CompletedCount);
        Assert.Equal("public-unpack", unpack.PasswordText);
        a.ClearCommand.Execute(null); Assert.False(a.SeparateArchives); Assert.False(a.EncryptionEnabled); Assert.Empty(a.TargetPassword);
    }

    [AvaloniaFact]
    public async Task 失败组重试冻结原密码输出和分组且成功包不重复创建()
    {
        using var w = new TestWorkspace(); using var lifetime = new TestLifetime();
        var failedOnce = false;
        var service = new PackBatchService(new PackService(new(), new Writer((plan, output, progress, token, secret) =>
        {
            if (Path.GetFileName(plan.Request.Inputs[0]) == "b.txt" && !failedOnce)
            { failedOnce = true; throw new IOException("公开注入故障"); }
            return new ZipArchiveWriter().WriteAsync(plan, output, progress, token, secret);
        })));
        await using var document = new PackDocument(service, lifetime)
        { OutputDirectory = w.Output, SeparateArchives = true, EncryptionEnabled = true, TargetPassword = "public-original", ConfirmPassword = "public-original" };
        await document.AddPathsAsync([Source(w, "a.txt"), Source(w, "b.txt")]); await document.StartCommand.ExecuteAsync(null);
        Assert.Equal(1, document.BatchResult?.CompletedCount); Assert.True(document.RetryFailedCommand.CanExecute(null)); Assert.True(document.HasOutput);
        var success = document.BatchResult!.Groups[0].Result.OutputPath;
        document.OutputDirectory = w.FilePath("changed"); document.SeparateArchives = false;
        document.TargetPassword = "public-new"; document.ConfirmPassword = "public-new";
        await document.RetryFailedCommand.ExecuteAsync(null);
        Assert.Equal(2, document.BatchResult?.CompletedCount); Assert.Equal(success, document.BatchResult!.Groups[0].Result.OutputPath);
        Assert.False(Directory.Exists(w.FilePath("changed"))); Assert.Equal(2, Directory.GetFiles(w.Output).Length);
        await using var unpack = new UnpackService().CreateSession(new([document.BatchResult.Groups[1].Result.OutputPath!], w.FilePath("readback"), passwords: ["public-original"]));
        Assert.Equal(BatchState.Completed, (await unpack.ExecuteAsync(cancellationToken: Token)).State);
        Assert.Empty(document.TargetPassword); Assert.False(document.RetryFailedCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public async Task 来源改变后重新准备仅包含未完成项且保留已有成功入口()
    {
        using var w = new TestWorkspace(); using var lifetime = new TestLifetime();
        await using var document = new PackDocument(new PackBatchService(), lifetime) { OutputDirectory = w.Output, SeparateArchives = true };
        var a = Source(w, "a.txt"); var b = Source(w, "b.txt");
        await document.AddPathsAsync([a, b]); File.WriteAllText(b, "准备后的新内容");
        await document.StartCommand.ExecuteAsync(null);
        Assert.Equal(1, document.BatchResult?.CompletedCount); Assert.False(document.HasRetryableGroups); Assert.True(document.HasUnfinishedGroups);
        var success = document.BatchResult!.Groups[0].Result.OutputPath;
        await document.PrepareUnfinishedCommand.ExecuteAsync(null);
        Assert.Equal(b, Assert.Single(document.Inputs).Path); Assert.Equal(success, Assert.Single(document.PreviousOutputs));
        Assert.True(document.HasOutput); Assert.Null(document.BatchResult);
        await document.StartCommand.ExecuteAsync(null);
        Assert.Equal(1, document.BatchResult?.CompletedCount); Assert.Equal(2, Directory.GetFiles(w.Output).Length);
        document.ClearCommand.Execute(null); Assert.Empty(document.PreviousOutputs); Assert.False(document.HasOutput);
    }

    [AvaloniaFact]
    public async Task 关闭批次会排空写入清理密码且迟到进度不能改变页面()
    {
        using var w = new TestWorkspace(); using var lifetime = new TestLifetime();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exited = false; IProgress<PackProgress>? delayed = null;
        var service = new PackBatchService(new PackService(new(), new Writer(async (_, output, progress, token, _) =>
        {
            delayed = progress; await output.WriteAsync(new byte[32], token); entered.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, token); } finally { exited = true; }
        })));
        await using var document = new PackDocument(service, lifetime)
        { OutputDirectory = w.Output, SeparateArchives = true, EncryptionEnabled = true, TargetPassword = "public-close", ConfirmPassword = "public-close", ShowPassword = true };
        await document.AddPathsAsync([Source(w, "a.txt"), Source(w, "b.txt")]);
        var active = document.StartCommand.ExecuteAsync(null); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        await document.DisposeAsync(); await active;
        Assert.True(exited); Assert.Empty(document.TargetPassword); Assert.Empty(document.ConfirmPassword); Assert.False(document.ShowPassword);
        var summary = document.Summary;
        delayed!.Report(new(PackState.Writing, "迟到", 1, 1, 1, 1)); Dispatcher.UIThread.RunJobs();
        Assert.Equal(summary, document.Summary); Assert.Empty(Directory.GetFiles(w.Output));
    }

    [AvaloniaTheory]
    [InlineData(false, 900, 760)]
    [InlineData(true, 900, 760)]
    [InlineData(false, 640, 520)]
    [InlineData(true, 640, 520)]
    public async Task 批次默认展开和部分失败状态在主题尺寸下可用(bool dark, int width, int height)
    {
        using var w = new TestWorkspace(); using var lifetime = new TestLifetime();
        await using var document = new PackDocument(new PackBatchService(), lifetime) { OutputDirectory = w.Output };
        var view = new PackView { DataContext = document };
        var window = new Window { Content = view, Width = width, Height = height, RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light };
        var destination = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/ui/G0009")); Directory.CreateDirectory(destination);
        void Capture(string state)
        {
            Dispatcher.UIThread.RunJobs();
            var start = view.FindControl<Button>("StartButton")!; var point = start.TranslatePoint(default, window)!.Value;
            Assert.InRange(point.Y, 0, window.ClientSize.Height - start.Bounds.Height);
            Assert.InRange(point.X, 0, window.ClientSize.Width - start.Bounds.Width);
            using var frame = window.CaptureRenderedFrame(); Assert.NotNull(frame);
            frame.Save(Path.Combine(destination, $"batch-{state}-{(dark ? "dark" : "light")}-{width}x{height}.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        }
        try
        {
            window.Show(); Capture("default");
            var a = Source(w, "a.txt"); var b = Source(w, "b.txt"); await document.AddPathsAsync([a, b]);
            document.SeparateArchives = true; document.MoreOptionsExpanded = true; document.EncryptionEnabled = true;
            document.TargetPassword = "public-render"; document.ConfirmPassword = "public-render";
            var password = view.FindControl<TextBox>("PasswordEditor")!;
            password.BringIntoView(); Dispatcher.UIThread.RunJobs();
            Assert.Equal('●', password.PasswordChar); Assert.True(password.Focus());
            window.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null);
            Assert.NotSame(password, window.FocusManager!.GetFocusedElement());
            document.ShowPassword = true; Assert.Equal('\0', password.PasswordChar); document.ShowPassword = false;
            Capture("expanded");
            await document.PreviewCommand.ExecuteAsync(null); File.WriteAllText(b, "准备后发生变化"); await document.StartCommand.ExecuteAsync(null);
            view.FindControl<ScrollViewer>("MainScroller")!.Offset = default; Capture("partial-failure");
            Assert.Equal(1, document.BatchResult?.CompletedCount); Assert.Equal(1, document.BatchResult?.FailedCount);
            var open = view.FindControl<Button>("OpenResultButton")!; Assert.True(open.IsVisible);
            Assert.InRange(open.TranslatePoint(default, window)!.Value.Y, 0, window.ClientSize.Height - open.Bounds.Height);
        }
        finally { window.Close(); }
    }

    private sealed class Writer(Func<PackPlan, Stream, IProgress<PackProgress>?, CancellationToken, PackSecret?, Task> action) : IArchiveWriter
    { public Task WriteAsync(PackPlan plan, Stream output, IProgress<PackProgress>? progress, CancellationToken cancellationToken, PackSecret? secret = null) => action(plan, output, progress, cancellationToken, secret); }
}

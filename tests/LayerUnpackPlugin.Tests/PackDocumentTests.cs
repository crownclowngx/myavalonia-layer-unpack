using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LayerUnpackPlugin.Features.Pack;
using LayerUnpackPlugin.Features.Unpack;
using LayerUnpackPlugin.Headless.Application;
using LayerUnpackPlugin.Headless.Contracts;
using LayerUnpackPlugin.Headless.Infrastructure;
using LayerUnpackPlugin.Headless.Tests;
using LayerUnpackPlugin.Standalone;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MyAvaloniaManagement.PluginSdk;
using Xunit;

namespace LayerUnpackPlugin.Tests;

public sealed class PackDocumentTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static string Source(TestWorkspace w, string name)
    {
        var path = w.FilePath(name); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "普通中文资料"); return path;
    }
    [AvaloniaFact]
    public async Task 默认操作建议路径并完成ZIP无需高级选项()
    {
        using var w = new TestWorkspace(); using var lifetime = new TestLifetime();
        await using var document = new PackDocument(new PackBatchService(), lifetime);
        await document.InitializeAsync(new NewDocumentActivation("压缩任务"), Token);
        await document.AddPathsAsync([Source(w, "资料/正文.txt")]);
        Assert.Equal(w.FilePath("资料"), document.OutputDirectory);
        Assert.Equal("正文.zip", document.ArchiveName); Assert.True(document.StartCommand.CanExecute(null));
        Assert.False(document.DetailsExpanded); Assert.Single(document.RootMappings);
        await document.StartCommand.ExecuteAsync(null);
        Assert.Equal(PackState.Completed, document.CurrentResult?.State);
        Assert.True(File.Exists(document.CurrentResult?.OutputPath)); Assert.True(document.HasOutput);
        document.ClearCommand.Execute(null);
        Assert.Empty(document.Inputs); Assert.False(document.HasOutput); Assert.False(document.StartCommand.CanExecute(null));
    }
    [AvaloniaFact]
    public async Task 手动名称位置不被后续输入建议覆盖且多来源要求选择位置()
    {
        using var w = new TestWorkspace(); using var lifetime = new TestLifetime();
        await using var document = new PackDocument(new PackBatchService(), lifetime);
        await document.AddPathsAsync([Source(w, "a/one.txt"), Source(w, "b/two.txt")]);
        Assert.Empty(document.OutputDirectory); Assert.False(document.StartCommand.CanExecute(null));
        document.ArchiveName = "交付.zip"; document.ChooseOutputDirectory(w.Output);
        await document.AddPathsAsync([Source(w, "c/three.txt")]);
        Assert.Equal("交付.zip", document.ArchiveName); Assert.Equal(w.Output, document.OutputDirectory);
        await document.StartCommand.ExecuteAsync(null);
        Assert.Equal(3, document.CurrentResult?.FileCount);
    }
    [AvaloniaFact]
    public async Task 不合法名称和变更来源给出可恢复的新任务提示()
    {
        using var w = new TestWorkspace(); using var lifetime = new TestLifetime();
        await using var document = new PackDocument(new PackBatchService(), lifetime);
        var source = Source(w, "input/a.txt"); await document.AddPathsAsync([source]);
        File.WriteAllText(source, "变更内容");
        await document.StartCommand.ExecuteAsync(null);
        Assert.Equal(PackError.InputChanged, document.CurrentResult?.Error?.Code); Assert.True(document.CanEdit);
        await document.StartCommand.ExecuteAsync(null); Assert.True(document.HasOutput);
        document.ArchiveName = "../bad.zip";
        await document.StartCommand.ExecuteAsync(null);
        Assert.Contains("名称", document.Message); Assert.True(document.CanEdit); Assert.False(document.HasOutput);
    }
    [AvaloniaFact]
    public async Task 写入中参数冻结下次执行才使用新目标()
    {
        using var w = new TestWorkspace(); using var lifetime = new TestLifetime();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new PackService(new(), new Writer(async (plan, output, progress, token) =>
        {
            entered.TrySetResult(); await release.Task.WaitAsync(token);
            await new ZipArchiveWriter().WriteAsync(plan, output, progress, token);
        }));
        await using var document = new PackDocument(new PackBatchService(service), lifetime) { OutputDirectory = w.Output };
        await document.AddPathsAsync([Source(w, "source.txt")]);
        var work = document.StartCommand.ExecuteAsync(null);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
            Assert.False(document.CanEdit); Assert.False(document.StartCommand.CanExecute(null));
            document.ArchiveName = "next.zip"; release.TrySetResult(); await work;
            Assert.EndsWith("source.zip", document.CurrentResult?.OutputPath);
            await document.StartCommand.ExecuteAsync(null);
            Assert.EndsWith("next.zip", document.CurrentResult?.OutputPath);
        }
        finally { release.TrySetResult(); }
    }
    [AvaloniaFact]
    public async Task 关闭等待后台清理且迟到进度不能覆盖关闭页()
    {
        using var w = new TestWorkspace(); using var lifetime = new TestLifetime();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        IProgress<PackProgress>? captured = null; var stopped = false;
        var service = new PackService(new(), new Writer(async (_, output, progress, token) =>
        {
            captured = progress; await output.WriteAsync(new byte[32], token); entered.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, token); } finally { stopped = true; }
        }));
        await using var document = new PackDocument(new PackBatchService(service), lifetime) { OutputDirectory = w.Output };
        await document.AddPathsAsync([Source(w, "source.txt")]);
        var work = document.StartCommand.ExecuteAsync(null);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        lifetime.Close(); document.Dispose(); await work;
        var summary = document.Summary;
        captured!.Report(new(PackState.Writing, "迟到数据", 1, 1, 1, 1)); Dispatcher.UIThread.RunJobs();
        Assert.True(stopped); Assert.True(document.IsClosed); Assert.Equal(summary, document.Summary);
        Assert.Empty(Directory.GetFiles(w.Output));
    }
    [AvaloniaFact]
    public async Task 压缩取消时解压任务继续运行完成()
    {
        using var w = new TestWorkspace(); using var packLife = new TestLifetime(); using var unpackLife = new TestLifetime();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new PackService(new(), new Writer(async (_, _, _, token) => { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, token); }));
        await using var pack = new PackDocument(new PackBatchService(service), packLife) { OutputDirectory = w.Output };
        await using var unpack = new UnpackDocument(new UnpackService(new DelegatingExtractor(async call => { await release.Task.WaitAsync(call.Token); return await call.WriteAsync([1]); })), unpackLife) { OutputDirectory = w.FilePath("unpacked") };
        await pack.AddPathsAsync([Source(w, "source.txt")]); await unpack.AddPathsAsync([w.Zip("source.zip")]);
        var packWork = pack.StartCommand.ExecuteAsync(null); var unpackWork = unpack.StartCommand.ExecuteAsync(null);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token); pack.CancelCommand.Execute(null); await packWork;
            Assert.Equal(PackState.Cancelled, pack.CurrentResult?.State); Assert.True(unpack.IsBusy); Assert.False(unpack.IsClosed);
            release.TrySetResult(); await unpackWork;
            Assert.Equal(BatchState.Completed, unpack.CurrentResult?.State);
        }
        finally { release.TrySetResult(); }
    }
    [AvaloniaTheory]
    [InlineData(false, 900, 760)]
    [InlineData(true, 900, 760)]
    [InlineData(false, 640, 520)]
    [InlineData(true, 640, 520)]
    public async Task 压缩页面空状态准备完成状态在主题尺寸下可用(bool dark, int width, int height)
    {
        using var w = new TestWorkspace(); using var lifetime = new TestLifetime();
        await using var document = new PackDocument(new PackBatchService(), lifetime);
        var view = new PackView { DataContext = document };
        var window = new Window { Content = view, Width = width, Height = height, RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light };
        var destination = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/ui/G0008"));
        Directory.CreateDirectory(destination);
        void Capture(string state)
        {
            Dispatcher.UIThread.RunJobs();
            var start = view.FindControl<Button>("StartButton")!;
            Assert.Same(document.StartCommand, start.Command);
            var origin = start.TranslatePoint(default, window)!.Value;
            Assert.InRange(origin.Y, 0, window.ClientSize.Height - start.Bounds.Height);
            Assert.InRange(origin.X, 0, window.ClientSize.Width - start.Bounds.Width);
            Assert.False(document.MoreOptionsExpanded);
            using var frame = window.CaptureRenderedFrame(); Assert.NotNull(frame);
            frame.Save(Path.Combine(destination, $"pack-{state}-{(dark ? "dark" : "light")}-{width}x{height}.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        }
        try
        {
            window.Show(); Capture("empty");
            await document.AddPathsAsync([Source(w, "项目资料/中文说明.txt")]); Capture("ready");
            var name = view.FindControl<TextBox>("NameEditor")!;
            Assert.True(name.Focus()); window.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null);
            Assert.NotSame(name, window.FocusManager!.GetFocusedElement());
            await document.StartCommand.ExecuteAsync(null); Capture("completed");
            Assert.True(view.FindControl<Button>("OpenResultButton")!.IsVisible);
            var open = view.FindControl<Button>("OpenResultButton")!;
            Assert.InRange(open.TranslatePoint(default, window)!.Value.Y, 0, window.ClientSize.Height - open.Bounds.Height);
            Assert.Equal(PackState.Completed, document.CurrentResult?.State);
        }
        finally { window.Close(); }
    }
    [AvaloniaFact]
    public async Task Standalone双页使用独立Scope且关闭压缩工作后释放窗口()
    {
        using var w = new TestWorkspace();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var stopped = false;
        var services = StandaloneServices.Create();
        services.Replace(ServiceDescriptor.Singleton<IArchiveWriter>(new Writer(async (_, _, _, token) =>
        {
            entered.TrySetResult(); try { await Task.Delay(Timeout.Infinite, token); } finally { stopped = true; }
        })));
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        var window = new MainWindow(provider);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); window.Closed += (_, _) => closed.TrySetResult();
        try
        {
            window.Show();
            window.FindControl<TabControl>("TaskTabs")!.SelectedIndex = 1;
            var pack = Assert.IsType<PackDocument>(Assert.IsType<PackView>(window.FindControl<ContentControl>("PackPreviewHost")!.Content).DataContext);
            var unpack = Assert.IsType<UnpackDocument>(Assert.IsType<UnpackView>(window.FindControl<ContentControl>("PreviewHost")!.Content).DataContext);
            pack.OutputDirectory = w.Output; await pack.AddPathsAsync([Source(w, "source.txt")]);
            var work = pack.StartCommand.ExecuteAsync(null); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
            Assert.Empty(unpack.OutputDirectory); window.Close();
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(5), Token); await work;
            Assert.True(stopped); Assert.True(pack.IsClosed); Assert.True(unpack.IsClosed);
            Assert.Null(window.FindControl<ContentControl>("PackPreviewHost")!.Content);
        }
        finally { window.Close(); }
    }
    [AvaloniaFact]
    public async Task 同名来源未预览映射时先展示清单再执行()
    {
        using var w = new TestWorkspace(); using var lifetime = new TestLifetime();
        await using var document = new PackDocument(new PackBatchService(), lifetime);
        await document.AddPathsAsync([Source(w, "a/same.txt"), Source(w, "b/same.txt")]);
        document.OutputDirectory = w.Output;
        await document.StartCommand.ExecuteAsync(null);
        Assert.Null(document.CurrentResult); Assert.False(Directory.Exists(w.Output));
        Assert.Contains(document.RootMappings, line => line.Contains("same (1).txt"));
        await document.StartCommand.ExecuteAsync(null);
        Assert.Equal(PackState.Completed, document.CurrentResult?.State); Assert.Equal(2, document.CurrentResult?.FileCount);
    }

    private sealed class Writer(Func<PackPlan, Stream, IProgress<PackProgress>?, CancellationToken, Task> action) : IArchiveWriter
    { public Task WriteAsync(PackPlan plan, Stream output, IProgress<PackProgress>? progress, CancellationToken cancellationToken, PackSecret? secret = null) => action(plan, output, progress, cancellationToken); }
}

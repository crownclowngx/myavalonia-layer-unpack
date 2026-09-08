using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.Threading;
using LayerUnpackPlugin.Features.Browse;
using LayerUnpackPlugin.Features.Pack;
using LayerUnpackPlugin.Features.Unpack;
using LayerUnpackPlugin.Headless.Application;
using LayerUnpackPlugin.Headless.Contracts;
using LayerUnpackPlugin.Headless.Tests;
using LayerUnpackPlugin.Standalone;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MyAvaloniaManagement.PluginSdk;
using Xunit;

namespace LayerUnpackPlugin.Tests;

public sealed class BrowseDocumentTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static BrowseDocument Create(TestLifetime lifetime, IArchiveBrowseService? service = null) => new(service ?? new ArchiveBrowseService(), new UnpackService(), lifetime);

    [AvaloniaFact]
    public async Task 默认浏览搜索目录选择与一层提取形成闭环()
    {
        using var w = new TestWorkspace(); using var life = new TestLifetime(); await using var document = Create(life);
        await document.InitializeAsync(new NewDocumentActivation("查看资料"), Token); Assert.Equal("查看资料", document.Presentation.Title);
        Assert.False(document.ExtractCommand.CanExecute(null)); Assert.False(document.PasswordExpanded);
        await document.OpenPathAsync(w.Zip("source.zip", ("a/one", [1]), ("a/two", [2]), ("other", [3])));
        Assert.Equal(w.FilePath("提取结果"), document.OutputDirectory); Assert.False(Directory.Exists(document.OutputDirectory));
        document.SearchText = "a"; document.Rows.Single(r => r.Entry.Path == "a").IsSelected = true;
        Assert.Equal(2, document.SelectedCount); document.SearchText = "other"; Assert.Equal(2, document.SelectedCount);
        Assert.True(document.ExtractCommand.CanExecute(null)); await document.ExtractCommand.ExecuteAsync(null);
        Assert.Equal(BrowseExtractState.Completed, document.CurrentResult?.State); Assert.True(document.HasOutputs);
        Assert.Equal(new[] { "a/one", "a/two" }, document.CurrentResult!.Files);
        var output = document.Outputs[0]; await document.ClearCommand.ExecuteAsync(null);
        Assert.Empty(document.Rows); Assert.Empty(document.Outputs); Assert.Empty(document.SourcePath); Assert.False(document.ExtractCommand.CanExecute(null));
        Assert.True(Directory.Exists(output));
    }

    [AvaloniaFact]
    public async Task 部分选择与分页重建保持三态且清除选择可见生效()
    {
        using var w = new TestWorkspace(); using var life = new TestLifetime(); await using var document = Create(life);
        await document.OpenPathAsync(w.Zip("many.zip", Enumerable.Range(0, 410).Select(i => ($"a/{i:D3}", new byte[] { 1 })).ToArray()));
        Assert.Equal(200, document.Rows.Count); document.Rows.Single(r => r.Entry.Path == "a").IsSelected = true;
        Assert.Equal(410, document.SelectedCount); document.NextPageCommand.Execute(null); document.Rows[0].IsSelected = false;
        Assert.Equal(409, document.SelectedCount); document.PreviousPageCommand.Execute(null);
        Assert.Null(document.Rows.Single(r => r.Entry.Path == "a").IsSelected);
        document.SearchText = "不存在"; Assert.Empty(document.Rows); Assert.Equal(409, document.SelectedCount);
        document.ClearSelectionCommand.Execute(null); Assert.Equal(0, document.SelectedCount); Assert.False(document.ExtractCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public async Task 加密目录可见就地补密字段及时清除且不序列化秘密()
    {
        using var w = new TestWorkspace(); using var life = new TestLifetime(); await using var document = Create(life);
        await document.OpenPathAsync(w.CopyFixture("Zip.deflate.pkware.zip"));
        document.Rows.First(r => !r.Entry.IsDirectory).IsSelected = true; Assert.True(document.PasswordExpanded);
        document.PasswordText = "private-sentinel";
        Assert.DoesNotContain("private-sentinel", JsonSerializer.Serialize(document));
        await document.ExtractCommand.ExecuteAsync(null);
        Assert.Equal(UnpackError.PasswordRequiredOrInvalid, document.CurrentResult?.Error?.Code); Assert.Empty(document.PasswordText);
        document.PasswordText = "12345678"; await document.ExtractCommand.ExecuteAsync(null);
        Assert.Equal(BrowseExtractState.Completed, document.CurrentResult?.State); Assert.Empty(document.PasswordText);
        document.PasswordText = "unconsumed"; document.SourcePath = w.FilePath("new.zip"); Assert.Empty(document.PasswordText); Assert.Empty(document.Rows);
    }

    [AvaloniaFact]
    public async Task 源替换后清空选择并要求重新加载手动输出不被覆盖()
    {
        using var w = new TestWorkspace(); using var life = new TestLifetime(); await using var document = Create(life);
        var path = w.Zip("source.zip", ("one", [1])); await document.OpenPathAsync(path);
        document.ChooseOutputDirectory(w.Output); document.Rows[0].IsSelected = true; File.Delete(path); w.Zip("source.zip", ("two", [2]));
        await document.ExtractCommand.ExecuteAsync(null); Assert.Equal(UnpackError.InputChanged, document.CurrentResult?.Error?.Code);
        Assert.Equal(0, document.SelectedCount); Assert.False(document.ExtractCommand.CanExecute(null)); Assert.Contains("重新加载", document.Message);
        await document.LoadCommand.ExecuteAsync(null); Assert.Equal(w.Output, document.OutputDirectory); Assert.Equal("two", Assert.Single(document.Rows).Entry.Path);
    }

    [AvaloniaFact]
    public async Task 全部解压转入真实原任务并展示来源层数而不自动运行或继承秘密()
    {
        using var w = new TestWorkspace(); using var life = new TestLifetime(); await using var document = Create(life);
        var path = w.Zip("source.zip", ("data", [1])); await document.OpenPathAsync(path); document.PasswordText = "private-secret";
        await document.PrepareUnpackCommand.ExecuteAsync(null);
        Assert.True(document.ShowUnpackTask); var unpack = Assert.IsType<UnpackDocument>(document.UnpackTask);
        Assert.Equal(path, Assert.Single(unpack.Inputs).Path); Assert.Equal(1, unpack.MaxDepth); Assert.Empty(unpack.PasswordText);
        Assert.Null(unpack.CurrentResult); Assert.False(Directory.Exists(document.OutputDirectory));
        unpack.IsRecursive = true; Assert.Equal(2, unpack.MaxDepth);
        await unpack.StartCommand.ExecuteAsync(null); Assert.Equal(BatchState.Completed, unpack.CurrentResult?.State);
        document.ReturnToBrowseCommand.Execute(null); Assert.False(document.ShowUnpackTask); Assert.True(document.HasCatalog);
        await document.ClearCommand.ExecuteAsync(null); Assert.True(unpack.IsClosed); Assert.Null(document.UnpackTask);
    }

    [AvaloniaFact]
    public async Task 准备全部解压期间取消必须传给真实输入扫描()
    {
        using var w = new TestWorkspace(); using var life = new TestLifetime(); await using var document = Create(life);
        document.SourcePath = w.Zip("source.zip", ("data", [1]));
        var prepare = document.PrepareUnpackCommand.ExecuteAsync(null);
        Assert.True(document.IsBusy); Assert.True(document.UnpackTask!.IsBusy);
        document.CancelCommand.Execute(null); Assert.True(document.UnpackTask.IsCancelling);
        await prepare; Assert.False(document.IsBusy); Assert.False(document.UnpackTask.IsBusy);
    }

    [AvaloniaFact]
    public async Task 不支持浏览时仍可准备全部解压且重选来源不沿用旧输入()
    {
        using var w = new TestWorkspace(); using var life = new TestLifetime(); await using var document = Create(life);
        var unsupported = w.FilePath("archive.rar"); File.WriteAllBytes(unsupported, "Rar!\x1a\x07"u8.ToArray());
        await document.OpenPathAsync(unsupported); Assert.False(document.HasCatalog); Assert.Contains("全部解压", document.Message);
        await document.PrepareUnpackCommand.ExecuteAsync(null); Assert.Equal(unsupported, Assert.Single(document.UnpackTask!.Inputs).Path);
        document.ReturnToBrowseCommand.Execute(null); var first = document.UnpackTask;
        var zip = w.Zip("new.zip", ("data", [1])); await document.OpenPathAsync(zip); await document.PrepareUnpackCommand.ExecuteAsync(null);
        Assert.True(first.IsClosed); Assert.NotSame(first, document.UnpackTask); Assert.Equal(zip, Assert.Single(document.UnpackTask!.Inputs).Path);
    }

    [AvaloniaFact]
    public async Task 关闭同步排空真实提取清理且迟到进度不能改写页面()
    {
        using var w = new TestWorkspace(); using var life = new TestLifetime(); var service = new PausingService();
        await using var document = Create(life, service); await document.OpenPathAsync(w.Zip("large.zip", ("data", new byte[5 * 1024 * 1024])));
        document.Rows[0].IsSelected = true; var work = document.ExtractCommand.ExecuteAsync(null);
        await service.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token); Assert.False(document.CanEdit); Assert.True(document.CancelCommand.CanExecute(null));
        var originalOutput = document.OutputDirectory; document.OutputDirectory = w.FilePath("next");
        life.Close(); document.Dispose(); await work; var summary = document.Summary;
        service.Captured!.Report(new(BrowseOperation.Extracting, 999, 999, 999)); Dispatcher.UIThread.RunJobs();
        Assert.Equal(summary, document.Summary); Assert.True(document.IsClosed); Assert.True(service.Stopped);
        Assert.Empty(Directory.GetFileSystemEntries(originalOutput)); Assert.False(Directory.Exists(document.OutputDirectory));
    }

    [AvaloniaFact]
    public async Task 两个浏览Scope密码选择与取消互不影响()
    {
        using var w = new TestWorkspace(); var services = StandaloneServices.Create(); var pausing = new PausingService();
        services.Replace(ServiceDescriptor.Scoped<IArchiveBrowseService>(_ => pausing));
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        using var a = provider.CreateScope(); using var b = provider.CreateScope();
        var first = a.ServiceProvider.GetRequiredService<BrowseDocument>(); var second = b.ServiceProvider.GetRequiredService<BrowseDocument>();
        await first.OpenPathAsync(w.Zip("first.zip", ("data", new byte[5 * 1024 * 1024])));
        await second.OpenPathAsync(w.Zip("second.zip", ("other", [2])));
        first.Rows[0].IsSelected = true; first.PasswordText = "first-only"; Assert.Equal(0, second.SelectedCount); Assert.Empty(second.PasswordText);
        var work = first.ExtractCommand.ExecuteAsync(null); await pausing.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        a.Dispose(); await work; Assert.True(first.IsClosed); Assert.False(second.IsClosed); Assert.True(second.HasCatalog);
        Assert.True(second.LoadCommand.CanExecute(null));
    }

    [AvaloniaTheory]
    [InlineData(false, 900, 760)]
    [InlineData(true, 900, 760)]
    [InlineData(false, 640, 520)]
    [InlineData(true, 640, 520)]
    public async Task 浏览默认成功与问题状态在深浅主题窄窗口可操作(bool dark, int width, int height)
    {
        using var w = new TestWorkspace(); using var life = new TestLifetime(); await using var document = Create(life);
        var view = new BrowseView { DataContext = document };
        var window = new Window { Content = view, Width = width, Height = height, RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light };
        var destination = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/ui/G0010")); Directory.CreateDirectory(destination);
        void Capture(string state)
        {
            Dispatcher.UIThread.RunJobs(); var extract = view.FindControl<Button>("ExtractButton")!;
            Assert.Same(document.ExtractCommand, extract.Command); var origin = extract.TranslatePoint(default, window)!.Value;
            Assert.InRange(origin.X, 0, window.ClientSize.Width - extract.Bounds.Width); Assert.InRange(origin.Y, 0, window.ClientSize.Height - extract.Bounds.Height);
            using var frame = window.CaptureRenderedFrame(); Assert.NotNull(frame);
            frame.Save(Path.Combine(destination, $"browse-{state}-{(dark ? "dark" : "light")}-{width}x{height}.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        }
        try
        {
            window.Show(); Capture("empty");
            await document.OpenPathAsync(w.Zip("资料.zip", ("项目资料/中文说明.txt", "中文内容"u8.ToArray()), ("other", [2])));
            document.Rows.Single(r => r.Entry.IsDirectory).IsSelected = true; Capture("ready");
            var search = view.FindControl<TextBox>("SearchEditor")!; Assert.True(search.Focus());
            window.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null); Assert.NotSame(search, window.FocusManager!.GetFocusedElement());
            await document.ExtractCommand.ExecuteAsync(null); Assert.Equal(BrowseExtractState.Completed, document.CurrentResult?.State); Capture("completed");
            var button = view.FindControl<Button>("OpenResultButton")!; Assert.True(button.IsVisible);
            Assert.InRange(button.TranslatePoint(default, window)!.Value.Y, 0, window.ClientSize.Height - button.Bounds.Height);
            await document.OpenPathAsync(w.CopyFixture("Zip.deflate.pkware.zip")); document.Rows.First(r => !r.Entry.IsDirectory).IsSelected = true;
            await document.ExtractCommand.ExecuteAsync(null); Assert.True(document.PasswordExpanded); Capture("password-required");
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Standalone第三Scope关闭会排空浏览并释放三页()
    {
        using var w = new TestWorkspace(); var services = StandaloneServices.Create(); var service = new PausingService();
        services.Replace(ServiceDescriptor.Singleton<IArchiveBrowseService>(service));
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        var window = new MainWindow(provider); var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        window.Closed += (_, _) => closed.TrySetResult();
        try
        {
            window.Show(); window.FindControl<TabControl>("TaskTabs")!.SelectedIndex = 2;
            var browser = Assert.IsType<BrowseDocument>(Assert.IsType<BrowseView>(window.FindControl<ContentControl>("BrowsePreviewHost")!.Content).DataContext);
            var pack = Assert.IsType<PackDocument>(Assert.IsType<PackView>(window.FindControl<ContentControl>("PackPreviewHost")!.Content).DataContext);
            var unpack = Assert.IsType<UnpackDocument>(Assert.IsType<UnpackView>(window.FindControl<ContentControl>("PreviewHost")!.Content).DataContext);
            await browser.OpenPathAsync(w.Zip("large.zip", ("data", new byte[5 * 1024 * 1024]))); browser.Rows[0].IsSelected = true;
            var work = browser.ExtractCommand.ExecuteAsync(null); await service.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token); window.Close();
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(10), Token); await work;
            Assert.True(browser.IsClosed); Assert.True(pack.IsClosed); Assert.True(unpack.IsClosed); Assert.True(service.Stopped);
            Assert.Null(window.FindControl<ContentControl>("BrowsePreviewHost")!.Content);
        }
        finally { window.Close(); }
    }

    /// <summary>装饰真实用例，只在已有写入进度处暂停，仍由真实提取事务负责清理，避免伪造取消证据。</summary>
    private sealed class PausingService : IArchiveBrowseService
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IProgress<BrowseProgress>? Captured { get; private set; }
        public bool Stopped { get; private set; }
        public async Task<IArchiveBrowseSession> OpenAsync(BrowseRequest request, IProgress<BrowseProgress>? progress, CancellationToken cancellationToken) =>
            new Session(await new ArchiveBrowseService().OpenAsync(request, progress, cancellationToken), this);
        private sealed class Session(IArchiveBrowseSession inner, PausingService owner) : IArchiveBrowseSession
        {
            public ArchiveCatalog Catalog => inner.Catalog;
            public bool IsInvalidated => inner.IsInvalidated;
            public long ReadBytes => inner.ReadBytes;
            public long ExpandedBytes => inner.ExpandedBytes;
            public Task<BrowseExtractResult> ExtractAsync(BrowseSelection selection, string outputDirectory, string? password, IProgress<BrowseProgress>? progress, CancellationToken cancellationToken)
            {
                owner.Captured = progress;
                return inner.ExtractAsync(selection, outputDirectory, password, new Callback(p =>
                {
                    progress?.Report(p);
                    if (p.Operation != BrowseOperation.Extracting) return;
                    owner.Entered.TrySetResult(); cancellationToken.WaitHandle.WaitOne(); owner.Stopped = true;
                }), cancellationToken);
            }
            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }
        private sealed class Callback(Action<BrowseProgress> action) : IProgress<BrowseProgress> { public void Report(BrowseProgress value) => action(value); }
    }
}

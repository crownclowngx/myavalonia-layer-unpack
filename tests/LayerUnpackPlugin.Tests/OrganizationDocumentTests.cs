using System.Diagnostics;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LayerUnpackPlugin.Features.Organize;
using LayerUnpackPlugin.Features.Unpack;
using LayerUnpackPlugin.Headless.Application;
using LayerUnpackPlugin.Headless.Contracts;
using LayerUnpackPlugin.Headless.Infrastructure;
using LayerUnpackPlugin.Headless.Tests;
using LayerUnpackPlugin.Standalone;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace LayerUnpackPlugin.Tests;

public sealed class OrganizationDocumentTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [AvaloniaFact]
    public async Task 整理仅在成功解压结果中显式进入且默认全文件不压平()
    {
        using var w = new TestWorkspace(); using var life = new TestLifetime();
        await using var document = new UnpackDocument(new UnpackService(), life) { OutputDirectory = w.Output };
        Assert.False(document.OrganizeCommand.CanExecute(null)); Assert.Null(document.OrganizationTask);
        await document.AddPathsAsync([w.Zip("source.zip", ("wrap/data.pdf", [1]))]);
        await document.StartCommand.ExecuteAsync(null); Assert.Null(document.OrganizationTask);
        var sourceResult = document.CurrentResult; document.OrganizeCommand.Execute(null);
        var task = Assert.IsType<OrganizationDocument>(document.OrganizationTask);
        Assert.True(document.ShowOrganizationTask); Assert.False(document.CanEdit); Assert.False(document.StartCommand.CanExecute(null));
        Assert.Equal(OrganizationFileTypes.All, task.SelectedFileTypes.Types); Assert.False(task.FlattenWrappingDirectories);
        Assert.False(task.RulesExpanded); Assert.False(task.DetailsExpanded); Assert.Null(task.Plan); Assert.False(task.ExecuteCommand.CanExecute(null));
        await task.PreviewCommand.ExecuteAsync(null); Assert.NotNull(task.Plan); Assert.False(Directory.Exists(task.Plan.OutputDirectory));
        await task.ExecuteCommand.ExecuteAsync(null); Assert.Equal(OrganizationState.Completed, task.Result?.State);
        Assert.Same(sourceResult, document.CurrentResult); Assert.True(task.CanOpenResult);
        document.ReturnToUnpackCommand.Execute(null); Assert.False(document.ShowOrganizationTask); Assert.True(document.CanEdit);
        document.OrganizeCommand.Execute(null); Assert.Same(task, document.OrganizationTask); Assert.True(task.CanOpenResult);
    }

    [AvaloniaFact]
    public async Task 规则输出改变使映射失效无匹配时不能开始整理()
    {
        using var w = new TestWorkspace(); using var life = new TestLifetime();
        await using var parent = await CreateAsync(w, life);
        var task = parent.OrganizationTask!;
        await task.PreviewCommand.ExecuteAsync(null); Assert.True(task.ExecuteCommand.CanExecute(null));
        task.FlattenWrappingDirectories = true; Assert.Null(task.Plan);
        await task.PreviewCommand.ExecuteAsync(null); Assert.True(task.ExecuteCommand.CanExecute(null));
        task.OutputParent = w.FilePath("changed"); Assert.Null(task.Plan);
        task.SelectedFileTypes = OrganizationDocument.FileTypes.Single(t => t.Types == OrganizationFileTypes.Images);
        await task.PreviewCommand.ExecuteAsync(null);
        Assert.NotNull(task.Plan); Assert.Equal(0, task.Plan.FileCount); Assert.Contains("没有匹配文件", task.Message);
        Assert.False(task.ExecuteCommand.CanExecute(null)); Assert.False(Directory.Exists(task.OutputParent));
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 来源变化或目标占用提供诊断并强制重新预览(bool occupied)
    {
        using var w = new TestWorkspace(); using var life = new TestLifetime(); await using var parent = await CreateAsync(w, life);
        var task = parent.OrganizationTask!; await task.PreviewCommand.ExecuteAsync(null);
        var plan = task.Plan!;
        if (occupied) Directory.CreateDirectory(plan.OutputDirectory);
        else File.WriteAllBytes(Assert.Single(plan.Mappings, m => !m.IsDirectory).SourcePath, [4]);
        await task.ExecuteCommand.ExecuteAsync(null);
        Assert.Equal(OrganizationState.Failed, task.Result?.State); Assert.Null(task.Plan); Assert.False(task.ExecuteCommand.CanExecute(null));
        Assert.Contains(occupied ? "重新生成预览" : "重新解压", task.Message); Assert.False(task.CanOpenResult);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 新批次或清空释放旧整理页并保留磁盘结果(bool clear)
    {
        using var w = new TestWorkspace(); using var life = new TestLifetime(); await using var parent = await CreateAsync(w, life);
        var task = parent.OrganizationTask!; await task.PreviewCommand.ExecuteAsync(null); await task.ExecuteCommand.ExecuteAsync(null);
        var output = task.Result!.OutputDirectory;
        parent.ReturnToUnpackCommand.Execute(null);
        if (clear) await parent.ClearCommand.ExecuteAsync(null); else await parent.StartCommand.ExecuteAsync(null);
        Assert.True(task.IsClosed); Assert.Null(parent.OrganizationTask); Assert.True(Directory.Exists(output));
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 用户取消或父Scope同步关闭均排空真实复制并隔离迟到回调(bool closeScope)
    {
        using var w = new TestWorkspace(); var services = StandaloneServices.Create(); var copier = new PausingCopier();
        services.Replace(ServiceDescriptor.Singleton<IOrganizationCopier>(copier));
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        using var firstScope = provider.CreateScope(); using var secondScope = provider.CreateScope();
        var first = firstScope.ServiceProvider.GetRequiredService<UnpackDocument>();
        var second = secondScope.ServiceProvider.GetRequiredService<UnpackDocument>();
        first.OutputDirectory = w.Output;
        await first.AddPathsAsync([w.Zip("source.zip", ("data.pdf", new byte[300_000]))]); await first.StartCommand.ExecuteAsync(null);
        first.OrganizeCommand.Execute(null); var task = first.OrganizationTask!; await task.PreviewCommand.ExecuteAsync(null);
        var output = task.Plan!.OutputDirectory;
        var work = task.ExecuteCommand.ExecuteAsync(null);
        await copier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        Assert.True(task.IsBusy); Assert.False(task.CanEdit); Assert.False(first.ReturnToUnpackCommand.CanExecute(null));
        if (closeScope) firstScope.Dispose(); else task.CancelCommand.Execute(null);
        await work; Dispatcher.UIThread.RunJobs();
        Assert.True(copier.Stopped); Assert.False(Directory.Exists(output)); Assert.Empty(Directory.GetDirectories(w.Output, ".layer-unpack-*"));
        Assert.False(second.IsClosed); Assert.Null(second.OrganizationTask);
        if (closeScope) { Assert.True(first.IsClosed); Assert.True(task.IsClosed); }
        else { Assert.Equal(OrganizationState.Cancelled, task.Result?.State); Assert.False(task.IsBusy); Assert.True(first.ReturnToUnpackCommand.CanExecute(null)); }
    }

    [AvaloniaFact]
    public async Task 大量映射每页一百项可访问尾页且界面调度保持响应()
    {
        using var w = new TestWorkspace(); using var life = new TestLifetime();
        await using var parent = new UnpackDocument(new UnpackService(), life) { OutputDirectory = w.Output };
        await parent.AddPathsAsync([w.Zip("many.zip", Enumerable.Range(0, 1200).Select(i => ($"wrap/{i:D4}.pdf", new byte[64])).ToArray())]);
        await parent.StartCommand.ExecuteAsync(null); parent.OrganizeCommand.Execute(null);
        var task = parent.OrganizationTask!; var work = task.PreviewCommand.ExecuteAsync(null);
        var tick = Stopwatch.StartNew(); var posted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.UIThread.Post(() => posted.TrySetResult()); await posted.Task; tick.Stop();
        await work; Assert.Equal(1200, task.Plan?.FileCount); Assert.Equal(OrganizationDocument.PageSize, task.Rows.Count);
        var paging = Stopwatch.StartNew();
        while (task.NextPageCommand.CanExecute(null)) task.NextPageCommand.Execute(null);
        paging.Stop(); Assert.Contains(task.Rows, r => r.Target.EndsWith("1199.pdf", StringComparison.Ordinal));
        task.ConflictsOnly = true; Assert.Empty(task.Rows); Assert.False(task.PreviousPageCommand.CanExecute(null));
        var directory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/performance/G0011")); Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "ui-paging.json"), JsonSerializer.Serialize(new { fileCount = 1200, pageSize = OrganizationDocument.PageSize, dispatcherMilliseconds = tick.Elapsed.TotalMilliseconds, allPageTurnsMilliseconds = paging.Elapsed.TotalMilliseconds }, new JsonSerializerOptions { WriteIndented = true }));
    }

    [AvaloniaTheory]
    [InlineData(false, 640, 520)]
    [InlineData(true, 640, 520)]
    [InlineData(false, 900, 760)]
    [InlineData(true, 900, 760)]
    public async Task 整理预览运行结果与异常在深浅主题窄窗口可用(bool dark, int width, int height)
    {
        using var w = new TestWorkspace(); using var life = new TestLifetime(); await using var parent = await CreateAsync(w, life);
        var task = parent.OrganizationTask!; var view = new UnpackView { DataContext = parent };
        var window = new Window { Content = view, Width = width, Height = height, RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light };
        var destination = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/ui/G0011")); Directory.CreateDirectory(destination);
        var organize = view.GetVisualDescendants().OfType<OrganizationView>().SingleOrDefault();
        void Capture(string state)
        {
            Dispatcher.UIThread.RunJobs(); organize ??= view.GetVisualDescendants().OfType<OrganizationView>().Single();
            var button = organize.FindControl<Button>(task.HasPlan ? "ExecuteButton" : "PreviewButton")!;
            var origin = button.TranslatePoint(default, window)!.Value;
            Assert.InRange(origin.X, 0, window.ClientSize.Width - button.Bounds.Width); Assert.InRange(origin.Y, 0, window.ClientSize.Height - button.Bounds.Height);
            using var frame = window.CaptureRenderedFrame(); Assert.NotNull(frame);
            frame.Save(Path.Combine(destination, $"organize-{state}-{(dark ? "dark" : "light")}-{width}x{height}.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        }
        try
        {
            window.Show(); Capture("ready");
            Assert.Same(task.PreviewCommand, organize!.FindControl<Button>("PreviewButton")!.Command);
            var editor = organize!.FindControl<TextBox>("OutputEditor")!; Assert.True(editor.Focus());
            window.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null); Assert.NotSame(editor, window.FocusManager!.GetFocusedElement());
            await task.PreviewCommand.ExecuteAsync(null); task.DetailsExpanded = true; Capture("preview");
            await task.ExecuteCommand.ExecuteAsync(null); Assert.Equal(OrganizationState.Completed, task.Result?.State); Capture("completed");
            var result = organize!.FindControl<Button>("OpenResultButton")!; Assert.True(result.IsVisible);
            Assert.InRange(result.TranslatePoint(default, window)!.Value.Y, 0, window.ClientSize.Height - result.Bounds.Height);
            await task.PreviewCommand.ExecuteAsync(null); File.Delete(Assert.Single(task.Plan!.Mappings, m => !m.IsDirectory).SourcePath);
            await task.ExecuteCommand.ExecuteAsync(null); Capture("source-changed");
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task 关闭预览会取消并排空且晚到的进度不能覆盖成功终态()
    {
        using var w = new TestWorkspace(); using var life = new TestLifetime();
        var input = await ExtractAsync(w, w.Zip("source.zip", ("a.pdf", [1])));
        var observing = new ObservingService();
        await using var task = new OrganizationDocument(observing, input, w.Output, life.ClosingToken);
        await task.PreviewCommand.ExecuteAsync(null); await task.ExecuteCommand.ExecuteAsync(null);
        var summary = task.Summary; var output = task.Result!.OutputDirectory;
        observing.Progress!.Report(new("过期进度", 999, 999, 999, 999, "old")); Dispatcher.UIThread.RunJobs();
        Assert.Equal(summary, task.Summary); Assert.Equal(output, task.OutputPath);
        observing.PausePlanning = true;
        var planning = task.PreviewCommand.ExecuteAsync(null);
        await observing.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        life.Close(); task.Dispose(); await planning;
        Assert.True(task.IsClosed); Assert.True(observing.Stopped); Assert.True(Directory.Exists(output));
    }

    [AvaloniaFact]
    public async Task 全文件整理同时保留只有空目录的其他来源()
    {
        using var w = new TestWorkspace(); using var life = new TestLifetime();
        var input = await ExtractAsync(w, w.Zip("empty.zip", ("empty/", [])), w.Zip("data.zip", ("a.pdf", [1])));
        await using var task = new OrganizationDocument(new OrganizationService(), input, w.Output, life.ClosingToken);
        await task.PreviewCommand.ExecuteAsync(null); await task.ExecuteCommand.ExecuteAsync(null);
        Assert.Equal(OrganizationState.Completed, task.Result?.State);
        Assert.True(Directory.Exists(Path.Combine(task.Result!.OutputDirectory!, "empty/empty")));
    }

    private sealed class ObservingService : IOrganizationService
    {
        private readonly OrganizationService _inner = new();
        public IProgress<OrganizationProgress>? Progress { get; private set; }
        public bool PausePlanning { get; set; }
        public bool Stopped { get; private set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<OrganizationPlan> PlanAsync(UnpackResult result, string outputParent, OrganizationRules rules, CancellationToken cancellationToken)
        {
            if (PausePlanning)
            {
                Entered.TrySetResult();
                try { await Task.Delay(Timeout.Infinite, cancellationToken); }
                finally { Stopped = true; }
            }
            return await _inner.PlanAsync(result, outputParent, rules, cancellationToken);
        }
        public Task<OrganizationResult> ExecuteAsync(OrganizationPlan plan, IProgress<OrganizationProgress>? progress, CancellationToken cancellationToken)
        { Progress = progress; return _inner.ExecuteAsync(plan, progress, cancellationToken); }
    }

    private static async Task<UnpackResult> ExtractAsync(TestWorkspace w, params string[] archives)
    {
        await using var session = new UnpackService().CreateSession(new(archives, w.Output));
        var result = await session.ExecuteAsync(cancellationToken: Token);
        Assert.Equal(BatchState.Completed, result.State); return result;
    }

    private static async Task<UnpackDocument> CreateAsync(TestWorkspace w, TestLifetime life)
    {
        var parent = new UnpackDocument(new UnpackService(), life) { OutputDirectory = w.Output };
        await parent.AddPathsAsync([w.Zip("资料.zip", ("外层/资料/说明.pdf", "真实中文资料"u8.ToArray()), ("空目录/", []))]);
        await parent.StartCommand.ExecuteAsync(null); parent.OrganizeCommand.Execute(null); return parent;
    }
    private sealed class PausingCopier : IOrganizationCopier
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Stopped { get; private set; }
        public async Task CopyAsync(OrganizationMapping mapping, string destination, Action<long> progress, CancellationToken cancellationToken)
        {
            try
            {
                await new OrganizationCopier().CopyAsync(mapping, destination, bytes =>
                {
                    progress(bytes); Entered.TrySetResult();
                    cancellationToken.WaitHandle.WaitOne(TimeSpan.FromSeconds(10));
                }, cancellationToken);
            }
            finally { Stopped = true; }
        }
    }
}

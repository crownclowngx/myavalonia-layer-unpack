using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.Threading;
using LayerUnpackPlugin.Features.Organize;
using LayerUnpackPlugin.Features.Repack;
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

public sealed class RepackDocumentTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    [AvaloniaFact]
    public async Task 转换入口只带来源不继承密码且显式开始才写入()
    {
        using var w = new TestWorkspace(); using var life = new TestLifetime();
        await using var parent = new UnpackDocument(new UnpackService(), life) { OutputDirectory = w.Output, PasswordText = "private-source-sentinel" };
        Assert.False(parent.ConvertToZipCommand.CanExecute(null));
        await parent.AddPathsAsync([w.Zip("source.zip", ("a.txt", [1]))]);
        await parent.ConvertToZipCommand.ExecuteAsync(null); var task = Assert.IsType<RepackDocument>(parent.RepackTask);
        Assert.True(parent.ShowRepackTask); Assert.False(parent.CanEdit); Assert.False(parent.ShowMainTask);
        Assert.True(task.IsConversion); Assert.False(task.ExpandInternalArchives); Assert.False(task.EncryptionEnabled);
        Assert.Empty(task.SourcePasswordText); Assert.Empty(task.TargetPassword); Assert.False(Directory.Exists(w.Output));
        Assert.Contains("内部压缩包作为普通文件保留", task.ContentRule);
        await task.StartCommand.ExecuteAsync(null); Assert.Equal(RepackState.Completed, task.Result?.State); Assert.Single(task.Outputs);
        parent.ReturnFromRepackCommand.Execute(null); Assert.True(parent.CanEdit); Assert.True(parent.ShowMainTask);
        var output = task.Outputs.Single(); await parent.ClearCommand.ExecuteAsync(null);
        Assert.True(task.IsClosed); Assert.Null(parent.RepackTask); Assert.True(File.Exists(output));
    }

    [AvaloniaFact]
    public async Task 解压结果入口检查清单后开始且修改参数使计划失效()
    {
        using var w = new TestWorkspace(); using var life = new TestLifetime();
        await using var parent = await Parent(w, life);
        var source = parent.CurrentResult;
        await parent.PackResultsCommand.ExecuteAsync(null); var task = parent.RepackTask!;
        Assert.False(task.IsConversion); Assert.Null(task.Plan); Assert.False(task.StartCommand.CanExecute(null));
        await task.PreviewCommand.ExecuteAsync(null); Assert.NotNull(task.Plan); Assert.True(task.StartCommand.CanExecute(null));
        Assert.Empty(Directory.GetFiles(w.Output, "*.zip"));
        task.SeparateArchives = true; Assert.Null(task.Plan); Assert.False(task.StartCommand.CanExecute(null));
        await task.PreviewCommand.ExecuteAsync(null); await task.StartCommand.ExecuteAsync(null);
        Assert.Equal(RepackState.Completed, task.Result?.State); Assert.Same(source, parent.CurrentResult); Assert.Null(task.Plan);
        Assert.Single(task.Outputs); Assert.NotEmpty(task.Results);
    }

    [AvaloniaFact]
    public async Task 整理结果入口只带已提交清单并可返回原结果()
    {
        using var w = new TestWorkspace(); using var life = new TestLifetime(); await using var parent = await Parent(w, life);
        parent.OrganizeCommand.Execute(null); var organization = parent.OrganizationTask!;
        Assert.False(organization.PackResultCommand.CanExecute(null));
        await organization.PreviewCommand.ExecuteAsync(null); await organization.ExecuteCommand.ExecuteAsync(null);
        var previous = organization.Result;
        organization.PackResultCommand.Execute(null); var task = organization.RepackTask!;
        Assert.True(organization.ShowRepackTask); Assert.False(task.IsConversion); Assert.Null(task.Plan);
        Assert.False(organization.CanEdit); Assert.Empty(Directory.GetFiles(w.Output, "*.zip"));
        await task.PreviewCommand.ExecuteAsync(null); await task.StartCommand.ExecuteAsync(null);
        Assert.Equal(RepackState.Completed, task.Result?.State); Assert.Same(previous, organization.Result);
        organization.ReturnFromRepackCommand.Execute(null); Assert.True(organization.CanEdit);
        organization.PackResultCommand.Execute(null); Assert.Same(task, organization.RepackTask);
    }

    [AvaloniaFact]
    public async Task 源变化提示重新检查并保持用户已解压内容()
    {
        using var w = new TestWorkspace(); using var life = new TestLifetime(); await using var parent = await Parent(w, life);
        await parent.PackResultsCommand.ExecuteAsync(null); var task = parent.RepackTask!;
        await task.PreviewCommand.ExecuteAsync(null);
        File.WriteAllText(Path.Combine(parent.CurrentResult!.Nodes[0].OutputDirectory!, "history.txt"), "history");
        await task.StartCommand.ExecuteAsync(null);
        Assert.Equal(RepackState.Failed, task.Result?.State); Assert.Null(task.Plan); Assert.Empty(task.Outputs);
        Assert.Contains("来源已改变", string.Join("\n", task.Results));
    }

    [AvaloniaFact]
    public async Task 目标密码需一致且敏感字段不序列化执行后立即清除()
    {
        using var w = new TestWorkspace();
        await using var task = new RepackDocument(new RepackService(), [w.Zip("source.zip", ("a", [1]))], w.Output, Token)
        { EncryptionEnabled = true, SourcePasswordText = "private-source", TargetPassword = "private-target", ConfirmPassword = "different" };
        var json = JsonSerializer.Serialize(task); Assert.DoesNotContain("private-source", json); Assert.DoesNotContain("private-target", json);
        await task.StartCommand.ExecuteAsync(null); Assert.Contains("不一致", task.Message); Assert.False(Directory.Exists(w.Output));
        Assert.Empty(task.TargetPassword); Assert.Empty(task.SourcePasswordText);
        task.TargetPassword = task.ConfirmPassword = "private-target";
        await task.StartCommand.ExecuteAsync(null); Assert.Equal(RepackState.Completed, task.Result?.State);
        Assert.Empty(task.TargetPassword); Assert.Empty(task.ConfirmPassword); Assert.DoesNotContain("private-target", string.Join("\n", task.Results));
    }

    [AvaloniaTheory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task 取消或父Scope关闭等待真实写包清理且隔离迟到回调(bool closeScope, bool organized)
    {
        using var w = new TestWorkspace(); var services = StandaloneServices.Create(); var writer = new PausingWriter();
        services.Replace(ServiceDescriptor.Singleton<IArchiveWriter>(writer));
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        using var firstScope = provider.CreateScope(); using var secondScope = provider.CreateScope();
        var parent = firstScope.ServiceProvider.GetRequiredService<UnpackDocument>();
        var other = secondScope.ServiceProvider.GetRequiredService<UnpackDocument>();
        parent.OutputDirectory = w.Output; await parent.AddPathsAsync([w.Zip("source.zip", ("data.pdf", new byte[300_000]))]);
        RepackDocument task;
        if (organized)
        {
            await parent.StartCommand.ExecuteAsync(null); parent.OrganizeCommand.Execute(null);
            await parent.OrganizationTask!.PreviewCommand.ExecuteAsync(null); await parent.OrganizationTask.ExecuteCommand.ExecuteAsync(null);
            parent.OrganizationTask.PackResultCommand.Execute(null); task = parent.OrganizationTask.RepackTask!;
            await task.PreviewCommand.ExecuteAsync(null);
        }
        else { await parent.ConvertToZipCommand.ExecuteAsync(null); task = parent.RepackTask!; }
        var command = task.StartCommand.ExecuteAsync(null);
        await writer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        Assert.True(task.IsBusy); Assert.False(task.CanEdit); Assert.False(task.StartCommand.CanExecute(null));
        Assert.False(organized ? parent.ReturnToUnpackCommand.CanExecute(null) : parent.ReturnFromRepackCommand.CanExecute(null));
        if (closeScope) firstScope.Dispose(); else task.CancelCommand.Execute(null);
        await command; Dispatcher.UIThread.RunJobs();
        Assert.True(writer.Stopped); Assert.Empty(Directory.GetFiles(w.Output, "*.zip")); Assert.Empty(Directory.GetFiles(w.Output, ".layer-pack-*"));
        Assert.Empty(Directory.GetDirectories(w.Output, ".layer-unpack-*")); Assert.False(other.IsClosed); Assert.Null(other.RepackTask);
        if (organized) Assert.True(Directory.Exists(parent.OrganizationTask!.Result!.OutputDirectory));
        if (closeScope) Assert.True(task.IsClosed); else Assert.Equal(RepackState.Cancelled, task.Result?.State);
        var summary = task.Summary;
        writer.Progress!.Report(new(PackState.Writing, "late", 999, 999, 999, 999)); Dispatcher.UIThread.RunJobs();
        Assert.Equal(summary, task.Summary);
    }

    [AvaloniaFact]
    public async Task 清单分页能检查全部映射且重新预览不写文件()
    {
        using var w = new TestWorkspace(); var input = await Extract(w,
            w.Zip("many.zip", Enumerable.Range(0, 240).Select(i => ($"{i:D3}.txt", new byte[] { 1 })).ToArray()));
        await using var task = new RepackDocument(new RepackService(), input, w.FilePath("repack"), Token);
        await task.PreviewCommand.ExecuteAsync(null); Assert.Equal(RepackDocument.PageSize, task.Rows.Count);
        while (task.NextPageCommand.CanExecute(null)) task.NextPageCommand.Execute(null);
        Assert.Contains(task.Rows, r => r.Contains("239.txt", StringComparison.Ordinal));
        Assert.False(Directory.Exists(w.FilePath("repack"))); Assert.True(task.PreviousPageCommand.CanExecute(null));
    }

    [AvaloniaTheory]
    [InlineData(false, 640, 520)]
    [InlineData(true, 640, 520)]
    [InlineData(false, 900, 760)]
    [InlineData(true, 900, 760)]
    public async Task 转换和结果打包在深浅主题窄窗口保持主要操作可见(bool dark, int width, int height)
    {
        using var w = new TestWorkspace(); using var life = new TestLifetime();
        var source = w.Zip("source.zip", ("资料/说明.pdf", "中文内容"u8.ToArray()), ("空目录/", []));
        await using var conversion = new RepackDocument(new RepackService(), [source], w.Output, Token);
        var view = new RepackView { DataContext = conversion };
        var window = new Window { Content = view, Width = width, Height = height, RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light };
        var destination = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/ui/G0012")); Directory.CreateDirectory(destination);
        void Capture(string state)
        {
            Dispatcher.UIThread.RunJobs(); var button = view.FindControl<Button>("StartButton")!;
            var point = button.TranslatePoint(default, window)!.Value;
            Assert.InRange(point.X, 0, window.ClientSize.Width - button.Bounds.Width); Assert.InRange(point.Y, 0, window.ClientSize.Height - button.Bounds.Height);
            using var frame = window.CaptureRenderedFrame(); Assert.NotNull(frame);
            frame.Save(Path.Combine(destination, $"repack-{state}-{(dark ? "dark" : "light")}-{width}x{height}.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        }
        try
        {
            window.Show(); Capture("ready");
            Assert.Same(conversion.StartCommand, view.FindControl<Button>("StartButton")!.Command);
            var editor = view.FindControl<TextBox>("OutputEditor")!; Assert.True(editor.Focus());
            window.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null); Assert.NotSame(editor, window.FocusManager!.GetFocusedElement());
            conversion.OptionsExpanded = true; conversion.ExpandInternalArchives = true;
            conversion.SelectedFileTypes = OrganizationDocument.FileTypes[1]; Capture("options");
            await conversion.StartCommand.ExecuteAsync(null); Assert.Equal(RepackState.Completed, conversion.Result?.State); Capture("completed");
            var open = view.FindControl<Button>("OpenOutputButton")!; Assert.True(open.IsVisible);
            Assert.InRange(open.TranslatePoint(default, window)!.Value.Y, 0, window.ClientSize.Height - open.Bounds.Height);
            var input = await Extract(w, source);
            await using var resultTask = new RepackDocument(new RepackService(), input, w.FilePath("result-pack"), Token);
            view.DataContext = resultTask; await resultTask.PreviewCommand.ExecuteAsync(null); Capture("preview");
            File.Delete(resultTask.Plan!.Groups[0].Plan.Entries.Single(e => !e.IsDirectory).SourcePath);
            await resultTask.StartCommand.ExecuteAsync(null); Assert.Equal(RepackState.Failed, resultTask.Result?.State); Capture("source-changed");
        }
        finally { window.Close(); }
    }

    private static async Task<UnpackDocument> Parent(TestWorkspace w, TestLifetime life)
    {
        var parent = new UnpackDocument(new UnpackService(), life) { OutputDirectory = w.Output };
        await parent.AddPathsAsync([w.Zip("source.zip", ("wrap/data.pdf", [1]))]); await parent.StartCommand.ExecuteAsync(null); return parent;
    }
    private static async Task<UnpackResult> Extract(TestWorkspace w, params string[] sources)
    {
        await using var session = new UnpackService().CreateSession(new(sources, w.Output));
        var result = await session.ExecuteAsync(cancellationToken: Token); Assert.Equal(BatchState.Completed, result.State); return result;
    }
    private sealed class PausingWriter : IArchiveWriter
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Stopped { get; private set; }
        public IProgress<PackProgress>? Progress { get; private set; }
        public async Task WriteAsync(PackPlan plan, Stream output, IProgress<PackProgress>? progress, CancellationToken cancellationToken, PackSecret? secret = null)
        {
            Progress = progress;
            try
            {
                await new ZipArchiveWriter().WriteAsync(plan, output, progress, cancellationToken, secret);
                Entered.TrySetResult(); await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            finally { Stopped = true; }
        }
    }
}

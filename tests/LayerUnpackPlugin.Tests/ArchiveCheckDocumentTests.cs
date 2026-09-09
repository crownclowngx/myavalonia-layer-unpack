using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using LayerUnpackPlugin.Features.Browse;
using LayerUnpackPlugin.Features.Check;
using LayerUnpackPlugin.Features.Pack;
using LayerUnpackPlugin.Features.Unpack;
using LayerUnpackPlugin.Headless.Application;
using LayerUnpackPlugin.Headless.Contracts;
using LayerUnpackPlugin.Headless.Tests;
using Xunit;

namespace LayerUnpackPlugin.Tests;

public sealed class ArchiveCheckDocumentTests
{
    [AvaloniaFact]
    public async Task 解压和浏览检查入口不继承密码不自动执行且返回保留原状态()
    {
        using var w = new TestWorkspace(); using var life = new TestLifetime(); var source = w.Zip("a.zip", ("a", [1]));
        await using var unpack = new UnpackDocument(new UnpackService(), life) { PasswordText = "parent-secret" };
        await unpack.AddPathsAsync([source]); await unpack.CheckArchiveCommand.ExecuteAsync(null);
        Assert.False(unpack.ShowMainTask); Assert.False(unpack.CanEdit); Assert.True(unpack.ShowCheckTask);
        var task = unpack.CheckTask!; Assert.Null(task.Result); Assert.Empty(task.PasswordText); Assert.False(task.DirectoryOnly); Assert.False(Directory.Exists(w.Output));
        await task.StartCommand.ExecuteAsync(null); Assert.Equal(ArchiveCheckState.Completed, task.Result?.State);
        unpack.ReturnFromCheckCommand.Execute(null); Assert.True(unpack.ShowMainTask); Assert.Single(unpack.Inputs);
        await unpack.ClearCommand.ExecuteAsync(null); Assert.True(task.IsClosed); Assert.Null(unpack.CheckTask);
        await using var browse = new BrowseDocument(new ArchiveBrowseService(), new UnpackService(), life);
        await browse.OpenPathAsync(source); browse.PasswordText = "browse-secret"; var count = browse.Rows.Count;
        await browse.CheckArchiveCommand.ExecuteAsync(null); Assert.False(browse.ShowMainTask); Assert.Empty(browse.CheckTask!.PasswordText);
        browse.CheckTask.DirectoryOnly = true; await browse.CheckTask.StartCommand.ExecuteAsync(null);
        Assert.Equal(ArchiveCheckState.DirectoryRead, browse.CheckTask.Result?.State); Assert.Contains("尚未检查内容", browse.CheckTask.Summary);
        browse.ReturnFromCheckCommand.Execute(null); Assert.True(browse.ShowMainTask); Assert.Equal(count, browse.Rows.Count); Assert.True(browse.HasCatalog);
    }

    [AvaloniaFact]
    public async Task 格式选择只展示通过验证项且切换清除密码并更新扩展名()
    {
        using var w = new TestWorkspace(); using var life = new TestLifetime();
        await using var document = new PackDocument(new PackBatchService(), life);
        var view = new PackView { DataContext = document }; var window = new Window { Content = view, Width = 500, Height = 720 };
        window.Show();
        try
        {
            Assert.Equal(new[] { "ZIP", "TAR", "TAR.GZ" }, document.FormatChoices); Assert.Equal(0, document.FormatIndex);
            var source = w.FilePath("a.txt"); File.WriteAllText(source, "R07"); await document.AddPathsAsync([source]);
            document.EncryptionEnabled = true; document.TargetPassword = document.ConfirmPassword = "private-sentinel";
            var picker = view.FindControl<ComboBox>("FormatPicker")!; picker.SelectedIndex = 1; Dispatcher.UIThread.RunJobs();
            Assert.Equal(1, document.FormatIndex); Assert.False(document.CanEncrypt); Assert.False(document.CanChooseCompression);
            Assert.Empty(document.TargetPassword); Assert.False(document.EncryptionEnabled); Assert.EndsWith(".tar", document.ArchiveName);
            Assert.False(view.FindControl<CheckBox>("EncryptToggle")!.IsVisible); Assert.Empty(document.RootMappings);
            await document.StartCommand.ExecuteAsync(null); Assert.Equal(PackState.Completed, document.CurrentResult?.State); Assert.EndsWith(".tar", document.CurrentResult!.OutputPath);
            picker.SelectedIndex = 2; Dispatcher.UIThread.RunJobs(); Assert.EndsWith(".tar.gz", document.ArchiveName);
            Assert.Equal(new[] { "标准", "快速", "高压缩" }, document.CompressionChoices);
            await document.StartCommand.ExecuteAsync(null); Assert.Equal(PackState.Completed, document.CurrentResult?.State); Assert.EndsWith(".tar.gz", document.CurrentResult!.OutputPath);
            document.ClearCommand.Execute(null); Assert.Equal(0, document.FormatIndex); Assert.True(document.CanEncrypt);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 父页关闭或取消等待实际清理并隔离迟到回调(bool close)
    {
        using var w = new TestWorkspace(); using var life = new TestLifetime(); var service = new ControlledService();
        await using var parent = new UnpackDocument(new UnpackService(), life, checkService: service);
        await parent.AddPathsAsync([w.Zip("a.zip", ("a", [1]))]); await parent.CheckArchiveCommand.ExecuteAsync(null); var task = parent.CheckTask!;
        task.PasswordText = "private-sentinel"; var running = task.StartCommand.ExecuteAsync(null);
        await service.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(parent.ReturnFromCheckCommand.CanExecute(null)); Assert.False(task.CanEdit); Assert.Empty(task.PasswordText);
        Assert.DoesNotContain("private-sentinel", JsonSerializer.Serialize(task));
        Task? closing = null;
        if (close) closing = parent.DisposeAsync().AsTask(); else task.CancelCommand.Execute(null);
        await service.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(running.IsCompleted); if (close) Assert.False(closing!.IsCompleted); else Assert.True(task.IsBusy);
        service.Finish.SetResult(); if (closing is not null) await closing; await running;
        var summary = task.Summary; service.Progress!.Report(new(999, 999, 999)); Dispatcher.UIThread.RunJobs(); Assert.Equal(summary, task.Summary);
        if (close) Assert.True(task.IsClosed); else { Assert.False(task.IsBusy); Assert.True(parent.ReturnFromCheckCommand.CanExecute(null)); }
    }

    [AvaloniaTheory]
    [InlineData(false, 460, 560, false)]
    [InlineData(true, 460, 560, true)]
    [InlineData(false, 900, 720, true)]
    public async Task 检查结果在主题窄窗口下渲染且键盘能到达主动作(bool dark, int width, int height, bool directory)
    {
        using var w = new TestWorkspace(); var source = w.Zip("a.zip", ("readme.txt", "R07"u8.ToArray()));
        await using var document = new ArchiveCheckDocument(new ArchiveCheckService(), [source], CancellationToken.None) { DirectoryOnly = directory };
        await document.StartCommand.ExecuteAsync(null);
        var view = new ArchiveCheckView { DataContext = document };
        var window = new Window { Content = view, Width = width, Height = height, RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs(); var start = view.FindControl<Button>("CheckStart")!;
            Assert.True(start.IsEnabled); Assert.True(start.Focus()); Assert.True(start.IsFocused);
            window.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, "\t"); Dispatcher.UIThread.RunJobs();
            var position = start.TranslatePoint(default, window); Assert.NotNull(position); Assert.InRange(position.Value.Y, 0, height - start.Bounds.Height);
            Assert.Equal(document.Summary, view.FindControl<TextBlock>("CheckSummary")!.Text);
            var destination = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/ui")); Directory.CreateDirectory(destination);
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(); using var frame = window.CaptureRenderedFrame(); Assert.NotNull(frame);
            frame.Save(Path.Combine(destination, $"r07-check-{dark}-{width}-{directory}.png"), PngBitmapEncoderOptions.Default);
        }
        finally { window.Close(); }
    }

    private sealed class ControlledService : IArchiveCheckService
    {
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Finish { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal IProgress<ArchiveCheckProgress>? Progress { get; private set; }
        public async Task<ArchiveCheckResult> CheckAsync(ArchiveCheckRequest request, IProgress<ArchiveCheckProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            Progress = progress; Started.SetResult();
            try { await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { Cancelled.SetResult(); }
            await Finish.Task.ConfigureAwait(false);
            return new(ArchiveCheckState.Cancelled, request.Scope, "Zip", 0, 10, 1, TimeSpan.Zero, [], [], null, null);
        }
    }
}

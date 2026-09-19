using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using LayerUnpackPlugin.Features.Unpack;
using LayerUnpackPlugin.Headless.Application;
using LayerUnpackPlugin.Headless.Contracts;
using LayerUnpackPlugin.Headless.Tests;
using Xunit;

namespace LayerUnpackPlugin.Tests;

public sealed class SplitDocumentTests
{
    [AvaloniaFact]
    public async Task 模拟拖放全部卷实际经过视图事件且只添加一个组()
    {
        using var w = new TestWorkspace(); using var life = new TestLifetime(); var parts = SplitTestData.CopyParts(w);
        await using var document = new UnpackDocument(new UnpackService(), life);
        var view = new UnpackView { DataContext = document };
        var window = new Window { Content = view };
        try
        {
            window.Show();
            var transfer = new DataTransfer();
            foreach (var path in parts.Reverse())
            {
                var file = await window.StorageProvider.TryGetFileFromPathAsync(path);
                Assert.NotNull(file); transfer.Add(DataTransferItem.CreateFile(file));
            }
            var args = new DragEventArgs(DragDrop.DropEvent, transfer, view, default, KeyModifiers.None);
            view.RaiseEvent(args);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            while (document.IsBusy) await Task.Delay(5, timeout.Token);
            Assert.True(args.Handled); Assert.Contains("5 卷", Assert.Single(document.Inputs).Name);
            Assert.Null(document.CurrentResult); Assert.False(document.IsBusy);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(false, 900, 760)]
    [InlineData(true, 640, 520)]
    public async Task 分卷一行与重新识别子页在深浅主题和窄窗口可用(bool dark, int width, int height)
    {
        using var w = new TestWorkspace(); using var life = new TestLifetime(); var parts = SplitTestData.CopyParts(w);
        var tail = File.ReadAllBytes(parts[2]); File.Delete(parts[2]);
        await using var document = new UnpackDocument(new UnpackService(), life) { OutputDirectory = w.Output, InputsExpanded = true };
        var view = new UnpackView { DataContext = document };
        var window = new Window { Content = view, Width = width, Height = height, RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light };
        try
        {
            window.Show(); await document.AddPathsAsync([parts[1], Path.GetDirectoryName(parts[0])!]); Dispatcher.UIThread.RunJobs();
            Assert.Contains("4 卷", Assert.Single(document.Inputs).Name); Assert.Equal(1, document.MaxDepth);
            Assert.False(document.OptionsExpanded); Assert.False(document.PasswordsExpanded);
            Capture(window, $"input-{dark}-{width}");
            await document.StartCommand.ExecuteAsync(null); document.SelectedNode = Assert.Single(document.Roots);
            Dispatcher.UIThread.RunJobs(); var button = view.FindControl<Button>("RediscoverButton")!;
            Assert.True(button.IsVisible); Assert.True(button.IsEnabled); button.BringIntoView(); Dispatcher.UIThread.RunJobs();
            Assert.True(button.Focus()); window.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null);
            Assert.NotSame(button, window.FocusManager!.GetFocusedElement()); Capture(window, $"missing-{dark}-{width}");
            File.WriteAllBytes(parts[2], tail); var previous = document.CurrentResult;
            document.PasswordText = "private-not-copied";
            await document.RediscoverGroupCommand.ExecuteAsync(null); Dispatcher.UIThread.RunJobs();
            var fresh = document.RediscoveredTask!; Assert.True(document.ShowRediscoveredTask); Assert.False(document.ShowMainTask);
            Assert.Null(fresh.CurrentResult); Assert.False(fresh.IsBusy); Assert.Empty(fresh.PasswordText);
            Assert.Contains("5 卷", Assert.Single(fresh.Inputs).Name); Assert.Same(previous, document.CurrentResult);
            Capture(window, $"rediscovered-{dark}-{width}");
            await fresh.StartCommand.ExecuteAsync(null); Assert.Equal(BatchState.Completed, fresh.CurrentResult!.State);
            document.ReturnFromRediscoveryCommand.Execute(null); Assert.True(document.ShowMainTask); Assert.Same(previous, document.CurrentResult);
            await document.ClearCommand.ExecuteAsync(null); Assert.True(fresh.IsClosed); Assert.Null(document.RediscoveredTask);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task 输入预览之后补卷不会在开始时悄悄接纳()
    {
        using var w = new TestWorkspace(); using var life = new TestLifetime(); var parts = SplitTestData.CopyParts(w);
        var tail = File.ReadAllBytes(parts[^1]); File.Delete(parts[^1]);
        await using var document = new UnpackDocument(new UnpackService(), life) { OutputDirectory = w.Output };
        await document.AddPathsAsync([parts[0]]); File.WriteAllBytes(parts[^1], tail);
        await document.StartCommand.ExecuteAsync(null);
        Assert.Equal(UnpackError.InputChanged, Assert.Single(document.CurrentResult!.Nodes).Error?.Code);
        Assert.Equal(0, document.CurrentResult.AttemptCount);
    }

    [AvaloniaFact]
    public async Task 内层重新识别只准备本组且按原批次计算剩余深度()
    {
        using var w = new TestWorkspace(); using var life = new TestLifetime(); var parts = SplitTestData.CopyParts(w);
        var outer = w.Zip("outer.zip", parts.Where((_, i) => i != 2).Select(p => ("目录/" + Path.GetFileName(p), File.ReadAllBytes(p))).ToArray());
        await using var document = new UnpackDocument(new UnpackService(), life) { OutputDirectory = w.Output, MaxDepth = 4 };
        await document.AddPathsAsync([outer]); await document.StartCommand.ExecuteAsync(null);
        document.SelectedNode = Assert.Single(document.Roots[0].Children);
        var original = document.CurrentResult!; var node = original.Nodes[1];
        File.Copy(parts[2], Path.Combine(Path.GetDirectoryName(node.SourcePath)!, Path.GetFileName(parts[2])));
        document.MaxDepth = 1; await document.RediscoverGroupCommand.ExecuteAsync(null);
        Assert.Equal(3, document.RediscoveredTask!.MaxDepth);
        Assert.Equal(Path.GetDirectoryName(node.SourcePath), document.RediscoveredTask.OutputDirectory);
        Assert.Single(document.RediscoveredTask.Inputs); Assert.Null(document.RediscoveredTask.CurrentResult);
        await document.RediscoveredTask.StartCommand.ExecuteAsync(null);
        Assert.Same(original, document.CurrentResult); Assert.Equal(1, document.RediscoveredTask.CurrentResult!.Succeeded);
        Assert.Single(Directory.GetDirectories(w.Output));
    }

    [AvaloniaFact]
    public async Task 父任务关闭会排空重新识别任务并释放每个卷()
    {
        using var w = new TestWorkspace(); using var life = new TestLifetime(); var parts = SplitTestData.CopyParts(w);
        var tail = File.ReadAllBytes(parts[2]); File.Delete(parts[2]);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var extractor = new DelegatingExtractor(async call => { entered.SetResult(); await Task.Delay(Timeout.Infinite, call.Token); return new("Test", [], false); });
        using var document = new UnpackDocument(new UnpackService(extractor), life) { OutputDirectory = w.Output };
        await document.AddPathsAsync(parts.Where(File.Exists)); await document.StartCommand.ExecuteAsync(null);
        document.SelectedNode = document.Roots[0]; File.WriteAllBytes(parts[2], tail);
        await document.RediscoverGroupCommand.ExecuteAsync(null);
        var child = document.RediscoveredTask!; var work = child.StartCommand.ExecuteAsync(null);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.False(document.ReturnFromRediscoveryCommand.CanExecute(null));
        life.Close(); document.Dispose(); await work;
        Assert.True(child.IsClosed);
        foreach (var part in parts) { using var exclusive = File.Open(part, FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
        Assert.Empty(Directory.GetDirectories(w.Output));
    }

    private static void Capture(Window window, string name)
    {
        using var frame = window.CaptureRenderedFrame(); Assert.NotNull(frame);
        var output = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/ui/G0015")); Directory.CreateDirectory(output);
        frame.Save(Path.Combine(output, name + ".png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
    }

}

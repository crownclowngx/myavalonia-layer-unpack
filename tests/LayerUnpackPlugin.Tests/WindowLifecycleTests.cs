using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using LayerUnpackPlugin.Features.Unpack;
using LayerUnpackPlugin.Headless.Application;
using LayerUnpackPlugin.Headless.Contracts;
using LayerUnpackPlugin.Headless.Tests;
using LayerUnpackPlugin.Standalone;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace LayerUnpackPlugin.Tests;

public sealed class WindowLifecycleTests
{
    [AvaloniaFact]
    public async Task Standalone真实窗口关闭会取消工作并释放Scope后再关闭()
    {
        using var w = new TestWorkspace();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = false;
        var services = StandaloneServices.Create();
        services.Replace(ServiceDescriptor.Singleton<IArchiveExtractor>(new DelegatingExtractor(async call =>
        {
            entered.SetResult();
            try { await Task.Delay(Timeout.Infinite, call.Token); }
            finally { stopped = true; }
            return new("Test", [], false);
        })));
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        var window = new MainWindow(provider);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        window.Closed += (_, _) => closed.TrySetResult();
        try
        {
            window.Show();
            var view = Assert.IsType<UnpackView>(window.FindControl<ContentControl>("PreviewHost")!.Content);
            var document = Assert.IsType<UnpackDocument>(view.DataContext);
            document.OutputDirectory = w.Output;
            await document.AddPathsAsync([w.Zip("a.zip")]);
            var work = document.StartCommand.ExecuteAsync(null);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            window.Close();
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            await work;
            Assert.True(stopped); Assert.True(document.IsClosed); Assert.False(window.IsVisible);
            Assert.Null(window.FindControl<ContentControl>("PreviewHost")!.Content);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task 两个Document同时运行关闭一个不会取消另一个()
    {
        using var w = new TestWorkspace();
        using var firstLifetime = new TestLifetime(); using var secondLifetime = new TestLifetime();
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new UnpackService(new DelegatingExtractor(async call =>
        {
            (Path.GetFileName(call.Source) == "first.zip" ? firstStarted : secondStarted).SetResult();
            await release.Task.WaitAsync(call.Token);
            return await call.WriteAsync([1]);
        }));
        await using var first = new UnpackDocument(service, firstLifetime) { OutputDirectory = w.Output };
        await using var second = new UnpackDocument(service, secondLifetime) { OutputDirectory = w.Output };
        await first.AddPathsAsync([w.Zip("first.zip")]); await second.AddPathsAsync([w.Zip("second.zip")]);
        var firstWork = first.StartCommand.ExecuteAsync(null); var secondWork = second.StartCommand.ExecuteAsync(null);
        try
        {
            await Task.WhenAll(firstStarted.Task, secondStarted.Task).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            firstLifetime.Close(); first.Dispose();
            Assert.False(second.IsClosed); Assert.True(second.IsBusy); Assert.False(secondWork.IsCompleted);
            release.SetResult();
            await Task.WhenAll(firstWork, secondWork).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.Equal(BatchState.Cancelled, first.CurrentResult?.State);
            Assert.Equal(BatchState.Completed, second.CurrentResult?.State);
            Assert.Single(Directory.GetDirectories(w.Output));
        }
        finally { release.TrySetResult(); }
    }
}

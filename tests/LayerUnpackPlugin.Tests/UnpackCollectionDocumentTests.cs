using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using LayerUnpackPlugin.Features.Unpack;
using LayerUnpackPlugin.Headless.Application;
using LayerUnpackPlugin.Headless.Contracts;
using LayerUnpackPlugin.Headless.Tests;
using MyAvaloniaManagement.PluginSdk;
using Xunit;

namespace LayerUnpackPlugin.Tests;

public sealed class UnpackCollectionDocumentTests
{
    [AvaloniaFact]
    public async Task 重新打开密码库后默认选择并用于同批加密归档()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var w = new TestWorkspace();
        using var lifetime = new TestLifetime();
        var storePath = w.FilePath("passwords.json");
        var store = new SavedPasswordStore(storePath);
        const string password = "test-password-that-must-not-be-plaintext";
        var id = store.Save(null, "样例课程", password, true);
        Assert.DoesNotContain(password, File.ReadAllText(storePath));
        Assert.Equal(password, new SavedPasswordStore(storePath).ReadPassword(id));
        Assert.Equal(id, store.Save(id, "样例课程新版", "", true));
        Assert.Equal(password, store.ReadPassword(id));

        var token = TestContext.Current.CancellationToken;
        var source = w.FilePath("document.txt");
        File.WriteAllText(source, "批量解压检查");
        var pack = new PackService();
        using var secret = new PackSecret(password);
        async Task<string> EncryptAsync(string name, string input)
        {
            var archive = w.FilePath(name);
            var plan = await pack.PrepareAsync(new([input], archive, options: new() { Encrypt = true }), cancellationToken: token);
            var result = await pack.ExecuteAsync(plan, cancellationToken: token, secret: secret);
            Assert.Equal(PackState.Completed, result.State);
            return archive;
        }
        var nested = await EncryptAsync("nested.zip", source);
        var outer = await EncryptAsync("a.zip", nested);
        var other = await EncryptAsync("b.zip", source);
        await using var document = new UnpackDocument(new UnpackService(), lifetime, passwordStore: new SavedPasswordStore(storePath))
        { OutputDirectory = w.Output, MaxDepth = 2 };
        await document.InitializeAsync(new NewDocumentActivation("解压任务"), token);
        Assert.Equal("样例课程新版", document.SelectedSavedPassword?.Hint);
        await document.AddPathsAsync([outer, other]);
        await document.StartCommand.ExecuteAsync(null);
        Assert.Equal(3, document.CurrentResult!.Succeeded);
        Assert.Equal("批量解压检查", File.ReadAllText(Path.Combine(w.Output, "a", "nested", "document.txt")));
        document.RemoveSavedPasswordCommand.Execute(document.SelectedSavedPassword);
        Assert.Empty(store.List());
        Assert.Equal(Guid.Empty, document.SelectedSavedPassword?.Id);
    }

    [AvaloniaFact]
    public async Task 紧凑表单显示当前层与进度并移动结果()
    {
        using var w = new TestWorkspace();
        using var lifetime = new TestLifetime();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var extractor = new DelegatingExtractor(async call =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(call.Token);
            return await call.WriteAsync([1, 2, 3]);
        });
        await using var document = new UnpackDocument(new UnpackService(extractor), lifetime) { OutputDirectory = w.Output };
        await document.AddPathsAsync([w.Zip("sample.zip", ("file.txt", [1]))]);
        var view = new UnpackView { DataContext = document };
        var window = new Window { Width = 900, Height = 650, Content = view };
        window.Show();
        try
        {
            view.FindControl<CheckBox>("CollectFilesToggle")!.IsChecked = true;
            Assert.True(document.CollectFiles);
            var work = document.StartCommand.ExecuteAsync(null);
            await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
            Dispatcher.UIThread.RunJobs();
            Assert.Contains("第 1 层", document.CurrentItemText);
            Assert.Contains("0/1", view.FindControl<TextBlock>("TaskProgressText")!.Text);
            Assert.True(view.FindControl<ProgressBar>("TaskProgressBar")!.IsVisible);
            release.TrySetResult();
            await work;
            Assert.True(document.HasCollectedOutput);
            Assert.Contains("已移动 1", document.CollectionSummary);
            Assert.True(File.Exists(Path.Combine(document.ResultOutputPath!, "result.txt")));
            Assert.False(File.Exists(Path.Combine(w.Output, "sample", "result.txt")));
        }
        finally { release.TrySetResult(); window.Close(); }
    }
}

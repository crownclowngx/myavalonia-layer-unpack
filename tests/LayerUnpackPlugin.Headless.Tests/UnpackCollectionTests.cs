using LayerUnpackPlugin.Headless.Application;
using LayerUnpackPlugin.Headless.Contracts;
using Xunit;

namespace LayerUnpackPlugin.Headless.Tests;

public sealed class UnpackCollectionTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task 递归解压后平铺移动并只清理匹配的来源()
    {
        using var w = new TestWorkspace();
        var inner = w.Zip("inner.zip", ("a/clip.MP4", [1]), ("b/clip.mp4", [2]), ("captions.srt", [3]), ("extra.log", [4]));
        var outer = w.Zip("outer.zip", ("inner.zip", File.ReadAllBytes(inner)));
        var other = w.Zip("other.zip", ("unmatched.log", [5]));
        Directory.CreateDirectory(Path.Combine(w.Output, "结果"));
        File.WriteAllBytes(Path.Combine(w.Output, "结果", "existing.txt"), [99]);
        var batch = await UnpackAsync([outer, other], w.Output, 2);
        var collection = new UnpackCollection(batch, new(w.Output, CollectionFileTypes.Video, ".SRT", cleanup: true));
        var phases = new List<string>();
        var result = await collection.ExecuteAsync(new Observer(p => phases.Add(p.Phase)), Token);

        Assert.Equal(CollectionState.Completed, result.State);
        Assert.Equal(3, result.Moved);
        Assert.Equal(1, result.PreservedSources);
        Assert.Equal("结果 (1)", Path.GetFileName(result.OutputDirectory));
        Assert.Equal(new byte[] { 1 }, File.ReadAllBytes(Path.Combine(result.OutputDirectory!, "clip.MP4")));
        Assert.Equal(new byte[] { 2 }, File.ReadAllBytes(Path.Combine(result.OutputDirectory!, "clip (1).mp4")));
        Assert.Equal(new byte[] { 3 }, File.ReadAllBytes(Path.Combine(result.OutputDirectory!, "captions.srt")));
        Assert.False(Directory.Exists(Path.Combine(w.Output, "outer")));
        Assert.True(File.Exists(Path.Combine(w.Output, "other", "unmatched.log")));
        Assert.True(File.Exists(Path.Combine(w.Output, "结果", "existing.txt")));
        Assert.True(File.Exists(outer));
        Assert.Contains("准备结果文件", phases);
        Assert.Contains("完成移动", phases);
        Assert.Contains("清理解压产物", phases);
    }

    [Fact]
    public async Task 清理失败后继续沿用已提交结果且不触碰额外文件()
    {
        using var w = new TestWorkspace();
        var outer = w.Zip("outer.zip", ("clip.mp4", [1]), ("extra.log", [2]));
        var batch = await UnpackAsync([outer], w.Output);
        var source = batch.Nodes[0].OutputDirectory!;
        var newFile = Path.Combine(source, "用户新增.txt");
        File.WriteAllBytes(newFile, [9]);
        var collection = new UnpackCollection(batch, new(w.FilePath("custom"), CollectionFileTypes.Video, "", cleanup: true));
        var first = await collection.ExecuteAsync(cancellationToken: Token);

        Assert.Equal(CollectionState.Paused, first.State);
        Assert.Equal(1, first.Moved);
        Assert.Equal(1, first.Remaining);
        Assert.Contains(source, first.Message);
        Assert.True(File.Exists(newFile));
        Assert.False(File.Exists(Path.Combine(source, "clip.mp4")));
        Assert.False(File.Exists(Path.Combine(source, "extra.log")));
        File.Move(newFile, w.FilePath("用户新增.txt"));
        var second = await collection.ExecuteAsync(cancellationToken: Token);
        Assert.Equal(CollectionState.Completed, second.State);
        Assert.Equal(first.OutputDirectory, second.OutputDirectory);
        Assert.Single(Directory.EnumerateDirectories(w.FilePath("custom")));
        Assert.False(Directory.Exists(source));
    }

    [Fact]
    public async Task 取消或无匹配时保留产物且未展开的分支不清理()
    {
        using var w = new TestWorkspace();
        var inner = w.Zip("inner.zip", ("data.txt", [1]));
        var outer = w.Zip("outer.zip", ("inner.zip", File.ReadAllBytes(inner)), ("clip.mp4", [2]), ("extra.log", [3]));
        var batch = await UnpackAsync([outer], w.Output);
        var root = batch.Nodes[0].OutputDirectory!;
        var noMatch = new UnpackCollection(batch, new(w.Output, CollectionFileTypes.Image, "", cleanup: true));
        Assert.Equal(CollectionState.NoMatches, (await noMatch.ExecuteAsync(cancellationToken: Token)).State);
        Assert.False(Directory.Exists(Path.Combine(w.Output, "结果")));
        var collection = new UnpackCollection(batch, new(w.Output, CollectionFileTypes.Video, ".zip", cleanup: true));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var first = await collection.ExecuteAsync(new Observer(p =>
        {
            if (p.Phase == "准备结果文件") cancellation.Cancel();
        }), cancellation.Token);
        Assert.Equal(CollectionState.Paused, first.State);
        Assert.Null(first.OutputDirectory);
        Assert.True(File.Exists(Path.Combine(root, "clip.mp4")));
        var result = await collection.ExecuteAsync(cancellationToken: Token);
        Assert.Equal(CollectionState.Completed, result.State);
        Assert.Equal(1, result.Moved);
        Assert.Equal(1, result.PreservedSources);
        Assert.True(File.Exists(Path.Combine(root, "inner.zip")));
        Assert.True(File.Exists(Path.Combine(root, "extra.log")));
    }

    [Fact]
    public async Task 回收原包逐卷比对来源身份并拒绝等长替换()
    {
        using var w = new TestWorkspace();
        var source = w.Zip("outer.zip", ("clip.mp4", [1, 2, 3]));
        var batch = await UnpackAsync([source], w.Output);
        var original = File.ReadAllBytes(source);
        var modified = File.GetLastWriteTimeUtc(source);
        var changed = original.ToArray();
        changed[^1] ^= 1;
        File.WriteAllBytes(source, changed);
        File.SetLastWriteTimeUtc(source, modified);
        var recycler = new TestRecycler(w.FilePath("recycling"));
        var collection = new UnpackCollection(batch, new(w.Output, CollectionFileTypes.Video, "", true, true), recycler);
        var first = await collection.ExecuteAsync(cancellationToken: Token);
        Assert.Equal(CollectionState.Paused, first.State);
        Assert.True(File.Exists(source));
        Assert.Equal(0, first.Recycled);
        File.WriteAllBytes(source, original);
        File.SetLastWriteTimeUtc(source, modified);
        var second = await collection.ExecuteAsync(cancellationToken: Token);
        Assert.Equal(CollectionState.Completed, second.State);
        Assert.Equal(1, second.Recycled);
        Assert.True(File.Exists(w.FilePath(Path.Combine("recycling", "outer.zip"))));
    }

    private static async Task<UnpackResult> UnpackAsync(string[] inputs, string output, int depth = 1)
    {
        await using var session = new UnpackService().CreateSession(new(inputs, output, depth));
        return await session.ExecuteAsync(cancellationToken: Token);
    }

    private sealed class Observer(Action<CollectionProgress> report) : IProgress<CollectionProgress>
    {
        public void Report(CollectionProgress value) => report(value);
    }

    private sealed class TestRecycler(string path) : IArchiveRecycler
    {
        public void Recycle(string file)
        {
            Directory.CreateDirectory(path);
            File.Move(file, Path.Combine(path, Path.GetFileName(file)));
        }
    }
}

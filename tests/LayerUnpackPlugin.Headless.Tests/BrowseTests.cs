using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using ICSharpCode.SharpZipLib.Zip;
using LayerUnpackPlugin.Headless.Application;
using LayerUnpackPlugin.Headless.Contracts;
using Xunit;
using SystemZipFile = System.IO.Compression.ZipFile;

namespace LayerUnpackPlugin.Headless.Tests;

/// <summary>用真实归档清单和字节验证浏览闭环；故障与取消通过同步进度边界注入，不使用计时碰运气。</summary>
public sealed class BrowseTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static Task<IArchiveBrowseSession> Open(string path, BrowseLimits? limits = null, IProgress<BrowseProgress>? progress = null, CancellationToken? token = null) =>
        new ArchiveBrowseService().OpenAsync(new(path, Limits: limits), progress, token ?? Token);
    private static BrowseSelection Select(IArchiveBrowseSession session, params string[] paths)
    {
        var selection = new ArchiveSelection(session.Catalog);
        foreach (var path in paths) selection.SetSelected(Assert.Single(session.Catalog.Entries, e => e.Path == path).Id, true);
        return selection.Capture();
    }

    [Fact]
    public async Task 浏览不创建输出且只提取两个文件保留同名相对路径()
    {
        using var w = new TestWorkspace();
        var path = w.Zip("资料.zip", ("甲/正文.txt", "第一份"u8.ToArray()), ("乙/正文.txt", "第二份"u8.ToArray()), ("不要.txt", [3]));
        var before = Directory.GetFileSystemEntries(w.Root, "*", SearchOption.AllDirectories).Order().ToArray();
        await using var session = await Open(path);
        Assert.Equal(before, Directory.GetFileSystemEntries(w.Root, "*", SearchOption.AllDirectories).Order());
        Assert.Equal(3, session.Catalog.Entries.Count(e => !e.IsSynthetic));
        Assert.Equal(0, session.ExpandedBytes); Assert.True(session.ReadBytes >= new FileInfo(path).Length * 2);
        var result = await session.ExtractAsync(Select(session, "甲/正文.txt", "乙/正文.txt"), w.Output, cancellationToken: Token);
        Assert.Equal(BrowseExtractState.Completed, result.State);
        Assert.Equal(new[] { "乙/正文.txt", "甲/正文.txt" }.Order(), result.Files.Order());
        Assert.Equal("第一份", File.ReadAllText(Path.Combine(result.OutputDirectory!, "甲/正文.txt")));
        Assert.Equal("第二份", File.ReadAllText(Path.Combine(result.OutputDirectory!, "乙/正文.txt")));
        Assert.Equal(2, Directory.GetFiles(result.OutputDirectory!, "*", SearchOption.AllDirectories).Length);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task 搜索分页不改变目录后代选择且父子重复选择只计一次()
    {
        using var w = new TestWorkspace();
        await using var session = await Open(w.Zip("source.zip", ("a/first.txt", [1]), ("a/deep/second.txt", [2]), ("ab/other.txt", [3]), ("A/case.txt", [4])));
        var catalog = session.Catalog; var selection = new ArchiveSelection(catalog);
        var parent = Assert.Single(catalog.GetPage("a", pageSize: 500, cancellationToken: Token).Entries, e => e.Path == "a");
        selection.SetSelected(parent.Id, true);
        var child = Assert.Single(catalog.Entries, e => e.Path == "a/first.txt"); selection.SetSelected(child.Id, true);
        Assert.Equal(2, selection.Count); Assert.True(selection.GetState(parent.Id));
        Assert.Empty(catalog.GetPage("不存在", cancellationToken: Token).Entries); Assert.Equal(2, selection.Count);
        selection.SetSelected(child.Id, false); Assert.Null(selection.GetState(parent.Id));
        var result = await session.ExtractAsync(selection.Capture(), w.Output, cancellationToken: Token);
        Assert.Equal(new[] { "a/deep/second.txt" }, result.Files);
        Assert.Equal(new byte[] { 2 }, File.ReadAllBytes(Path.Combine(result.OutputDirectory!, result.Files[0])));
        selection.SetSelected(parent.Id, false); Assert.Equal(0, selection.Count);
    }

    [Fact]
    public async Task 明确空目录可以选择且内部归档按一层原样输出()
    {
        using var w = new TestWorkspace(); var nested = File.ReadAllBytes(w.Zip("inner.zip", ("secret.txt", [1])));
        await using var session = await Open(w.Zip("outer.zip", ("empty/", []), ("inner.zip", nested)));
        var result = await session.ExtractAsync(Select(session, "empty", "inner.zip"), w.Output, cancellationToken: Token);
        Assert.Equal(BrowseExtractState.Completed, result.State); Assert.Single(result.Files);
        Assert.True(Directory.Exists(Path.Combine(result.OutputDirectory!, "empty")));
        Assert.Equal(nested, File.ReadAllBytes(Path.Combine(result.OutputDirectory!, "inner.zip")));
        Assert.False(File.Exists(Path.Combine(result.OutputDirectory!, "secret.txt")));
    }

    [Theory]
    [InlineData("same.txt", "same.txt")]
    [InlineData("Case.txt", "case.txt")]
    [InlineData("a", "a/child.txt")]
    [InlineData("A/one.txt", "a/two.txt")]
    [InlineData("a/", "a/")]
    public async Task 冲突集合明确失败而不静默覆盖(string first, string second)
    {
        using var w = new TestWorkspace();
        await using var session = await Open(w.Zip("conflict.zip", (first, first.EndsWith('/') ? [] : [1]), (second, second.EndsWith('/') ? [] : [2])));
        var rows = session.Catalog.Entries.Where(e => !e.IsSynthetic).ToArray(); Assert.Equal(2, rows.Length); Assert.NotEqual(rows[0].Id, rows[1].Id);
        Assert.Contains(session.Catalog.Entries, e => e.Warning?.Contains("冲突") == true);
        var result = await session.ExtractAsync(new(rows.Select(e => e.Id)), w.Output, cancellationToken: Token);
        Assert.Equal(BrowseExtractState.Failed, result.State); Assert.Equal(UnpackError.OutputError, result.Error?.Code);
        Assert.False(Directory.Exists(w.Output)); Assert.Empty(result.Files);
    }

    [Fact]
    public async Task 重复路径按条目序号分别提取为独立产物且既有输出不覆盖()
    {
        using var w = new TestWorkspace();
        await using var session = await Open(w.Zip("same.zip", ("same.txt", [1]), ("same.txt", [2])));
        var rows = session.Catalog.Entries.ToArray();
        var first = await session.ExtractAsync(new([rows[0].Id]), w.Output, cancellationToken: Token);
        var second = await session.ExtractAsync(new([rows[1].Id]), w.Output, cancellationToken: Token);
        Assert.Equal(BrowseExtractState.Completed, first.State); Assert.Equal(BrowseExtractState.Completed, second.State);
        Assert.NotEqual(first.OutputDirectory, second.OutputDirectory);
        Assert.Equal(new byte[] { 1 }, File.ReadAllBytes(Path.Combine(first.OutputDirectory!, "same.txt")));
        Assert.Equal(new byte[] { 2 }, File.ReadAllBytes(Path.Combine(second.OutputDirectory!, "same.txt")));
    }

    [Fact]
    public async Task 同名包并发提取以不覆盖方式提交()
    {
        using var w = new TestWorkspace(); var path = w.Zip("same.zip", ("data", [1]));
        await using var a = await Open(path); await using var b = await Open(path);
        var results = await Task.WhenAll(a.ExtractAsync(Select(a, "data"), w.Output, cancellationToken: Token), b.ExtractAsync(Select(b, "data"), w.Output, cancellationToken: Token));
        Assert.All(results, r => Assert.Equal(BrowseExtractState.Completed, r.State));
        Assert.Equal(2, results.Select(r => r.OutputDirectory).Distinct().Count());
    }

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("/absolute.txt")]
    [InlineData("CON.txt")]
    [InlineData("a:stream")]
    [InlineData("a/../../bad")]
    public async Task 不安全名称可见且所选拒绝落盘其他安全项仍可提取(string bad)
    {
        using var w = new TestWorkspace();
        await using var session = await Open(w.Zip("unsafe.zip", (bad, [1]), ("safe.txt", [2])));
        var row = Assert.Single(session.Catalog.Entries, e => e.Problem is not null);
        var failed = await session.ExtractAsync(new([row.Id]), w.Output, cancellationToken: Token);
        Assert.Equal(UnpackError.UnsafePath, failed.Error?.Code); Assert.False(Directory.Exists(w.Output));
        var success = await session.ExtractAsync(Select(session, "safe.txt"), w.Output, cancellationToken: Token);
        Assert.Equal(BrowseExtractState.Completed, success.State);
    }

    [Theory]
    [InlineData("Rar5.encrypted_filesAndHeader.rar")]
    [InlineData("7Zip.LZMA2.Aes.7z")]
    [InlineData("Rar5.solid.rar")]
    [InlineData("Generated.utf8.tar.gz")]
    public async Task 未接入格式与加密头明确指向原解压而不尝试隐藏全量解压(string fixture)
    {
        using var w = new TestWorkspace(); var path = w.CopyFixture(fixture);
        var failure = await Assert.ThrowsAsync<UnpackFailureException>(() => Open(path));
        Assert.Equal(UnpackError.UnsupportedFormat, failure.Code); Assert.Contains("全部解压", failure.Message);
        Assert.Single(Directory.GetFileSystemEntries(w.Root));
    }

    [Theory]
    [InlineData("Zip.deflate.pkware.zip", "12345678")]
    [InlineData("Zip.deflate.WinzipAES.zip", "test")]
    [InlineData("Zip.deflate.WinzipAES2.zip", "test")]
    public async Task 加密内容先列目录再补密且新会话不继承密码(string fixture, string password)
    {
        using var w = new TestWorkspace(); var path = w.CopyFixture(fixture);
        await using var session = await Open(path);
        var row = session.Catalog.Entries.First(e => !e.IsDirectory && e.IsEncrypted);
        var selection = new BrowseSelection([row.Id]);
        var missing = await session.ExtractAsync(selection, w.Output, cancellationToken: Token);
        var wrong = await session.ExtractAsync(selection, w.Output, "wrong-public-password", cancellationToken: Token);
        var success = await session.ExtractAsync(selection, w.Output, password, cancellationToken: Token);
        Assert.Equal(UnpackError.PasswordRequiredOrInvalid, missing.Error?.Code);
        Assert.Equal(UnpackError.PasswordRequiredOrInvalid, wrong.Error?.Code);
        Assert.Equal(BrowseExtractState.Completed, success.State); Assert.True(success.ReadBytes > wrong.ReadBytes);
        using (var zip = SystemZipFile.OpenRead(path)) Assert.Equal(zip.Entries.Count, session.Catalog.Entries.Count(e => !e.IsSynthetic));
        using (var zip = new ICSharpCode.SharpZipLib.Zip.ZipFile(File.OpenRead(path)))
        {
            zip.Password = password; using var expected = zip.GetInputStream(zip[row.Id.Ordinal]); using var bytes = new MemoryStream(); expected.CopyTo(bytes);
            Assert.Equal(bytes.ToArray(), File.ReadAllBytes(Path.Combine(success.OutputDirectory!, row.Path)));
        }
        await using var fresh = await Open(path);
        var second = await fresh.ExtractAsync(new([fresh.Catalog.Entries.First(e => !e.IsDirectory && e.IsEncrypted).Id]), w.Output, cancellationToken: Token);
        Assert.Equal(UnpackError.PasswordRequiredOrInvalid, second.Error?.Code); Assert.Single(Directory.GetDirectories(w.Output));
    }

    [Fact]
    public async Task 同尺寸同时间戳替换仍失效旧选择且不能复用旧身份()
    {
        using var w = new TestWorkspace(); var path = w.Zip("source.zip", ("data.txt", [1, 2, 3]));
        var original = File.ReadAllBytes(path); var stamp = File.GetLastWriteTimeUtc(path);
        await using var session = await Open(path); var selection = Select(session, "data.txt");
        var changed = (byte[])original.Clone(); changed[30 + "data.txt".Length] ^= 1;
        File.WriteAllBytes(path, changed); File.SetLastWriteTimeUtc(path, stamp);
        var failed = await session.ExtractAsync(selection, w.Output, cancellationToken: Token);
        Assert.Equal(UnpackError.InputChanged, failed.Error?.Code); Assert.True(session.IsInvalidated); Assert.False(Directory.Exists(w.Output));
        File.WriteAllBytes(path, original); File.SetLastWriteTimeUtc(path, stamp);
        Assert.Equal(UnpackError.InputChanged, (await session.ExtractAsync(selection, w.Output, cancellationToken: Token)).Error?.Code);
        await using var fresh = await Open(path);
        Assert.Equal(UnpackError.OutputError, (await fresh.ExtractAsync(selection, w.Output, cancellationToken: Token)).Error?.Code);
        Assert.Equal(BrowseExtractState.Completed, (await fresh.ExtractAsync(Select(fresh, "data.txt"), w.Output, cancellationToken: Token)).State);
    }

    [Fact]
    public async Task 源删除要求重新加载且无输出()
    {
        using var w = new TestWorkspace(); var path = w.Zip("source.zip", ("data", [1]));
        await using var session = await Open(path); var selection = Select(session, "data"); File.Delete(path);
        var result = await session.ExtractAsync(selection, w.Output, cancellationToken: Token);
        Assert.Equal(UnpackError.InputChanged, result.Error?.Code); Assert.True(session.IsInvalidated); Assert.False(Directory.Exists(w.Output));
    }

    [Fact]
    public async Task 大量条目支持有限分页取消且不读取文件内容()
    {
        using var w = new TestWorkspace(); var path = w.Zip("many.zip", Enumerable.Range(0, 10_000).Select(i => ($"folder/{i:D5}.txt", new byte[] { (byte)i })).ToArray());
        await using var session = await Open(path);
        var first = session.Catalog.GetPage(".txt", cancellationToken: Token); var next = session.Catalog.GetPage(".txt", 200, cancellationToken: Token);
        Assert.Equal(10_000, first.TotalMatches); Assert.Equal(200, first.Entries.Count); Assert.True(first.HasNext);
        Assert.Empty(first.Entries.Select(e => e.Id).Intersect(next.Entries.Select(e => e.Id))); Assert.Equal(0, session.ExpandedBytes);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Token); cancel.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => session.Catalog.GetPage(".txt", cancellationToken: cancel.Token));
        using var during = CancellationTokenSource.CreateLinkedTokenSource(Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Open(path, progress: new SyncProgress(p => { if (p.Operation == BrowseOperation.ReadingDirectory && p.Entries > 1) during.Cancel(); }), token: during.Token));
        Assert.False(Directory.Exists(w.Output));
    }

    [Theory]
    [InlineData("entries")]
    [InlineData("read")]
    [InlineData("directory")]
    [InlineData("rows")]
    [InlineData("input")]
    public async Task 枚举预算在元数据构造和隐含父目录阶段生效(string kind)
    {
        using var w = new TestWorkspace(); var path = w.Zip("budget.zip", ("a/b/c/one", [1]), ("two", [2]), ("three", [3]));
        var limits = kind switch
        {
            "entries" => new BrowseLimits { Extraction = new() { MaxEntries = 2 } },
            "read" => new BrowseLimits { MaxReadBytes = 16 },
            "directory" => new BrowseLimits { MaxDirectoryBytes = 32 },
            "rows" => new BrowseLimits { MaxRows = 4 },
            _ => new BrowseLimits { Extraction = new() { MaxInputBytes = 16 } }
        };
        Assert.Equal(UnpackError.BudgetExceeded, (await Assert.ThrowsAsync<UnpackFailureException>(() => Open(path, limits))).Code);
        Assert.Single(Directory.GetFileSystemEntries(w.Root));
    }

    [Fact]
    public async Task 展开预算跨成功失败与再次执行累计()
    {
        using var w = new TestWorkspace(); var path = w.Zip("budget.zip", ("data", new byte[100]));
        await using var session = await Open(path, new() { Extraction = new() { MaxFileBytes = 150, MaxTotalBytes = 150 } });
        var first = await session.ExtractAsync(Select(session, "data"), w.Output, cancellationToken: Token);
        var second = await session.ExtractAsync(Select(session, "data"), w.Output, cancellationToken: Token);
        Assert.Equal(BrowseExtractState.Completed, first.State); Assert.Equal(UnpackError.BudgetExceeded, second.Error?.Code);
        Assert.True(second.ReadBytes > first.ReadBytes); Assert.Equal(100, second.ExpandedBytes);
        Assert.Single(Directory.GetDirectories(w.Output)); Assert.Null(second.CleanupWarning);
    }

    [Theory]
    [InlineData(BrowseOperation.Extracting)]
    [InlineData(BrowseOperation.Committing)]
    public async Task 写入或提交前取消清理暂存且保留已有产物(BrowseOperation phase)
    {
        using var w = new TestWorkspace(); var path = w.Zip("cancel.zip", ("data", new byte[5 * 1024 * 1024]));
        await using var session = await Open(path); var selection = Select(session, "data");
        var committed = await session.ExtractAsync(selection, w.Output, cancellationToken: Token);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var result = await session.ExtractAsync(selection, w.Output, progress: new SyncProgress(p => { if (p.Operation == phase) cancel.Cancel(); }), cancellationToken: cancel.Token);
        Assert.Equal(BrowseExtractState.Cancelled, result.State); Assert.Null(result.OutputDirectory); Assert.Null(result.CleanupWarning);
        Assert.Equal(new[] { committed.OutputDirectory }, Directory.GetDirectories(w.Output));
    }

    [Fact]
    public async Task 单会话互斥且释放必须等待实际清理()
    {
        using var w = new TestWorkspace(); var path = w.Zip("close.zip", ("data", new byte[5 * 1024 * 1024]));
        var session = await Open(path); var selection = Select(session, "data");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var work = session.ExtractAsync(selection, w.Output, progress: new SyncProgress(p =>
        { if (p.Operation == BrowseOperation.Extracting) { entered.TrySetResult(); release.Wait(Token); } }), cancellationToken: Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
            await Assert.ThrowsAsync<InvalidOperationException>(() => session.ExtractAsync(selection, w.Output, cancellationToken: Token));
            var close = session.DisposeAsync().AsTask(); Assert.False(close.IsCompleted); release.Set(); await close;
            Assert.Equal(BrowseExtractState.Cancelled, (await work).State); Assert.Empty(Directory.GetFileSystemEntries(w.Output));
            await Assert.ThrowsAsync<ObjectDisposedException>(() => session.ExtractAsync(selection, w.Output, cancellationToken: Token));
            await session.DisposeAsync();
        }
        finally { release.Set(); await session.DisposeAsync(); }
    }

    [Fact]
    public async Task 超时与取消不同且不提交()
    {
        using var w = new TestWorkspace(); var path = w.Zip("timeout.zip", ("data", [1]));
        await using var session = await Open(path, new() { Extraction = new() { ArchiveTimeout = TimeSpan.FromSeconds(1) } });
        var result = await session.ExtractAsync(Select(session, "data"), w.Output, progress: new SyncProgress(p =>
        { if (p.Operation == BrowseOperation.Committing) Thread.Sleep(1100); }), cancellationToken: Token);
        Assert.Equal(BrowseExtractState.Failed, result.State); Assert.Equal(UnpackError.Timeout, result.Error?.Code);
        Assert.Empty(Directory.GetFileSystemEntries(w.Output));
    }

    [Fact]
    public async Task 损坏正文仅在选择该内容时校验失败目录不冒充内容验证()
    {
        using var w = new TestWorkspace(); var path = w.Zip("crc.zip", ("bad.txt", "known-payload"u8.ToArray()), ("good.txt", [8]));
        var bytes = File.ReadAllBytes(path); var offset = bytes.AsSpan().IndexOf("known-payload"u8); Assert.True(offset > 0); bytes[offset] ^= 1; File.WriteAllBytes(path, bytes);
        await using var session = await Open(path);
        var bad = await session.ExtractAsync(Select(session, "bad.txt"), w.Output, cancellationToken: Token);
        Assert.Equal(UnpackError.CorruptArchive, bad.Error?.Code); Assert.Empty(Directory.GetFileSystemEntries(w.Output));
        var good = await session.ExtractAsync(Select(session, "good.txt"), w.Output, cancellationToken: Token);
        Assert.Equal(BrowseExtractState.Completed, good.State); Assert.Equal(new byte[] { 8 }, File.ReadAllBytes(Path.Combine(good.OutputDirectory!, "good.txt")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StoredAES含空文件必须校验认证尾部(bool empty)
    {
        using var w = new TestWorkspace(); var path = w.FilePath("aes.zip");
        using (var output = new ZipOutputStream(File.Create(path)))
        {
            output.Password = "public-R04"; output.SetLevel(0);
            output.PutNextEntry(new ZipEntry("data") { AESKeySize = 256, Size = empty ? 0 : 64, CompressionMethod = CompressionMethod.Stored });
            if (!empty) output.Write(new byte[64]); output.CloseEntry(); output.Finish();
        }
        long start, compressed;
        using (var zip = new ICSharpCode.SharpZipLib.Zip.ZipFile(File.OpenRead(path))) { var entry = zip[0]; start = entry.Offset; compressed = entry.CompressedSize; }
        var bytes = File.ReadAllBytes(path); var dataStart = (int)start + 30 + BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan((int)start + 26)) + BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan((int)start + 28));
        bytes[dataStart + (int)compressed - 1] ^= 1; File.WriteAllBytes(path, bytes);
        await using var session = await Open(path);
        var result = await session.ExtractAsync(Select(session, "data"), w.Output, "public-R04", cancellationToken: Token);
        Assert.Equal(UnpackError.PasswordRequiredOrInvalid, result.Error?.Code); Assert.Empty(Directory.GetFileSystemEntries(w.Output));
    }

    [Fact]
    public async Task 空ZIP可浏览且无选择不能创建产物()
    {
        using var w = new TestWorkspace(); await using var session = await Open(w.Zip("empty.zip"));
        Assert.Empty(session.Catalog.GetPage(cancellationToken: Token).Entries);
        var result = await session.ExtractAsync(new([]), w.Output, cancellationToken: Token);
        Assert.Equal(BrowseExtractState.Failed, result.State); Assert.False(Directory.Exists(w.Output));
    }

    [Fact]
    public async Task 伪报中心目录条目数不会被当作完整清单()
    {
        using var w = new TestWorkspace(); var path = w.Zip("bad-directory.zip", ("one", [1]), ("two", [2]));
        var bytes = File.ReadAllBytes(path); BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(bytes.Length - 22 + 8), 1); BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(bytes.Length - 22 + 10), 1); File.WriteAllBytes(path, bytes);
        Assert.Equal(UnpackError.CorruptArchive, (await Assert.ThrowsAsync<UnpackFailureException>(() => Open(path))).Code);
    }

    private sealed class SyncProgress(Action<BrowseProgress> action) : IProgress<BrowseProgress>
    { public void Report(BrowseProgress value) => action(value); }
}

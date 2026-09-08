using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ICSharpCode.SharpZipLib.Zip;
using LayerUnpackPlugin.Headless.Application;
using LayerUnpackPlugin.Headless.Contracts;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Readers;
using Xunit;

namespace LayerUnpackPlugin.Headless.Tests;

/// <summary>每个压缩级别均使用真实内容、零字节条目和独立 SharpCompress 读取器，不以模拟解密代替格式证据。</summary>
public sealed class PackEncryptionTests
{
    private const string Password = "public-R03-中文-password";
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false, PackCompression.Standard)]
    [InlineData(false, PackCompression.Fast)]
    [InlineData(false, PackCompression.High)]
    [InlineData(false, PackCompression.Store)]
    [InlineData(true, PackCompression.Standard)]
    [InlineData(true, PackCompression.Fast)]
    [InlineData(true, PackCompression.High)]
    [InlineData(true, PackCompression.Store)]
    public async Task 四级偏好普通与AES均保持中文空文件空目录和随机内容(bool encrypted, PackCompression compression)
    {
        using var w = new TestWorkspace(); PackTests.Source(w, "资料/随机.bin", RandomNumberGenerator.GetBytes(65536));
        PackTests.Source(w, "资料/空.txt", []); PackTests.Source(w, "资料/中文.txt", Encoding.UTF8.GetBytes(new string('中', 32768)));
        Directory.CreateDirectory(w.FilePath("资料/空目录"));
        var service = new PackService();
        var plan = await service.PrepareAsync(new([w.FilePath("资料")], Path.Combine(w.Output, "创建.zip"), options: new() { Encrypt = encrypted, Compression = compression }), cancellationToken: Token);
        using var secret = encrypted ? new PackSecret(Password) : null;
        var result = await service.ExecuteAsync(plan, cancellationToken: Token, secret: secret);
        Assert.Equal(PackState.Completed, result.State); Assert.Null(result.CleanupWarning);
        using (var zip = new ZipFile(File.OpenRead(result.OutputPath!)))
        {
            foreach (var entry in zip.Cast<ZipEntry>().Where(e => !e.IsDirectory))
            {
                Assert.Equal(encrypted ? 256 : 0, entry.AESKeySize); Assert.Equal(encrypted, entry.IsCrypted);
                if ((!encrypted && compression == PackCompression.Store) || entry.Size == 0) Assert.Equal(CompressionMethod.Stored, entry.CompressionMethod);
                else Assert.Equal(CompressionMethod.Deflated, entry.CompressionMethod);
            }
        }
        await using var unpack = new UnpackService().CreateSession(new([result.OutputPath!], w.FilePath("readback"), passwords: encrypted ? [Password] : []));
        var readback = await unpack.ExecuteAsync(cancellationToken: Token);
        Assert.Equal(BatchState.Completed, readback.State);
        foreach (var entry in plan.Entries.Where(e => !e.IsDirectory))
            Assert.Equal(entry.Sha256, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(readback.Nodes[0].OutputDirectory!, entry.EntryName)))));
        Assert.Equal(plan.TotalBytes, result.SourceBytes); Assert.Equal(new FileInfo(result.OutputPath!).Length, result.ArchiveBytes);
        await CheckIndependentReader(plan, result.OutputPath!, encrypted ? Password : null);
    }

    private static async Task CheckIndependentReader(PackPlan plan, string path, string? password)
    {
        await using var input = File.OpenRead(path);
        await using var archive = await ArchiveFactory.OpenAsyncArchive(input,
            new ReaderOptions { Password = password, ArchiveEncoding = new ArchiveEncoding { Default = Encoding.UTF8 } }, Token);
        var names = new List<string>();
        await foreach (var entry in archive.EntriesAsync.WithCancellation(Token))
        {
            names.Add(entry.Key!);
            if (entry.IsDirectory) continue;
            var expected = Assert.Single(plan.Entries, e => e.EntryName == entry.Key);
            await using var content = await entry.OpenEntryStreamAsync(Token);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[131072];
            int count; long length = 0;
            while ((count = await content.ReadAsync(buffer, 0, buffer.Length, Token)) != 0) { hash.AppendData(buffer, 0, count); length += count; }
            Assert.True(expected.Length == length, $"{entry.Key}: expected {expected.Length}, actual {length}");
            Assert.Equal(expected.Sha256, Convert.ToHexString(hash.GetHashAndReset()));
        }
        Assert.Equal(plan.Entries.Select(e => e.EntryName + (e.IsDirectory ? "/" : "")).Order(), names.Order());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("wrong-public-password")]
    public async Task 新创建AES在无密码与错误密码时不提交任何解压结果(string? password)
    {
        using var w = new TestWorkspace(); var path = await CreateEncrypted(w, new byte[65536]);
        await using var unpack = new UnpackService().CreateSession(new([path], w.FilePath("readback"), passwords: password is null ? [] : [password]));
        var result = await unpack.ExecuteAsync(cancellationToken: Token);
        Assert.NotEqual(BatchState.Completed, result.State); Assert.Equal(UnpackError.PasswordRequiredOrInvalid, result.Nodes[0].Error?.Code);
        Assert.Empty(Directory.GetFileSystemEntries(w.FilePath("readback")));
    }

    [Theory]
    [InlineData(0, PackCompression.Standard)]
    [InlineData(65536, PackCompression.Standard)]
    [InlineData(65536, PackCompression.Store)]
    public async Task 新创建AES认证码损坏即使密码正确也不得成功(int size, PackCompression compression)
    {
        using var w = new TestWorkspace(); var path = await CreateEncrypted(w, RandomNumberGenerator.GetBytes(size), compression);
        await CorruptAuthentication(path);
        await using var unpack = new UnpackService().CreateSession(new([path], w.FilePath("readback"), passwords: [Password]));
        var result = await unpack.ExecuteAsync(cancellationToken: Token);
        Assert.Equal(BatchState.Failed, result.State); Assert.Empty(Directory.GetFileSystemEntries(w.FilePath("readback")));
    }

    private static async Task CorruptAuthentication(string path)
    {
        var bytes = await File.ReadAllBytesAsync(path, Token);
        // 使用中央目录给出的压缩长度和本地头偏移，准确翻转认证码最后一字节，不把截断或密码校验失败冒充认证测试。
        var central = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(bytes.Length - 22 + 16)));
        Assert.True(bytes.AsSpan(central).StartsWith("PK\x01\x02"u8));
        Assert.Equal(99, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(central + 10)));
        var compressed = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(central + 20)));
        var local = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(central + 42)));
        var headerLength = 30 + BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(local + 26)) + BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(local + 28));
        bytes[local + headerLength + compressed - 1] ^= 1;
        await File.WriteAllBytesAsync(path, bytes, Token);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65536)]
    public async Task 外部StoredAES必须通过专用读取重载验证内容和认证尾部(int size)
    {
        using var w = new TestWorkspace(); var path = w.FilePath("stored.zip"); var data = RandomNumberGenerator.GetBytes(size);
        using (var zip = new ZipOutputStream(File.Create(path)) { Password = Password })
        {
            zip.PutNextEntry(new("file.bin") { AESKeySize = 256, Size = size, CompressionMethod = CompressionMethod.Stored });
            zip.Write(data, 0, data.Length); zip.CloseEntry();
        }
        await using (var valid = new UnpackService().CreateSession(new([path], w.FilePath("valid"), passwords: [Password])))
        {
            var result = await valid.ExecuteAsync(cancellationToken: Token);
            Assert.Equal(BatchState.Completed, result.State);
            Assert.Equal(data, await File.ReadAllBytesAsync(Path.Combine(result.Nodes[0].OutputDirectory!, "file.bin"), Token));
        }
        await CorruptAuthentication(path);
        await using var unpack = new UnpackService().CreateSession(new([path], w.FilePath("readback"), passwords: [Password]));
        Assert.Equal(BatchState.Failed, (await unpack.ExecuteAsync(cancellationToken: Token)).State);
        Assert.Empty(Directory.GetFileSystemEntries(w.FilePath("readback")));
    }

    [Fact]
    public async Task 同一批次各目标使用同一显式密码而盐值不重复()
    {
        using var w = new TestWorkspace(); var a = PackTests.Source(w, "a.txt"); var b = PackTests.Source(w, "b.txt");
        var service = new PackBatchService();
        var plan = await service.PrepareAsync(new([a, b], w.Output, grouping: PackGrouping.Separate, options: new() { Encrypt = true }), cancellationToken: Token);
        await using var session = service.CreateSession(plan, new(Password));
        var result = await session.ExecuteAsync(cancellationToken: Token); Assert.Equal(2, result.CompletedCount);
        var salts = new List<byte[]>();
        foreach (var group in result.Groups)
        {
            await CheckIndependentReader(plan.Groups[group.Index].Plan!, group.Result.OutputPath!, Password);
            var bytes = await File.ReadAllBytesAsync(group.Result.OutputPath!, Token);
            var header = 30 + BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(26)) + BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(28));
            salts.Add(bytes.AsSpan(header, 16).ToArray());
        }
        Assert.NotEqual(salts[0], salts[1]);
    }

    [Fact]
    public async Task 两个创建会话和解压会话不继承密码且序列化不含秘密()
    {
        using var w = new TestWorkspace(); var source = PackTests.Source(w, "source.txt");
        var service = new PackBatchService();
        var plan = await service.PrepareAsync(new([source], w.Output, options: new() { Encrypt = true }), cancellationToken: Token);
        using var firstSecret = new PackSecret(Password); using var secondSecret = new PackSecret("second-public-password");
        await using var first = service.CreateSession(plan, firstSecret); await using var second = service.CreateSession(plan, secondSecret);
        var results = await Task.WhenAll(first.ExecuteAsync(cancellationToken: Token), second.ExecuteAsync(cancellationToken: Token));
        Assert.All(results, r => Assert.Equal(1, r.CompletedCount));
        var json = JsonSerializer.Serialize(new { plan, request = plan.Request, first.CurrentResult, secret = firstSecret });
        Assert.DoesNotContain(Password, json); Assert.DoesNotContain(Password, firstSecret.ToString()); Assert.DoesNotContain("second-public-password", json);
        await CheckIndependentReader(plan.Groups[0].Plan!, results[0].Groups[0].Result.OutputPath!, Password);
        await CheckIndependentReader(plan.Groups[0].Plan!, results[1].Groups[0].Result.OutputPath!, "second-public-password");
        await using var unpack = new UnpackService().CreateSession(new([results[0].Groups[0].Result.OutputPath!], w.FilePath("readback"), passwords: ["second-public-password"]));
        Assert.NotEqual(BatchState.Completed, (await unpack.ExecuteAsync(cancellationToken: Token)).State);
        Assert.NotEqual(File.ReadAllBytes(results[0].Groups[0].Result.OutputPath!), File.ReadAllBytes(results[1].Groups[0].Result.OutputPath!));
    }

    [Fact]
    public async Task 加密开关和秘密不一致或秘密已释放时绝不静默写出明文()
    {
        using var w = new TestWorkspace(); var source = PackTests.Source(w, "source.txt"); var service = new PackService();
        var encrypted = await service.PrepareAsync(new([source], w.FilePath("encrypted.zip"), options: new() { Encrypt = true }), cancellationToken: Token);
        var plain = await service.PrepareAsync(new([source], w.FilePath("plain.zip")), cancellationToken: Token);
        using var secret = new PackSecret(Password);
        await Assert.ThrowsAsync<PackValidationException>(() => service.ExecuteAsync(encrypted, cancellationToken: Token));
        await Assert.ThrowsAsync<PackValidationException>(() => service.ExecuteAsync(plain, cancellationToken: Token, secret: secret));
        secret.Dispose();
        await Assert.ThrowsAsync<PackValidationException>(() => service.ExecuteAsync(encrypted, cancellationToken: Token, secret: secret));
        Assert.False(File.Exists(w.FilePath("encrypted.zip"))); Assert.False(File.Exists(w.FilePath("plain.zip")));
    }

    [Theory]
    [InlineData("writing")]
    [InlineData("finalizing")]
    [InlineData("budget")]
    public async Task AES写入取消收尾取消与输出预算均回滚(string kind)
    {
        using var w = new TestWorkspace(); var source = PackTests.Source(w, "source.bin", RandomNumberGenerator.GetBytes(512 * 1024));
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var service = new PackService();
        var plan = await service.PrepareAsync(new([source], Path.Combine(w.Output, "result.zip"),
            new() { MaxArchiveBytes = kind == "budget" ? 32 : 1024 * 1024 }, new() { Encrypt = true }), cancellationToken: Token);
        using var secret = new PackSecret(Password);
        var result = await service.ExecuteAsync(plan, new ProgressRelay(p =>
        {
            if ((kind == "writing" && p.State == PackState.Writing) || (kind == "finalizing" && p.State == PackState.Finalizing)) cancel.Cancel();
        }), cancel.Token, secret);
        if (kind == "budget") Assert.Equal(PackError.BudgetExceeded, result.Error?.Code);
        else Assert.Equal(PackState.Cancelled, result.State);
        Assert.Null(result.CleanupWarning); Assert.Empty(Directory.GetFiles(w.Output));
    }

    [Fact]
    public async Task 零字节和不可压缩数据只呈现真实字节不承诺节省空间()
    {
        using var w = new TestWorkspace(); var empty = PackTests.Source(w, "empty.txt", []); var random = PackTests.Source(w, "random.bin", RandomNumberGenerator.GetBytes(65536));
        var service = new PackBatchService();
        var plan = await service.PrepareAsync(new([empty, random], w.Output, grouping: PackGrouping.Separate, options: new() { Encrypt = true, Compression = PackCompression.Store }), cancellationToken: Token);
        await using var session = service.CreateSession(plan, new(Password));
        var result = await session.ExecuteAsync(cancellationToken: Token);
        Assert.Equal(65536, result.SourceBytes); Assert.True(result.ArchiveBytes > result.SourceBytes);
        Assert.Equal(0, result.Groups[0].Result.SourceBytes); Assert.True(result.Groups[0].Result.ArchiveBytes > 0);
    }

    private static async Task<string> CreateEncrypted(TestWorkspace w, byte[] data, PackCompression compression = PackCompression.Standard)
    {
        var service = new PackService(); var source = PackTests.Source(w, "file.bin", data);
        var plan = await service.PrepareAsync(new([source], w.FilePath("encrypted.zip"), options: new() { Encrypt = true, Compression = compression }), cancellationToken: Token);
        using var secret = new PackSecret(Password);
        var result = await service.ExecuteAsync(plan, cancellationToken: Token, secret: secret);
        Assert.Equal(PackState.Completed, result.State); return result.OutputPath!;
    }
    private sealed class ProgressRelay(Action<PackProgress> action) : IProgress<PackProgress>
    { public void Report(PackProgress value) => action(value); }
}

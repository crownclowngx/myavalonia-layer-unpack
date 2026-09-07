using System.Buffers.Binary;
using LayerUnpackPlugin.Headless.Application;
using LayerUnpackPlugin.Headless.Contracts;
using Xunit;

namespace LayerUnpackPlugin.Headless.Tests;

public sealed class IntegrityTests
{
    [Fact]
    public async Task 同包明文和AES条目补密后只提交完整结果()
    {
        using var w = new TestWorkspace();
        var path = w.FilePath("mixed.zip");
        using (var zip = new ICSharpCode.SharpZipLib.Zip.ZipOutputStream(File.Create(path)))
        {
            zip.PutNextEntry(new ICSharpCode.SharpZipLib.Zip.ZipEntry("plain.txt") { Size = 5 });
            zip.Write("plain"u8); zip.CloseEntry();
            zip.Password = "public-test";
            zip.PutNextEntry(new ICSharpCode.SharpZipLib.Zip.ZipEntry("secret.txt") { Size = 6, AESKeySize = 256 });
            zip.Write("secret"u8); zip.CloseEntry();
        }
        await using var session = new UnpackService().CreateSession(new([path], w.Output, passwords: ["public-test"]));
        var result = await session.ExecuteAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(BatchState.Completed, result.State);
        Assert.Equal(2, result.AttemptCount);
        Assert.Equal(16, result.TotalWrittenBytes);
        Assert.Equal("secret", await File.ReadAllTextAsync(Path.Combine(result.Nodes[0].OutputDirectory!, "secret.txt"), TestContext.Current.CancellationToken));
        Assert.Single(Directory.GetDirectories(w.Output));
    }
    [Fact]
    public async Task 旧ZIP错误密码统一为可重试诊断()
    {
        using var w = new TestWorkspace();
        var input = w.CopyFixture("Zip.deflate.pkware.zip");
        Directory.CreateDirectory(w.Output);
        var failure = await Assert.ThrowsAsync<UnpackFailureException>(() => new LayerUnpackPlugin.Headless.Infrastructure.ArchiveExtractor()
            .ExtractAsync(input, w.Output, "wrong-first", LegacyNameEncoding.Cp866, new(new()), _ => { }, TestContext.Current.CancellationToken));
        Assert.Equal(UnpackError.PasswordRequiredOrInvalid, failure.Code);
    }
    [Fact]
    public async Task ZIP的零CRC不是跳过校验的标记()
    {
        using var w = new TestWorkspace();
        var path = w.Zip("zero-crc.zip", ("file", "non-empty-content"u8.ToArray()));
        var bytes = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        bytes.AsSpan(14, 4).Clear();
        var central = bytes.AsSpan().IndexOf("PK\x01\x02"u8);
        Assert.True(central > 0); bytes.AsSpan(central + 16, 4).Clear();
        await File.WriteAllBytesAsync(path, bytes, TestContext.Current.CancellationToken);
        await using var session = new UnpackService().CreateSession(new([path], w.Output));
        var result = await session.ExecuteAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(BatchState.Failed, result.State);
        Assert.Empty(Directory.GetFileSystemEntries(w.Output));
    }

    [Fact]
    public async Task AES认证尾部损坏不能绕过完整性验证()
    {
        using var w = new TestWorkspace();
        var path = w.CopyFixture("Zip.deflate.WinzipAES2.zip");
        var bytes = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        var modified = false;
        for (var offset = 0; offset < bytes.Length - 30; offset++)
        {
            if (!bytes.AsSpan(offset).StartsWith("PK\x03\x04"u8) || BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset + 8)) != 99) continue;
            var compressed = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + 18)));
            var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset + 26));
            var extraLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset + 28));
            Assert.True(compressed > 10);
            var authenticationEnd = offset + 30 + nameLength + extraLength + compressed - 1;
            Assert.InRange(authenticationEnd, 0, bytes.Length - 1);
            bytes[authenticationEnd] ^= 1; modified = true; break;
        }
        Assert.True(modified);
        await File.WriteAllBytesAsync(path, bytes, TestContext.Current.CancellationToken);
        await using var session = new UnpackService().CreateSession(new([path], w.Output, passwords: ["test"]));
        var result = await session.ExecuteAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(BatchState.Failed, result.State);
        Assert.Empty(Directory.GetFileSystemEntries(w.Output));
    }

    [Fact]
    public async Task 未支持的压缩算法返回受限诊断()
    {
        using var w = new TestWorkspace();
        var path = w.Zip("method.zip", ("file", "content"u8.ToArray()));
        var bytes = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(8), 999);
        var central = bytes.AsSpan().IndexOf("PK\x01\x02"u8);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(central + 10), 999);
        await File.WriteAllBytesAsync(path, bytes, TestContext.Current.CancellationToken);
        await using var session = new UnpackService().CreateSession(new([path], w.Output));
        var result = await session.ExecuteAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(UnpackError.UnsupportedEncryption, result.Nodes[0].Error?.Code);
        Assert.Empty(Directory.GetFileSystemEntries(w.Output));
    }
}

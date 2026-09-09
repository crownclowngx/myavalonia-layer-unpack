using System.Buffers.Binary;
using System.Text.Json;
using LayerUnpackPlugin.Headless.Application;
using LayerUnpackPlugin.Headless.Contracts;
using LayerUnpackPlugin.Headless.Infrastructure;
using Xunit;

namespace LayerUnpackPlugin.Headless.Tests;

public sealed class ArchiveCheckTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    [Theory]
    [InlineData("Generated.single.txt.gz", 1)]
    [InlineData("Generated.single.txt.gz", 8)]
    [InlineData("Generated.utf8.tar.gz", 4)]
    public async Task GZip缺失校验尾部不能用解码EOF冒充完整(string fixture, int removed)
    {
        using var w = new TestWorkspace(); var path = w.CopyFixture(fixture); var bytes = File.ReadAllBytes(path);
        File.WriteAllBytes(path, bytes[..^removed]);
        var result = await Check(w, path); Assert.Equal(ArchiveCheckState.Failed, result.State); Assert.Empty(result.Evidence);
        Assert.Equal(UnpackError.CorruptArchive, result.Error?.Code); Assert.Empty(Directory.GetFileSystemEntries(w.Output));
    }
    private static Task<ArchiveCheckResult> Check(TestWorkspace w, string path, ArchiveCheckScope scope = ArchiveCheckScope.FullContent,
        string[]? passwords = null, UnpackLimits? limits = null) => new ArchiveCheckService().CheckAsync(
            new(path, scope, passwords, limits, LegacyNameEncoding.Cp866, w.Output), cancellationToken: Token);

    [Fact]
    public async Task 可列目录的损坏正文完整检查失败且不留下文件()
    {
        using var w = new TestWorkspace(); var path = w.Zip("bad.zip", ("a.txt", "original-body"u8.ToArray()));
        var bytes = File.ReadAllBytes(path); var position = bytes.AsSpan().IndexOf("original-body"u8); Assert.True(position > 0);
        bytes[position] ^= 1; File.WriteAllBytes(path, bytes);
        var directory = await Check(w, path, ArchiveCheckScope.DirectoryOnly);
        Assert.Equal(ArchiveCheckState.DirectoryRead, directory.State); Assert.Empty(directory.Evidence); Assert.False(Directory.Exists(w.Output));
        var full = await Check(w, path);
        Assert.Equal(ArchiveCheckState.Failed, full.State); Assert.Equal(UnpackError.CorruptArchive, full.Error?.Code); Assert.Empty(full.Evidence);
        Assert.Empty(Directory.GetFileSystemEntries(w.Output));
    }

    [Theory]
    [InlineData("Zip.deflate.zip", null, "CRC32")]
    [InlineData("Zip.deflate.pkware.zip", "12345678", "CRC32")]
    [InlineData("Zip.deflate.WinzipAES2.zip", "test", "AES 认证码")]
    [InlineData("7Zip.LZMA.7z", null, "CRC32")]
    [InlineData("7Zip.LZMA2.Aes.7z", "testpassword", "CRC32")]
    [InlineData("7Zip.solid.7z", null, "CRC32")]
    [InlineData("Rar4.rar", null, "CRC32")]
    [InlineData("Rar5.rar", null, "CRC32")]
    [InlineData("Rar.encrypted_filesAndHeader.rar", "test", "CRC32")]
    public async Task 已验证格式真实检查证据与临时清理(string fixture, string? password, string evidence)
    {
        using var w = new TestWorkspace(); var path = w.CopyFixture(fixture);
        var result = await Check(w, path, passwords: password is null ? [] : ["wrong-first", password]);
        Assert.True(result.State == ArchiveCheckState.Completed, JsonSerializer.Serialize(result));
        Assert.True(result.FilesRead > 0); Assert.Contains(result.Evidence, e => e.Kind == evidence && e.Files > 0);
        Assert.Empty(Directory.GetFileSystemEntries(w.Output));
        using var exclusive = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None); Assert.True(exclusive.CanRead);
    }

    [Theory]
    [InlineData("Rar5.encrypted_filesOnly.rar", "test")]
    [InlineData("Rar5.encrypted_filesAndHeader.rar", "test")]
    [InlineData("Generated.utf8.tar", null)]
    [InlineData("Generated.utf8.tar.gz", null)]
    [InlineData("Generated.single.txt.xz", null)]
    public async Task 受限校验不得因正常解码显示为完全通过(string fixture, string? password)
    {
        using var w = new TestWorkspace(); var result = await Check(w, w.CopyFixture(fixture), passwords: password is null ? [] : [password]);
        Assert.Equal(ArchiveCheckState.CompletedWithLimitations, result.State); Assert.NotEmpty(result.Limitations);
        if (fixture.StartsWith("Rar5")) { Assert.Contains(result.Limitations, v => v.Contains("未验证")); Assert.DoesNotContain(result.Evidence, e => e.Kind.Contains("CRC") || e.Kind.Contains("认证")); }
        Assert.Empty(Directory.GetFileSystemEntries(w.Output));
    }

    [Theory]
    [InlineData("Rar5.multi.part01.rar", UnpackError.MissingVolume)]
    [InlineData("7Zip.BZip2.split.001", UnpackError.MissingVolumeOrCorruptArchive)]
    public async Task 缺卷诊断不宣称支持拼接(string fixture, UnpackError code)
    {
        using var w = new TestWorkspace(); var result = await Check(w, w.CopyFixture(fixture));
        Assert.Equal(code, result.Error?.Code); Assert.Contains("不能拼接", result.Error!.NextStep);
        Assert.Empty(result.Evidence); Assert.Empty(Directory.GetFileSystemEntries(w.Output));
    }

    [Fact]
    public async Task 密码歧义与所有诊断都有下一步且秘密不泄漏()
    {
        using var w = new TestWorkspace(); const string secret = "private-r07-sentinel";
        var request = new ArchiveCheckRequest(w.CopyFixture("Zip.deflate.WinzipAES2.zip"), passwords: [secret], temporaryDirectory: w.Output);
        var result = await new ArchiveCheckService().CheckAsync(request, cancellationToken: Token);
        Assert.Equal(UnpackError.PasswordRequiredOrInvalid, result.Error?.Code); Assert.Contains("损坏", result.Error!.NextStep);
        Assert.DoesNotContain(secret, JsonSerializer.Serialize(request) + request + JsonSerializer.Serialize(result));
        foreach (var code in Enum.GetValues<UnpackError>()) Assert.False(string.IsNullOrWhiteSpace(new UnpackDiagnostic(code, "test").NextStep));
    }

    [Fact]
    public async Task 非ZIP目录检查拒绝且不会解码或创建临时目录()
    {
        using var w = new TestWorkspace(); var result = await Check(w, w.CopyFixture("Rar5.rar"), ArchiveCheckScope.DirectoryOnly);
        Assert.Equal(UnpackError.UnsupportedFormat, result.Error?.Code); Assert.Equal(0, result.ExpandedBytes); Assert.False(Directory.Exists(w.Output));
    }

    [Fact]
    public async Task 资源上限条目上限非法请求与不存在来源分别报告()
    {
        using var w = new TestWorkspace(); var path = w.Zip("a.zip", ("a", new byte[100]), ("b", [1]));
        foreach (var limits in new[] { new UnpackLimits { MaxInputBytes = 1 }, new UnpackLimits { MaxEntries = 1 }, new UnpackLimits { MaxFileBytes = 10 } })
            Assert.Equal(UnpackError.BudgetExceeded, (await Check(w, path, limits: limits)).Error?.Code);
        Assert.Equal(UnpackError.InputUnavailable, (await Check(w, w.FilePath("missing.zip"))).Error?.Code);
        await Assert.ThrowsAsync<UnpackValidationException>(() => new ArchiveCheckService().CheckAsync(new("relative.zip"), cancellationToken: Token));
        await Assert.ThrowsAsync<UnpackValidationException>(() => new ArchiveCheckService().CheckAsync(new(path, (ArchiveCheckScope)42), cancellationToken: Token));
        if (Directory.Exists(w.Output)) Assert.Empty(Directory.GetFileSystemEntries(w.Output));
    }

    [Fact]
    public async Task ZIP卷号与旧名称编码故障可准确诊断()
    {
        using var w = new TestWorkspace(); var path = w.Zip("volume.zip", ("a", [1]));
        var bytes = File.ReadAllBytes(path); var end = bytes.AsSpan().LastIndexOf("PK\x05\x06"u8);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(end + 4), 1); File.WriteAllBytes(path, bytes);
        Assert.Equal(UnpackError.MissingVolume, (await Check(w, path)).Error?.Code);
        path = w.Zip("encoding.zip", ("a", [1])); bytes = File.ReadAllBytes(path);
        var central = bytes.AsSpan().IndexOf("PK\x01\x02"u8);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(6), 0); BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(central + 8), 0);
        bytes[30] = 0xff; bytes[central + 46] = 0xff; File.WriteAllBytes(path, bytes);
        var result = await new ArchiveCheckService().CheckAsync(new(path, nameEncoding: LegacyNameEncoding.Utf8, temporaryDirectory: w.Output), cancellationToken: Token);
        Assert.Equal(UnpackError.InvalidNameEncoding, result.Error?.Code);
    }

    [Fact]
    public async Task 检查不递归展开内嵌包且空包不捏造校验数量()
    {
        using var w = new TestWorkspace(); var path = w.Zip("nested.zip", ("bad.zip", "not-an-archive"u8.ToArray()));
        var result = await Check(w, path); Assert.Equal(ArchiveCheckState.Completed, result.State); Assert.Equal(1, result.FilesRead);
        Assert.Contains(result.Limitations, x => x.Contains("未检查其内部"));
        path = w.Zip("empty.zip"); result = await Check(w, path);
        Assert.Equal(ArchiveCheckState.Completed, result.State); Assert.Equal(0, result.FilesRead); Assert.Empty(result.Evidence);
    }
}

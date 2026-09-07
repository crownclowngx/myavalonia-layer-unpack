using System.Security.Cryptography;
using System.Text.Json;
using LayerUnpackPlugin.Headless.Application;
using LayerUnpackPlugin.Headless.Contracts;
using Xunit;

namespace LayerUnpackPlugin.Headless.Tests;

public sealed class FormatTests
{
    [Theory]
    [InlineData("Zip.deflate.zip", null)]
    [InlineData("Zip.deflate.pkware.zip", "12345678")]
    [InlineData("Zip.deflate.WinzipAES.zip", "test")]
    [InlineData("Zip.deflate.WinzipAES2.zip", "test")]
    [InlineData("7Zip.LZMA.7z", null)]
    [InlineData("7Zip.LZMA.Aes.7z", "testpassword")]
    [InlineData("7Zip.LZMA2.Aes.7z", "testpassword")]
    [InlineData("7Zip.solid.7z", null)]
    [InlineData("Rar4.rar", null)]
    [InlineData("Rar5.rar", null)]
    [InlineData("Rar.solid.rar", null)]
    [InlineData("Rar5.solid.rar", null)]
    [InlineData("Rar.encrypted_filesAndHeader.rar", "test")]
    [InlineData("Rar.encrypted_filesOnly.rar", "test")]
    [InlineData("Rar5.encrypted_filesAndHeader.rar", "test")]
    [InlineData("Rar5.encrypted_filesOnly.rar", "test")]
    public async Task 真实压缩包所有输出内容与上游原文件摘要相同(string fixture, string? password)
    {
        using var workspace = new TestWorkspace();
        var input = workspace.CopyFixture(fixture);
        await using var session = new UnpackService().CreateSession(new UnpackRequest([input], workspace.Output,
            passwords: password is null ? [] : ["wrong-first", password], legacyNameEncoding: LegacyNameEncoding.Cp866));
        var result = await session.ExecuteAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(result.State == BatchState.Completed, JsonSerializer.Serialize(result));
        var node = Assert.Single(result.Nodes);
        Assert.Equal(NodeState.Extracted, node.State);
        var expected = JsonSerializer.Deserialize<Dictionary<string, string>>(
            await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "expected-content.json"), cancellationToken: TestContext.Current.CancellationToken))!;
        var files = Directory.GetFiles(node.OutputDirectory!, "*", SearchOption.AllDirectories);
        if (fixture == "Zip.deflate.pkware.zip")
        {
            Assert.Equal(Path.Combine(node.OutputDirectory!, "Folder", "File.txt"), Assert.Single(files));
            Assert.Equal("bla-bla-bla-bla-bla", await File.ReadAllTextAsync(files[0], cancellationToken: TestContext.Current.CancellationToken));
            return;
        }
        Assert.Equal(expected.Count, files.Length);
        foreach (var file in files)
        {
            var relative = Path.GetRelativePath(node.OutputDirectory!, file).Replace('\\', '/');
            if (fixture == "Zip.deflate.WinzipAES2.zip")
            {
                Assert.StartsWith("Zip.deflate.WinzipAES2/", relative, StringComparison.Ordinal);
                relative = Path.GetExtension(file) switch { ".exe" => "exe/test.exe", ".jpg" => "jpg/test.jpg", _ => "тест.txt" };
            }
            Assert.True(expected.TryGetValue(relative, out var digest), relative);
            Assert.Equal(digest, Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(file, cancellationToken: TestContext.Current.CancellationToken))));
        }
        Assert.Empty(Directory.GetDirectories(workspace.Output, ".layer-unpack-*"));
        Assert.Equal(fixture.StartsWith("Rar5.encrypted", StringComparison.Ordinal), node.Warning is not null);
    }
}

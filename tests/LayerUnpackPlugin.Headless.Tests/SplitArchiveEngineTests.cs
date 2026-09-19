using System.Security.Cryptography;
using System.Text.Json;
using SharpCompress.Archives.SevenZip;
using SharpCompress.Readers;
using Xunit;

namespace LayerUnpackPlugin.Headless.Tests;

/// <summary>用独立 7-Zip 生成的分卷证明锁定读取器支持明确的有序流；不是由待测 Writer 自证格式。</summary>
public sealed class SplitArchiveEngineTests
{
    [Theory]
    [InlineData("plain", null)]
    [InlineData("solid", null)]
    [InlineData("content-encrypted", "volume-test")]
    [InlineData("header-encrypted", "volume-test")]
    public async Task 显式分卷流读取与独立原始清单一致(string name, string? password)
    {
        var token = TestContext.Current.CancellationToken;
        var root = Path.Combine(AppContext.BaseDirectory, "Fixtures", "G0015");
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, "manifest.json"), token));
        var expected = manifest.RootElement.GetProperty("expected").EnumerateArray().ToDictionary(e => e.GetProperty("path").GetString()!);
        var paths = Directory.GetFiles(root, name + ".7z.*").Order(StringComparer.Ordinal).ToArray();
        var streams = new List<Stream>();
        try
        {
            foreach (var path in paths)
            {
                var volume = manifest.RootElement.GetProperty("volumes").EnumerateArray().Single(e => e.GetProperty("name").GetString() == Path.GetFileName(path));
                Assert.Equal(volume.GetProperty("sha256").GetString(), Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path, token))));
                streams.Add(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read));
            }
            var seen = new HashSet<string>(StringComparer.Ordinal);
            await using (var archive = await SevenZipArchive.OpenAsyncArchive(streams, new ReaderOptions { Password = password, LeaveStreamOpen = true }, token))
            await using (var reader = await archive.ExtractAllEntriesAsync())
            {
                while (await reader.MoveToNextEntryAsync(token))
                {
                    var entry = reader.Entry;
                    var key = entry.Key!.Replace('\\', '/').TrimEnd('/');
                    Assert.True(seen.Add(key));
                    Assert.True(expected.TryGetValue(key, out var original), key);
                    Assert.Equal(original.GetProperty("directory").GetBoolean(), entry.IsDirectory);
                    if (entry.IsDirectory) continue;
                    await using var content = await reader.OpenEntryStreamAsync(token);
                    Assert.Equal(original.GetProperty("sha256").GetString(), Convert.ToHexString(await SHA256.HashDataAsync(content, token)));
                }
            }
            Assert.Equal(expected.Keys.Order(), seen.Order());
            // 引擎借用流，来源所有者在所有密码尝试结束后统一释放，防止第一次失败误关其他卷。
            Assert.All(streams, stream => Assert.True(stream.CanRead));
        }
        finally { foreach (var stream in streams) await stream.DisposeAsync(); }
    }
}

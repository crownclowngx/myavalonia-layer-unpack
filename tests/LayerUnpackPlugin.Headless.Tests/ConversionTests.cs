using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using LayerUnpackPlugin.Headless.Application;
using LayerUnpackPlugin.Headless.Contracts;
using LayerUnpackPlugin.Headless.Domain;
using LayerUnpackPlugin.Headless.Infrastructure;
using Xunit;

namespace LayerUnpackPlugin.Headless.Tests;

/// <summary>R06 格式证据使用真实归档和独立 .NET ZIP 读取器，逐路径、类型及内容摘要核对。</summary>
public sealed class ConversionTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("7Zip.LZMA.7z", null)]
    [InlineData("7Zip.solid.7z", null)]
    [InlineData("7Zip.LZMA.Aes.7z", "testpassword")]
    [InlineData("7Zip.LZMA2.Aes.7z", "testpassword")]
    [InlineData("Rar4.rar", null)]
    [InlineData("Rar5.rar", null)]
    [InlineData("Rar.solid.rar", null)]
    [InlineData("Rar5.solid.rar", null)]
    [InlineData("Rar.encrypted_filesAndHeader.rar", "test")]
    [InlineData("Rar.encrypted_filesOnly.rar", "test")]
    [InlineData("Rar5.encrypted_filesAndHeader.rar", "test")]
    [InlineData("Rar5.encrypted_filesOnly.rar", "test")]
    public async Task 真实RAR和7z转ZIP逐路径及摘要匹配公开原文(string fixture, string? password)
    {
        using var w = new TestWorkspace(); var source = w.CopyFixture(fixture);
        var result = await new RepackService().ConvertAsync(new([source], w.Output), password is null ? [] : ["wrong-first", password], cancellationToken: Token);
        Assert.Equal(fixture.StartsWith("Rar5.encrypted", StringComparison.Ordinal) ? RepackState.CompletedWithWarnings : RepackState.Completed, result.State);
        var group = Assert.Single(result.Groups); Assert.NotNull(group.OutputPath);
        var expected = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures/expected-content.json")))!;
        Assert.Equal(expected.OrderBy(p => p.Key), Digests(group.OutputPath!).OrderBy(p => p.Key));
        Assert.Equal(fixture.StartsWith("Rar5.encrypted", StringComparison.Ordinal), group.Warnings.Count > 0);
        Assert.Empty(result.CleanupWarnings); Assert.Empty(Directory.GetDirectories(w.Output)); Assert.True(File.Exists(source));
        Assert.Equal(new FileInfo(group.OutputPath!).Length, result.Usage.ArchiveBytes);
    }

    [Fact]
    public async Task 多来源RAR和7z每来源独立产物同名不覆盖()
    {
        using var w = new TestWorkspace(); var rar = w.CopyFixture("Rar4.rar", "same.rar"); var seven = w.CopyFixture("7Zip.LZMA.7z", "same.7z");
        var result = await new RepackService().ConvertAsync(new([rar, seven], w.Output), cancellationToken: Token);
        Assert.Equal(RepackState.Completed, result.State); Assert.Equal(2, result.CommittedCount);
        Assert.Equal(2, result.Groups.Select(g => g.OutputPath).Distinct().Count());
        Assert.Equal(Digests(result.Groups[0].OutputPath!).OrderBy(p => p.Key), Digests(result.Groups[1].OutputPath!).OrderBy(p => p.Key));
    }

    [Fact]
    public async Task 普通转换保留内部包字节空目录中文路径且不进行子包发现()
    {
        using var w = new TestWorkspace(); var inner = w.Zip("inner.zip", ("x.pdf", [1, 2, 3])); var bytes = File.ReadAllBytes(inner);
        var source = w.Zip("source.zip", ("目录/内部.zip", bytes), ("bad.rar", [1]), ("空目录/", []), ("中文.txt", "中文内容"u8.ToArray()), ("空.txt", []));
        var result = await new RepackService().ConvertAsync(new([source], w.Output,
            limits: new() { Unpack = new() { MaxArchives = 1 } }), cancellationToken: Token);
        Assert.Equal(RepackState.Completed, result.State);
        using var zip = ZipFile.OpenRead(Assert.Single(result.Groups).OutputPath!);
        Assert.Contains(zip.Entries, e => e.FullName == "空目录/");
        using var content = zip.GetEntry("目录/内部.zip")!.Open(); using var copy = new MemoryStream(); await content.CopyToAsync(copy, Token);
        Assert.Equal(bytes, copy.ToArray()); Assert.Equal(Digests(source).OrderBy(p => p.Key), Digests(result.Groups[0].OutputPath!).OrderBy(p => p.Key));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task 显式递归多分支与PDF整理映射沿用既有规则(int depth)
    {
        using var w = new TestWorkspace(); var leaf = w.Zip("leaf.zip", ("leaf.pdf", [8]));
        var left = w.Zip("left.zip", ("wrap/left.pdf", [1]), ("leaf.zip", File.ReadAllBytes(leaf)), ("other.txt", [4]));
        var right = w.Zip("right.zip", ("right.pdf", [2]));
        var source = w.Zip("source.zip", ("left.zip", File.ReadAllBytes(left)), ("right.zip", File.ReadAllBytes(right)), ("top.pdf", [3]));
        var rules = new OrganizationRules(OrganizationFileTypes.Pdf, true);
        var result = await new RepackService().ConvertAsync(new([source], w.Output, ConversionMode.ExpandAndOrganize, depth, rules), cancellationToken: Token);
        Assert.Equal(RepackState.Completed, result.State);
        await using var unpack = new UnpackService().CreateSession(new([source], w.FilePath("reference"), depth));
        var extracted = await unpack.ExecuteAsync(cancellationToken: Token);
        var expected = await new OrganizationPlanner().CreateAsync(extracted, w.FilePath("unused"), rules, cancellationToken: Token);
        var prefix = Assert.Single(expected.Sources).TargetName + "/";
        Assert.Equal(expected.Mappings.Where(m => !m.IsDirectory).ToDictionary(m => m.TargetRelativePath[prefix.Length..], m => m.Sha256).OrderBy(p => p.Key),
            Digests(result.Groups[0].OutputPath!).OrderBy(p => p.Key));
        Assert.False(Directory.Exists(w.FilePath("unused"))); Assert.Empty(Directory.GetDirectories(w.Output));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 来源密码A与目标B分离且默认不继承A(bool encrypted)
    {
        using var w = new TestWorkspace(); var source = w.CopyFixture("7Zip.LZMA.Aes.7z");
        using var secret = encrypted ? new PackSecret("public-target-B") : null;
        var request = new ConversionRequest([source], w.Output, options: new() { Encrypt = encrypted });
        var result = await new RepackService().ConvertAsync(request, ["testpassword"], secret, cancellationToken: Token);
        Assert.Equal(RepackState.Completed, result.State);
        var target = Assert.Single(result.Groups).OutputPath!;
        await using var read = new UnpackService().CreateSession(new([target], w.FilePath("read"), passwords: encrypted ? ["public-target-B"] : []));
        Assert.Equal(BatchState.Completed, (await read.ExecuteAsync(cancellationToken: Token)).State);
        if (encrypted)
        {
            await using var wrong = new UnpackService().CreateSession(new([target], w.FilePath("wrong"), passwords: ["testpassword"]));
            Assert.Equal(BatchState.Failed, (await wrong.ExecuteAsync(cancellationToken: Token)).State);
        }
        var json = JsonSerializer.Serialize(new { request, result, secret });
        Assert.DoesNotContain("public-target-B", json); Assert.DoesNotContain("testpassword", json);
    }

    [Fact]
    public async Task 来源损坏不会撤销其他成功包且失败来源不生成残缺ZIP()
    {
        using var w = new TestWorkspace(); var good = w.CopyFixture("Rar4.rar"); var bad = w.Zip("bad.zip", ("a", [1]));
        File.WriteAllBytes(bad, [1, 2, 3]); var next = w.CopyFixture("7Zip.LZMA.7z");
        var result = await new RepackService().ConvertAsync(new([good, bad, next], w.Output), cancellationToken: Token);
        Assert.Equal(RepackState.PartiallyCompleted, result.State); Assert.Equal(2, result.CommittedCount);
        Assert.Equal(RepackState.Failed, result.Groups[1].State); Assert.Null(result.Groups[1].OutputPath);
        Assert.Equal(2, Directory.GetFiles(w.Output).Length); Assert.Empty(Directory.GetDirectories(w.Output));
    }

    [Fact]
    public async Task 子包损坏使该来源复合转换失败且不把父内容当完整转换()
    {
        using var w = new TestWorkspace(); var source = w.Zip("source.zip", ("bad.zip", [1]), ("ok.pdf", [2]));
        var result = await new RepackService().ConvertAsync(new([source], w.Output, ConversionMode.ExpandAndOrganize), cancellationToken: Token);
        Assert.Equal(RepackState.Failed, result.State); Assert.Equal(0, result.CommittedCount); Assert.Empty(Directory.GetFileSystemEntries(w.Output));
    }

    [Theory]
    [InlineData(TarEntryType.SymbolicLink)]
    [InlineData(TarEntryType.HardLink)]
    [InlineData(TarEntryType.Fifo)]
    public async Task 不支持的链接与特殊条目必须失败不能静默丢弃(TarEntryType kind)
    {
        using var w = new TestWorkspace(); var source = w.FilePath("special.tar");
        using (var tar = new TarWriter(File.Create(source)))
        {
            var entry = new PaxTarEntry(kind, "special");
            if (kind != TarEntryType.Fifo) entry.LinkName = "outside";
            tar.WriteEntry(entry);
        }
        var result = await new RepackService().ConvertAsync(new([source], w.Output), cancellationToken: Token);
        Assert.Equal(RepackState.Failed, result.State); Assert.Equal("UnsafePath", Assert.Single(result.Groups).Error?.Code);
        Assert.Empty(Directory.GetFileSystemEntries(w.Output));
    }

    [Fact]
    public async Task 空归档和纯空目录转换仍生成可回读的独立ZIP()
    {
        using var w = new TestWorkspace(); var empty = w.Zip("empty.zip"); var dirs = w.Zip("dirs.zip", ("one/two/", []));
        var result = await new RepackService().ConvertAsync(new([empty, dirs], w.Output), cancellationToken: Token);
        Assert.Equal(2, result.CommittedCount); Assert.Equal(RepackState.Completed, result.State);
        using var zip = ZipFile.OpenRead(result.Groups[0].OutputPath!); Assert.Empty(zip.Entries);
        using var directories = ZipFile.OpenRead(result.Groups[1].OutputPath!); Assert.Contains(directories.Entries, e => e.FullName == "one/two/");
    }

    [Fact]
    public async Task 筛选没有匹配时明确跳过且不创建空壳包()
    {
        using var w = new TestWorkspace(); var source = w.Zip("source.zip", ("a.txt", [1]));
        var result = await new RepackService().ConvertAsync(new([source], w.Output, ConversionMode.ExpandAndOrganize, rules: new(OrganizationFileTypes.Pdf)), cancellationToken: Token);
        Assert.Equal(RepackState.Skipped, result.State); Assert.Empty(Directory.GetFileSystemEntries(w.Output));
    }

    [Theory]
    [InlineData(0x1000)]
    [InlineData(0x2000)]
    [InlineData(0x6000)]
    [InlineData(0xC000)]
    [InlineData(0xA000)]
    public async Task ZIP声明的特殊对象不能被转换成普通空文件(int unixType)
    {
        using var w = new TestWorkspace(); var path = w.FilePath("special.zip");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create)) zip.CreateEntry("special").ExternalAttributes = unixType << 16;
        var result = await new RepackService().ConvertAsync(new([path], w.Output), cancellationToken: Token);
        Assert.Equal(RepackState.Failed, result.State); Assert.Equal("UnsafePath", result.Groups[0].Error?.Code);
        Assert.Empty(Directory.GetFileSystemEntries(w.Output));
    }

    internal static Dictionary<string, string> Digests(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        return zip.Entries.Where(e => !e.FullName.EndsWith('/')).ToDictionary(e => e.FullName, e =>
        { using var stream = e.Open(); return Convert.ToHexString(SHA256.HashData(stream)); });
    }
}

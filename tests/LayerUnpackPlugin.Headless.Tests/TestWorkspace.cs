using System.IO.Compression;

namespace LayerUnpackPlugin.Headless.Tests;

/// <summary>每个测试独占临时根，仅清理自己创建的目录，不接触用户输入或输出。</summary>
internal sealed class TestWorkspace : IDisposable
{
    internal string Root { get; } = Directory.CreateTempSubdirectory("LayerUnpack-tests-").FullName;
    internal string Output => Path.Combine(Root, "output");
    internal string FilePath(string name) => Path.Combine(Root, name);
    internal string Zip(string name, params (string Name, byte[] Bytes)[] entries)
    {
        var path = FilePath(name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var entry in entries)
        {
            using var output = zip.CreateEntry(entry.Name, CompressionLevel.NoCompression).Open();
            output.Write(entry.Bytes);
        }
        return path;
    }
    internal string CopyFixture(string name, string? targetName = null)
    {
        var target = FilePath(targetName ?? name);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", name), target);
        return target;
    }
    public void Dispose()
    {
        var root = Path.GetFullPath(Root);
        if (!root.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(root).StartsWith("LayerUnpack-tests-", StringComparison.Ordinal)) throw new InvalidOperationException();
        Directory.Delete(root, true);
    }
}

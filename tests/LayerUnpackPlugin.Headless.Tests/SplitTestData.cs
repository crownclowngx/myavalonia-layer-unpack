using System.Security.Cryptography;
using System.Text.Json;
using Xunit;

namespace LayerUnpackPlugin.Headless.Tests;

internal static class SplitTestData
{
    internal static string[] CopyParts(TestWorkspace w, string variant = "plain", string directory = "parts")
    {
        var root = Directory.CreateDirectory(w.FilePath(directory)).FullName;
        return Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Fixtures", "G0015"), variant + ".7z.*")
            .Order(StringComparer.Ordinal).Select(p => { var target = Path.Combine(root, Path.GetFileName(p)); File.Copy(p, target); return target; }).ToArray();
    }

    internal static void AssertContents(string output)
    {
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "G0015", "manifest.json")));
        foreach (var entry in manifest.RootElement.GetProperty("expected").EnumerateArray())
        {
            var path = Path.Combine(output, entry.GetProperty("path").GetString()!);
            if (entry.GetProperty("directory").GetBoolean()) Assert.True(Directory.Exists(path), path);
            else Assert.Equal(entry.GetProperty("sha256").GetString(), Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
        }
    }

}

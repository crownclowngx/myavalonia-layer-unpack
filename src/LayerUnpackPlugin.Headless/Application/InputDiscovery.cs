using LayerUnpackPlugin.Headless.Contracts;
using LayerUnpackPlugin.Headless.Infrastructure;

namespace LayerUnpackPlugin.Headless.Application;

public sealed record DiscoveryResult(IReadOnlyList<string> Files, IReadOnlyList<string> Warnings);

/// <summary>输入发现与归档嵌套分开。只扫描普通目录，不跟随链接，且排除明确选择的输出目录。</summary>
public static class InputDiscovery
{
    public static async Task<DiscoveryResult> DiscoverAsync(IEnumerable<string> inputs, string? excludedDirectory,
        int maximumFiles = 1000, CancellationToken cancellationToken = default)
    {
        if (maximumFiles is <= 0 or > 10_000) throw new ArgumentOutOfRangeException(nameof(maximumFiles));
        var found = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var warnings = new List<string>();
        var pending = new Stack<string>(inputs.Reverse());
        var visited = new HashSet<string>(found.Comparer);
        var inspected = 0;
        while (pending.TryPop(out var path))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++inspected > 100_000) { warnings.Add("扫描达到 100000 项上限，请缩小输入目录范围。"); break; }
            try
            {
                var full = Path.GetFullPath(path);
                if (!visited.Add(full)) continue;
                if (!string.IsNullOrWhiteSpace(excludedDirectory) &&
                    (full.Equals(Path.GetFullPath(excludedDirectory), PathPolicy.Comparison) || PathPolicy.IsWithin(excludedDirectory, full))) continue;
                PathPolicy.EnsureNoLinks(full);
                if (Directory.Exists(full))
                {
                    foreach (var child in Directory.EnumerateFileSystemEntries(full))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (pending.Count + inspected >= 100_000) { warnings.Add("待扫描项超过上限，请缩小输入目录范围。"); break; }
                        pending.Push(child);
                    }
                }
                else if (File.Exists(full) && (ArchiveProbe.HasKnownExtension(full) ||
                    await ArchiveProbe.DetectAsync(full, cancellationToken).ConfigureAwait(false) != ArchiveKind.Unknown))
                {
                    if (found.Count >= maximumFiles) { warnings.Add($"最多添加 {maximumFiles} 个压缩包，其余输入未加入。"); break; }
                    found.Add(full);
                }
                else if (warnings.Count < 100) warnings.Add($"未识别为压缩包：{full}");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or UnpackFailureException or ArgumentException)
            { if (warnings.Count < 100) warnings.Add($"无法扫描：{path}"); }
        }
        return new DiscoveryResult(Array.AsReadOnly(found.Order(StringComparer.OrdinalIgnoreCase).ToArray()), Array.AsReadOnly(warnings.Take(100).ToArray()));
    }
}

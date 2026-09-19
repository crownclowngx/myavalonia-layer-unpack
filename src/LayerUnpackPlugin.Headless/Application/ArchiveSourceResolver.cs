using System.Globalization;
using System.Text.RegularExpressions;
using LayerUnpackPlugin.Headless.Contracts;
using LayerUnpackPlugin.Headless.Infrastructure;

namespace LayerUnpackPlugin.Headless.Application;

/// <summary>
/// 只负责逻辑来源归组。顶层允许在选中卷的直接父目录补全成员，内层只接受已提交清单；
/// 不解码、不写输出，也不让引擎依据文件名自行访问其他目录。
/// </summary>
public static partial class ArchiveSourceResolver
{
    public const int MaximumDiscoveredPaths = 100_000;
    internal static StringComparer Comparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    [GeneratedRegex(@"^(.+\.7z)\.([0-9]{3,})$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SplitName();

    public static bool IsSplitPath(string path) => SplitName().IsMatch(Path.GetFileName(path));
    private static string? Key(string path)
    {
        var match = SplitName().Match(Path.GetFileName(path));
        return match.Success ? Path.Combine(Path.GetDirectoryName(path)!, match.Groups[1].Value) : null;
    }

    /// <summary>直接输入的路径可包含任意卷；重复成员只形成一个组，结果按首个输入位置排序。</summary>
    public static IReadOnlyList<ArchiveSource> ResolveInputs(IEnumerable<string> paths, UnpackLimits limits, CancellationToken token = default)
        => Resolve(paths, limits, true, token);

    /// <summary>由本次父包清单发现孩子时禁止补扫目录；即使邻近出现同名卷也不能接管历史文件。</summary>
    internal static IReadOnlyList<ArchiveSource> ResolveCommitted(IEnumerable<string> paths, UnpackLimits limits, CancellationToken token)
        => Resolve(paths, limits, false, token);

    private static IReadOnlyList<ArchiveSource> Resolve(IEnumerable<string> paths, UnpackLimits limits, bool includeSiblings, CancellationToken token)
    {
        limits.Validate();
        var input = new List<string>();
        var unique = new HashSet<string>(Comparer);
        var inspected = 0;
        foreach (var path in paths)
        {
            token.ThrowIfCancellationRequested(); Count(ref inspected);
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
                throw new UnpackValidationException("Inputs", "请提供完整的本地绝对路径。");
            var full = Path.GetFullPath(path);
            if (unique.Add(full)) input.Add(full);
        }
        var committedGroups = input.Where(IsSplitPath).GroupBy(p => Key(p)!, Comparer).ToDictionary(g => g.Key, g => g.ToArray(), Comparer);
        var directories = new Dictionary<string, string[]>(Comparer);
        var seen = new HashSet<string>(Comparer);
        var result = new List<ArchiveSource>();
        foreach (var path in input)
        {
            token.ThrowIfCancellationRequested();
            var key = Key(path);
            if (!seen.Add(key is null ? "file:" + path : "split:" + key)) continue;
            if (key is null) { result.Add(new(path, [path])); continue; }
            try
            {
                var candidates = committedGroups[key];
                if (includeSiblings)
                {
                    var directory = Path.GetDirectoryName(path)!;
                    if (!directories.TryGetValue(directory, out var files))
                    {
                        PathPolicy.EnsureNoLinks(directory);
                        var found = new List<string>();
                        foreach (var file in Directory.EnumerateFiles(directory))
                        { token.ThrowIfCancellationRequested(); Count(ref inspected); if (IsSplitPath(file)) found.Add(file); }
                        files = found.ToArray(); directories.Add(directory, files);
                    }
                    candidates = files.Where(p => Comparer.Equals(Key(p), key)).ToArray();
                }
                result.Add(Group(path, key, candidates, limits));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            { result.Add(new(path, [path], key, new(UnpackError.InputUnavailable, "无法读取分卷所在目录，请检查来源和权限。"))); }
        }
        return result.AsReadOnly();
    }

    private static ArchiveSource Group(string selected, string key, string[] candidates, UnpackLimits limits)
    {
        var numbered = candidates.Select(p => (Path: p, Digits: SplitName().Match(Path.GetFileName(p)).Groups[2].Value))
            .Select(p => (p.Path, p.Digits, Number: int.TryParse(p.Digits, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : -1))
            .OrderBy(p => p.Number).ThenBy(p => p.Path, StringComparer.Ordinal).ToArray();
        var members = numbered.Select(p => p.Path).ToArray();
        var primary = numbered.FirstOrDefault(p => p.Number == 1).Path ?? selected;
        UnpackDiagnostic? error = null;
        if (numbered.Length > limits.MaxVolumesPerArchive)
            error = new(UnpackError.BudgetExceeded, "分卷数量超过单组上限。");
        else if (numbered.Any(p => p.Number <= 0) || numbered.Select(p => p.Number).Distinct().Count() != numbered.Length || numbered.Select(p => p.Digits.Length).Distinct().Count() > 1)
            error = new(UnpackError.InvalidVolumeSet, "分卷编号重复、无效或补零方式不一致，请整理来源后重新识别。");
        else
        {
            var next = 1;
            foreach (var part in numbered)
            {
                if (part.Number != next) break;
                next++;
            }
            if (numbered.Length == 0 || next <= numbered[^1].Number)
                error = new(UnpackError.MissingVolume, $"缺少分卷：{Path.GetFileName(key)}.{next.ToString("D" + (numbered.FirstOrDefault().Digits?.Length ?? 3), CultureInfo.InvariantCulture)}。请补齐到同一文件夹后重新识别。");
        }
        return new(primary, members, key, error);
    }

    /// <summary>只比较成员身份，不接纳新文件。已冻结输入与重新扫描结果不同就要求新任务。</summary>
    internal static void VerifyMembers(ArchiveSource source, UnpackLimits limits, CancellationToken token)
    {
        if (!source.IsSplit) return;
        var now = ResolveInputs([source.PrimaryPath], limits, token).Single();
        if (!source.Members.SequenceEqual(now.Members, Comparer))
            throw new UnpackFailureException(UnpackError.InputChanged, "分卷成员已变化，请重新识别此组并开始新任务。");
    }

    private static void Count(ref int inspected)
    {
        if (++inspected > MaximumDiscoveredPaths)
            throw new UnpackFailureException(UnpackError.BudgetExceeded, "来源发现超过 100000 项上限，请缩小输入范围。", true);
    }
}

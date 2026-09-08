using LayerUnpackPlugin.Headless.Contracts;
using LayerUnpackPlugin.Headless.Infrastructure;

namespace LayerUnpackPlugin.Headless.Application;

/// <summary>合包与分别打包共用顶层选择规则：规范路径排序、去重、父目录吸收后代、根名稳定编号。</summary>
internal static class PackInputSelection
{
    internal static (string[] Sources, PackRoot[] Roots) Resolve(IReadOnlyList<string> inputs, PackLimits limits)
    {
        if (inputs.Count == 0 || inputs.Count > limits.MaxInputs)
            throw new PackValidationException("请添加输入，且输入项数量不能超过限制。");
        var paths = inputs.Select(p =>
        {
            if (string.IsNullOrWhiteSpace(p) || !Path.IsPathFullyQualified(p))
                throw new PackValidationException("输入必须是完整的文件或文件夹路径。");
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(p));
        }).Distinct(PackPaths.Comparer).OrderBy(p => p, PackPaths.Comparer).ToArray();
        // 此处只确定包含关系；可读性和链接检查留给各组规划器，坏来源不会阻止独立兄弟组准备。
        var sources = paths.Select(p => new PackRoot(p, Path.GetFileName(p), Directory.Exists(p))).ToArray();
        if (sources.Any(s => string.IsNullOrEmpty(s.EntryName)))
            throw new PackValidationException("请选具体文件夹，不支持整个磁盘根目录。");
        var roots = sources.Where(s => !sources.Any(parent => parent.IsDirectory && PathPolicy.IsWithin(parent.SourcePath, s.SourcePath))).ToArray();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < roots.Length; i++)
        {
            var root = roots[i];
            var candidate = root.EntryName;
            for (var suffix = 1; !used.Add(candidate); suffix++)
                candidate = root.IsDirectory ? $"{root.EntryName} ({suffix})" : $"{Path.GetFileNameWithoutExtension(root.EntryName)} ({suffix}){Path.GetExtension(root.EntryName)}";
            roots[i] = root with { EntryName = candidate };
        }
        return (paths, roots);
    }
}

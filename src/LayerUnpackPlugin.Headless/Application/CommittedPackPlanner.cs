using LayerUnpackPlugin.Headless.Contracts;
using LayerUnpackPlugin.Headless.Infrastructure;

namespace LayerUnpackPlugin.Headless.Application;

/// <summary>把 R05 已验证的来源映射适配成 R02 写入清单。规则和来源归类仍交给整理规划器，
/// 本类只负责包边界与包内相对路径，不扫描目录补充文件，也不创建另一份整理内容。</summary>
public sealed class CommittedPackPlanner(OrganizationPlanner organizationPlanner)
{
    public CommittedPackPlanner() : this(new OrganizationPlanner()) { }

    public async Task<RepackPlan> PrepareAsync(UnpackResult input, string outputDirectory, PackGrouping grouping = PackGrouping.Combined,
        PackOptions? options = null, RepackLimits? limits = null, OrganizationRules? rules = null, CancellationToken cancellationToken = default)
    {
        limits ??= new(); options ??= new(); rules ??= new(); limits.Validate(); ValidateOptions(options);
        if (!Enum.IsDefined(grouping)) throw new PackValidationException("打包分组无效。");
        var organized = await organizationPlanner.CreateAsync(input, outputDirectory, rules,
            new()
            {
                MaxEntries = limits.Pack.MaxEntries,
                MaxTotalBytes = limits.Pack.MaxTotalBytes,
                MaxFileBytes = limits.Pack.MaxFileBytes,
                Timeout = limits.Timeout
            }, cancellationToken).ConfigureAwait(false);
        var groups = new List<RepackGroupPlan>();
        if (grouping == PackGrouping.Combined && organized.Sources.Count > 0)
            Add("已提交结果", "打包结果", organized.Sources.Select(s => s.Id).ToHashSet(), null);
        else
            foreach (var source in organized.Sources) Add(source.ArchivePath, source.TargetName, [source.Id], source.TargetName);
        return new(groups, limits);

        void Add(string label, string name, HashSet<Guid> ids, string? prefix)
        {
            var mappings = organized.Mappings.Where(m => ids.Contains(m.SourceId));
            var inputs = organized.Inputs.Where(i => ids.Contains(i.SourceId)).ToArray();
            var credentials = inputs.ToDictionary(i => i.Path, OrganizationFiles.Comparer);
            var entries = new List<PackEntry>();
            foreach (var mapping in mappings)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // 分别打包时来源已经由 ZIP 文件名表达，不再给每个 ZIP 多套一层来源目录。
                if (prefix is not null && mapping.TargetRelativePath == prefix) continue;
                var relative = prefix is null ? mapping.TargetRelativePath : mapping.TargetRelativePath[(prefix.Length + 1)..];
                PackPaths.CheckEntry(relative, mapping.IsDirectory);
                if (relative.Count(c => c == '/') > limits.Pack.MaxDirectoryDepth)
                    throw new PackFailureException(PackError.BudgetExceeded, "包内目录深度超过创建限制。");
                entries.Add(new(mapping.SourcePath, relative, mapping.IsDirectory, mapping.Length,
                    credentials[mapping.SourcePath].Entry.LastWriteUtc, mapping.Sha256));
            }
            var target = Path.Combine(outputDirectory, name + ".zip");
            PackPaths.CheckEntry(Path.GetFileName(target), false); PackPaths.Check(target);
            var request = new PackRequest(inputs.Where(i => i.Entry.RelativePath == "").Select(i => i.Path), target, limits.Pack, options);
            var pack = new PackPlan(request, [], entries, 0, 0, [])
            { ManifestInputs = Array.AsReadOnly(inputs), AllowEmptyArchive = rules.FileTypes == OrganizationFileTypes.All };
            // 无论是否只选择部分类型，父／子节点的错误或认证限制都不能在新产物上丢失。
            var warnings = input.Nodes.Where(n => BelongsTo(n, ids, input)).SelectMany(n =>
                new[] { n.Warning, n.Error?.Message, n.State is NodeState.Failed or NodeState.NotRun or NodeState.Cancelled ? "来源仅有部分已提交内容。" : null })
                .OfType<string>().Distinct().ToArray();
            groups.Add(new(label, pack, Array.AsReadOnly(warnings)));
        }
    }

    /// <summary>整理结果重新包装为真实提交节点；分别模式必须有 R06 开始记录的来源边界。
    /// 老结果只能合成一个包，不能根据目录名字猜测来源。</summary>
    public async Task<RepackPlan> PrepareAsync(OrganizationResult input, string outputDirectory, PackGrouping grouping = PackGrouping.Combined,
        PackOptions? options = null, RepackLimits? limits = null, CancellationToken cancellationToken = default)
    {
        if (input.State != OrganizationState.Completed || input.OutputDirectory is null)
            throw new PackValidationException("只有已提交的整理结果可以打包。");
        if (!Enum.IsDefined(grouping)) throw new PackValidationException("打包分组无效。");
        var node = new ArchiveNodeResult(Guid.NewGuid(), null, "整理结果", 1, NodeState.Extracted, null, input.OutputDirectory, null, [], 0,
            input.SourceWarnings.Count == 0 ? null : string.Join("\n", input.SourceWarnings))
        { CommittedEntries = input.Entries };
        // 先验证整理目录的完整清单。分别模式也不能遗漏来源边界以外的提交条目，或暗中接纳历史文件。
        var combined = await PrepareAsync(new UnpackResult(Guid.NewGuid(), BatchState.Completed, [node], 0, 0), outputDirectory,
            PackGrouping.Separate, options, limits, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (grouping == PackGrouping.Combined) return combined;
        if (input.SourceGroups.Count == 0) throw new PackValidationException("当前整理结果缺少来源分组，请使用合成模式或重新整理。");
        var all = combined.Groups.Single();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var groups = new List<RepackGroupPlan>();
        foreach (var source in input.SourceGroups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PackPaths.CheckEntry(source.RelativeRoot, true);
            if (source.RelativeRoot.Contains('/') || source.RelativeRoot.Contains('\\') || !seen.Add(source.RelativeRoot) ||
                !input.Entries.Any(e => e.IsDirectory && e.RelativePath == source.RelativeRoot))
                throw new PackValidationException("整理来源边界重复或缺失，请重新整理后再分别打包。");
            var root = OrganizationFiles.EntryPath(input.OutputDirectory, source.RelativeRoot, true);
            var entries = all.Plan.Entries.Where(e => e.EntryName.StartsWith(source.RelativeRoot + "/", StringComparison.Ordinal))
                .Select(e => e with { EntryName = e.EntryName[(source.RelativeRoot.Length + 1)..] });
            var request = new PackRequest([root], Path.Combine(outputDirectory, source.RelativeRoot + ".zip"), all.Plan.Request.Limits, all.Plan.Request.Options);
            var plan = new PackPlan(request, [], entries, 0, 0, [])
            {
                ManifestInputs = Array.AsReadOnly(all.Plan.ManifestInputs!.Where(i => OrganizationFiles.Comparer.Equals(i.Path, root) || PathPolicy.IsWithin(root, i.Path)).ToArray()),
                AllowEmptyArchive = true
            };
            groups.Add(new(source.Source, plan, all.Warnings));
        }
        if (input.Entries.Any(e => !seen.Contains(e.RelativePath.Split('/')[0])))
            throw new PackValidationException("部分提交条目没有来源分组，已拒绝遗漏内容的分别打包。");
        return new(groups, combined.Limits);
    }

    internal static void ValidateOptions(PackOptions options)
    {
        options.Validate();
        if (options.Exclusions.Extensions.Count > 0 || options.Exclusions.DirectoryNames.Count > 0)
            throw new PackValidationException("结果打包使用明确的提交清单；如需筛选请在整理规则中选择，不接受隐式排除。");
    }

    private static bool BelongsTo(ArchiveNodeResult node, HashSet<Guid> ids, UnpackResult input)
    {
        var seen = new HashSet<Guid>();
        while (seen.Add(node.Id))
        {
            if (ids.Contains(node.Id)) return true;
            if (node.ParentId is not Guid parent || input.Nodes.FirstOrDefault(n => n.Id == parent) is not { } ancestor) return false;
            node = ancestor;
        }
        return false;
    }
}

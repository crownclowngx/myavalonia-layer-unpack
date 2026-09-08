using LayerUnpackPlugin.Headless.Contracts;
using LayerUnpackPlugin.Headless.Infrastructure;

namespace LayerUnpackPlugin.Headless.Application;

/// <summary>将明确的已提交清单转换为目标映射；规则、来源归属及命名都在预览阶段确定，执行器不再作产品决策。</summary>
public sealed class OrganizationPlanner
{
    public async Task<OrganizationPlan> CreateAsync(UnpackResult result, string outputParent, OrganizationRules? rules = null,
        OrganizationLimits? limits = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        rules ??= new(); limits ??= new(); limits.Validate();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(limits.Timeout);
        try
        {
            if (!Enum.IsDefined(rules.FileTypes)) throw new ArgumentException("文件类型规则无效。");
            if (result.State is BatchState.Ready or BatchState.Running)
                throw new OrganizationFailureException(OrganizationError.MissingManifest, "请等待当前解压操作退出后再整理结果。");
            var parent = OrganizationFiles.Absolute(outputParent);
            if (File.Exists(parent)) throw new OrganizationFailureException(OrganizationError.OutputError, "输出位置必须是目录。");
            var inputs = Collect(result, limits, timeout.Token);
            var roots = inputs.Where(i => i.Entry.RelativePath == "").ToArray();
            foreach (var root in roots)
                if (OrganizationFiles.Comparer.Equals(root.Path, parent) || PathPolicy.IsWithin(root.Path, parent))
                    throw new OrganizationFailureException(OrganizationError.UnsafePath, "新整理目录不能位于原解压结果内部，请选择其他位置。");
            await OrganizationFiles.ValidateAsync(inputs, timeout.Token).ConfigureAwait(false);
            var sources = new List<OrganizationSource>();
            var mappings = new List<OrganizationMapping>();
            var conflicts = new List<OrganizationConflict>();
            var sourceNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var root in roots.OrderBy(r => r.Path, StringComparer.Ordinal))
            {
                timeout.Token.ThrowIfCancellationRequested();
                var items = inputs.Where(i => i.SourceId == root.SourceId && i.Path != root.Path).ToArray();
                var files = items.Where(i => !i.Entry.IsDirectory && Matches(i.Path, rules.FileTypes)).ToArray();
                if (files.Length == 0 && rules.FileTypes != OrganizationFileTypes.All) continue;
                var prefix = rules.FlattenWrappingDirectories ? FindWrappingPrefix(root.Path, items) : root.Path;
                var node = result.Nodes.Single(n => n.Id == root.SourceId);
                var suggestedName = ArchiveProbe.OutputName(node.SourcePath);
                var sourceName = Allocate(suggestedName, true, sourceNames);
                if (sourceName != suggestedName) conflicts.Add(new(suggestedName, sourceName, "不同来源名称相同或仅大小写不同，保留独立目录。"));
                var removed = prefix == root.Path ? "" : Path.GetRelativePath(root.Path, prefix).Replace('\\', '/');
                sources.Add(new(root.SourceId, node.SourcePath, root.Path, sourceName, removed));
                mappings.Add(new(root.SourceId, prefix, sourceName, true, 0, ""));

                var selected = files.Select(i => i.Path).ToHashSet(OrganizationFiles.Comparer);
                if (rules.FileTypes == OrganizationFileTypes.All)
                    foreach (var item in items.Where(i => i.Entry.IsDirectory && PathPolicy.IsWithin(prefix, i.Path))) selected.Add(item.Path);
                foreach (var file in files)
                    for (var directory = Path.GetDirectoryName(file.Path)!; PathPolicy.IsWithin(prefix, directory); directory = Path.GetDirectoryName(directory)!) selected.Add(directory);
                var renamed = new Dictionary<string, string>(OrganizationFiles.Comparer) { [prefix] = sourceName };
                var usedByParent = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
                foreach (var item in items.Where(i => selected.Contains(i.Path)).OrderBy(i => i.Path.Count(c => c == Path.DirectorySeparatorChar)).ThenBy(i => i.Path, StringComparer.Ordinal))
                {
                    timeout.Token.ThrowIfCancellationRequested();
                    var targetParent = renamed[Path.GetDirectoryName(item.Path)!];
                    if (!usedByParent.TryGetValue(targetParent, out var used)) usedByParent[targetParent] = used = new(StringComparer.OrdinalIgnoreCase);
                    var name = Path.GetFileName(item.Path);
                    var allocated = Allocate(name, item.Entry.IsDirectory, used);
                    var target = targetParent + "/" + allocated;
                    if (name != allocated) conflicts.Add(new(item.Path, target, "同名或大小写冲突，自动编号且不覆盖。"));
                    renamed[item.Path] = target;
                    mappings.Add(new(root.SourceId, item.Path, target, item.Entry.IsDirectory, item.Entry.Length, item.Entry.Sha256));
                }
            }
            var output = OrganizationFiles.EntryPath(parent, "整理结果", true);
            for (var suffix = 1; OrganizationFiles.Occupied(output); suffix++)
            {
                timeout.Token.ThrowIfCancellationRequested();
                if (suffix > 10_000) throw new OrganizationFailureException(OrganizationError.OutputConflict, "无法分配整理目录，请选择其他位置。");
                output = OrganizationFiles.EntryPath(parent, $"整理结果 ({suffix})", true);
            }
            if (Path.GetFileName(output) != "整理结果") conflicts.Add(new("整理结果", Path.GetFileName(output), "已有目标保留，为本次整理分配新目录。"));
            foreach (var mapping in mappings)
            {
                timeout.Token.ThrowIfCancellationRequested();
                OrganizationFiles.EntryPath(output, mapping.TargetRelativePath, mapping.IsDirectory);
            }
            return new(result.BatchId, output, rules, limits, sources, mappings, conflicts, inputs)
            { SourceWarnings = Array.AsReadOnly(result.Nodes.SelectMany(n => new[] { n.Warning, n.Error?.Message }).OfType<string>().Distinct().ToArray()) };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new OrganizationFailureException(OrganizationError.Timeout, "生成预览超过时间预算，请减少本次结果数量。"); }
        catch (Exception e) when (e is not (OperationCanceledException or OrganizationFailureException))
        { var diagnostic = OrganizationFiles.Diagnostic(e); throw new OrganizationFailureException(diagnostic.Code, diagnostic.Message); }
    }

    private static IReadOnlyList<OrganizationInput> Collect(UnpackResult result, OrganizationLimits limits, CancellationToken token)
    {
        var nodes = result.Nodes.ToDictionary(n => n.Id);
        var collected = new Dictionary<string, OrganizationInput>(OrganizationFiles.Comparer);
        long bytes = 0;
        foreach (var node in result.Nodes.Where(n => n.State == NodeState.Extracted))
        {
            token.ThrowIfCancellationRequested();
            if (node.OutputDirectory is null || node.CommittedEntries is null)
                throw new OrganizationFailureException(OrganizationError.MissingManifest, "当前结果缺少提交清单，请用新版重新解压；不会扫描历史输出补全。");
            var source = node;
            var seen = new HashSet<Guid> { node.Id };
            while (source.ParentId is Guid id)
            {
                if (!seen.Add(id) || !nodes.TryGetValue(id, out var ancestor) || ancestor.State != NodeState.Extracted || ancestor.OutputDirectory is null ||
                    !PathPolicy.IsWithin(ancestor.OutputDirectory, source.OutputDirectory!) ||
                    ancestor.CommittedEntries?.Any(e => !e.IsDirectory && OrganizationFiles.Comparer.Equals(OrganizationFiles.EntryPath(ancestor.OutputDirectory, e.RelativePath, false), source.SourcePath)) != true)
                    throw new OrganizationFailureException(OrganizationError.MissingManifest, "来源关系不完整，不能确定嵌套结果归属。");
                source = ancestor;
            }
            var output = OrganizationFiles.Absolute(node.OutputDirectory);
            Add(new(source.Id, output, new(node.Id == source.Id ? "" : Path.GetRelativePath(source.OutputDirectory!, output), true, 0, default, default, "")));
            foreach (var entry in node.CommittedEntries)
            {
                token.ThrowIfCancellationRequested();
                if (entry.Length < 0 || entry.Length > limits.MaxFileBytes || (entry.IsDirectory && entry.Length != 0) || (!entry.IsDirectory && entry.Sha256.Length != 64))
                    throw new OrganizationFailureException(OrganizationError.BudgetExceeded, "来源清单凭据无效或单文件超过整理预算。");
                Add(new(source.Id, OrganizationFiles.EntryPath(output, entry.RelativePath, entry.IsDirectory), entry));
            }
        }
        return Array.AsReadOnly(collected.Values.ToArray());

        void Add(OrganizationInput input)
        {
            if (collected.TryGetValue(input.Path, out var previous))
            {
                if (previous.SourceId != input.SourceId || previous.Entry != input.Entry)
                    throw new OrganizationFailureException(OrganizationError.MissingManifest, "同一路径出现不一致的来源或内容凭据。");
                return;
            }
            if (collected.Count >= limits.MaxEntries || (bytes += input.Entry.Length) > limits.MaxTotalBytes)
                throw new OrganizationFailureException(OrganizationError.BudgetExceeded, "当前结果超过整理条目或字节预算。");
            collected.Add(input.Path, input);
        }
    }

    private static bool Matches(string path, OrganizationFileTypes types)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        var pdf = extension == ".pdf";
        var image = extension is ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp" or ".tif" or ".tiff";
        return types switch { OrganizationFileTypes.All => true, OrganizationFileTypes.Pdf => pdf, OrganizationFileTypes.Images => image, _ => pdf || image };
    }

    private static string FindWrappingPrefix(string root, OrganizationInput[] items)
    {
        var children = items.GroupBy(i => Path.GetDirectoryName(i.Path)!, OrganizationFiles.Comparer).ToDictionary(g => g.Key, g => g.ToArray(), OrganizationFiles.Comparer);
        var current = root;
        // 只去掉来源根处连续的单普通子目录链；判断使用未筛选的完整清单，所以同层非匹配文件或空目录也会阻止压平。
        while (children.TryGetValue(current, out var next) && next.Length == 1 && next[0].Entry.IsDirectory) current = next[0].Path;
        return current;
    }

    private static string Allocate(string name, bool directory, HashSet<string> used)
    {
        var extension = directory ? "" : Path.GetExtension(name);
        var stem = directory ? name : Path.GetFileNameWithoutExtension(name);
        var candidate = name;
        for (var suffix = 1; !used.Add(candidate); suffix++) candidate = $"{stem} ({suffix}){extension}";
        return candidate;
    }
}

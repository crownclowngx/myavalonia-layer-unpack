using LayerUnpackPlugin.Headless.Contracts;

namespace LayerUnpackPlugin.Headless.Application;

/// <summary>会话只读目录。显示采用有限分页；搜索在完整路径上匹配，既不打开文件内容，也不影响选择。</summary>
public sealed class ArchiveCatalog
{
    private readonly Dictionary<ArchiveEntryId, BrowseEntry> _index;
    private readonly Dictionary<string, int> _rangeCounts = new(StringComparer.Ordinal);
    internal ArchiveCatalog(Guid id, string sourcePath, string sha256, IEnumerable<BrowseEntry> entries)
    {
        Id = id; SourcePath = sourcePath; Sha256 = sha256;
        Entries = Array.AsReadOnly(entries.ToArray());
        _index = Entries.ToDictionary(e => e.Id);
        foreach (var entry in Entries.Where(e => !e.IsSynthetic))
            foreach (var path in Ancestors(entry.Path)) _rangeCounts[path] = _rangeCounts.GetValueOrDefault(path) + 1;
    }
    public Guid Id { get; }
    public string SourcePath { get; }
    public string Sha256 { get; }
    public string Format => "ZIP";
    public IReadOnlyList<BrowseEntry> Entries { get; }
    internal int RangeCount(string path) => _rangeCounts.GetValueOrDefault(path);

    internal static IEnumerable<string> Ancestors(string path)
    {
        yield return path;
        for (var slash = path.LastIndexOf('/'); slash > 0; slash = path.LastIndexOf('/', slash - 1)) yield return path[..slash];
        if (path.Length > 0) yield return "";
    }

    public BrowseEntry GetEntry(ArchiveEntryId id) => _index.TryGetValue(id, out var entry) ? entry :
        throw new UnpackValidationException("Selection", "条目不属于本次目录快照，请重新加载并选择。");

    public BrowsePage GetPage(string? search = null, int offset = 0, int pageSize = 200, CancellationToken cancellationToken = default)
    {
        if (offset < 0 || pageSize is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(pageSize));
        var page = new List<BrowseEntry>(pageSize);
        var matches = 0;
        foreach (var entry in Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.IsNullOrEmpty(search) && !entry.Path.Contains(search, StringComparison.OrdinalIgnoreCase)) continue;
            if (matches >= offset && page.Count < pageSize) page.Add(entry);
            matches++;
        }
        return new(page.AsReadOnly(), offset, matches);
    }

    internal IEnumerable<BrowseEntry> GetSelectionRange(BrowseEntry row)
    {
        // 目录边界必须包含斜线，且按包内原始大小写匹配；a 不包含 ab，A 不会悄悄包含 a。
        if (!row.IsDirectory) { yield return row; yield break; }
        var prefix = row.Path.Length == 0 ? "" : row.Path + "/";
        foreach (var entry in Entries)
            if (!entry.IsSynthetic && (entry.Path == row.Path || entry.Path.StartsWith(prefix, StringComparison.Ordinal))) yield return entry;
    }
}

/// <summary>朴素集合实现选择语义：选目录立即展开成明确后代；取消子项后父目录呈部分选中，重复选择不重复计数。</summary>
/// <remarks>归属调用方交互线程；异步执行只使用 Capture 产生的独立快照，不读取可变集合。</remarks>
public sealed class ArchiveSelection(ArchiveCatalog catalog)
{
    private readonly HashSet<ArchiveEntryId> _selected = [];
    private readonly Dictionary<string, int> _selectedCounts = new(StringComparer.Ordinal);
    public int Count => _selected.Count;
    public int FileCount => _selected.Count(id => !catalog.GetEntry(id).IsDirectory);
    public void Clear() { _selected.Clear(); _selectedCounts.Clear(); }
    public void SetSelected(ArchiveEntryId id, bool selected)
    {
        foreach (var entry in catalog.GetSelectionRange(catalog.GetEntry(id)))
        {
            var changed = selected ? _selected.Add(entry.Id) : _selected.Remove(entry.Id);
            if (!changed) continue;
            foreach (var path in ArchiveCatalog.Ancestors(entry.Path))
                _selectedCounts[path] = _selectedCounts.GetValueOrDefault(path) + (selected ? 1 : -1);
        }
    }
    public bool? GetState(ArchiveEntryId id)
    {
        var row = catalog.GetEntry(id);
        if (!row.IsDirectory) return _selected.Contains(id);
        // 预聚合祖先计数，分页重绘不为每一行重复扫描十万条目录；目录勾选时才展开实际后代。
        var selected = _selectedCounts.GetValueOrDefault(row.Path);
        return selected == 0 ? false : selected == catalog.RangeCount(row.Path) ? true : null;
    }
    public BrowseSelection Capture() => new(_selected.OrderBy(id => id.Ordinal));
}

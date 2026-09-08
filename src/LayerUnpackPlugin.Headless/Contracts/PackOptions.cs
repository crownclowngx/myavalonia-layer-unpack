using System.Text.Json.Serialization;

namespace LayerUnpackPlugin.Headless.Contracts;

public enum PackCompression { Standard, Fast, High, Store }
public enum PackGrouping { Combined, Separate }

/// <summary>可记录的写入偏好只描述能力，不包含密码；默认值保持普通 ZIP 的原有行为。</summary>
public sealed record PackOptions
{
    public PackCompression Compression { get; init; } = PackCompression.Standard;
    public bool Encrypt { get; init; }
    public PackExclusionRules Exclusions { get; init; } = new();

    internal void Validate()
    {
        if (!Enum.IsDefined(Compression) || Exclusions is null)
            throw new PackValidationException("压缩偏好或排除规则无效。");
    }
}

/// <summary>
/// 有限、可解释的规则：扩展名按最后一段匹配，目录按完整叶名称匹配，均不区分大小写。
/// 不接受通配符和路径表达式。命中目录时整棵子树排除，清单以该目录一项表示，避免读取已排除内容。
/// </summary>
public sealed class PackExclusionRules
{
    public PackExclusionRules(IEnumerable<string>? extensions = null, IEnumerable<string>? directoryNames = null)
    {
        Extensions = Normalize(extensions, true);
        DirectoryNames = Normalize(directoryNames, false);
    }
    public IReadOnlyList<string> Extensions { get; }
    public IReadOnlyList<string> DirectoryNames { get; }

    private static IReadOnlyList<string> Normalize(IEnumerable<string>? values, bool extension)
    {
        var items = (values ?? []).Take(33).ToArray();
        if (items.Length > 32 || items.Any(v => string.IsNullOrWhiteSpace(v) || v.Length > 80 ||
            v != v.Trim() || v.IndexOfAny(['/', '\\', '*', '?', ':', '"', '<', '>', '|']) >= 0 ||
            v.Any(char.IsControl) || v is "." or ".." || v.EndsWith('.') ||
            (extension && (!v.StartsWith('.') || v.Length < 2 || v[1..].Contains('.')))))
            throw new PackValidationException("排除规则仅接受最多 32 个扩展名（如 .tmp）或完整目录名，不支持路径及通配符。");
        return Array.AsReadOnly(items.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    internal string? Match(string path, bool directory)
    {
        var value = directory ? Path.GetFileName(path) : Path.GetExtension(path);
        return (directory ? DirectoryNames : Extensions).Contains(value, StringComparer.OrdinalIgnoreCase)
            ? (directory ? $"目录名：{value}（包含全部后代）" : $"文件类型：{value}") : null;
    }
}

public sealed record PackExcludedItem(string SourcePath, string EntryName, bool IsDirectory, string Reason);

/// <summary>
/// 仅当前创建会话持有的目标秘密。通过独立参数传给写入器，不进入请求、计划、结果或异常。
/// 释放只撤销本对象的字符串引用，不能承诺清零运行时及第三方库已复制的托管内存。
/// </summary>
public sealed class PackSecret : IDisposable
{
    private string? _password;
    public PackSecret(string password)
    {
        if (string.IsNullOrEmpty(password) || password.Length > 1024)
            throw new PackValidationException("请设置 1–1024 个字符的目标密码。");
        _password = password;
    }
    [JsonIgnore] internal string Password => _password ?? throw new PackValidationException("本次目标密码已释放，请重新设置。");
    public void Dispose() => _password = null;
    public override string ToString() => "目标密码（已遮蔽）";
}

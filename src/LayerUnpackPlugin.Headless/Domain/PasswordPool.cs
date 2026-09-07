using LayerUnpackPlugin.Headless.Contracts;

namespace LayerUnpackPlugin.Headless.Domain;

/// <summary>只负责当前批次的候选顺序，不认识文件系统、引擎或界面。</summary>
public sealed class PasswordPool : IDisposable
{
    private readonly List<string> _candidates = [];
    private readonly List<string> _successful = [];

    public static string[] ParseLines(string text) => Normalize(text.Replace("\r\n", "\n").Split('\n'));

    public static string[] Normalize(IEnumerable<string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var result = new List<string>();
        foreach (var value in values)
        {
            if (value is null || value.Length > 1024 || value.Contains('\r') || value.Contains('\n'))
                throw new UnpackValidationException("Passwords", "每个密码最多 1024 个字符，不能包含换行符。");
            if (value.Length == 0 || result.Contains(value, StringComparer.Ordinal)) continue;
            result.Add(value);
            if (result.Count > UnpackLimits.PasswordCountCeiling)
                throw new UnpackValidationException("Passwords", "同一批次最多使用 64 个不同候选密码。");
        }
        return result.ToArray();
    }

    public void Add(IEnumerable<string> values)
    {
        // 先完整验证候选合并结果，再替换原集合；非法追加不会留下半更新密码池。
        var merged = Normalize(_candidates.Concat(values));
        _candidates.Clear();
        _candidates.AddRange(merged);
    }

    public IReadOnlyList<string?> Attempts() => new string?[] { null }
        .Concat(_successful).Concat(_candidates.Except(_successful, StringComparer.Ordinal)).ToArray();

    public void MarkSuccessful(string? password)
    {
        if (password is not null && !_successful.Contains(password, StringComparer.Ordinal)) _successful.Add(password);
    }

    public void Dispose()
    {
        _candidates.Clear();
        _successful.Clear();
    }
}

using System.Text.Json.Serialization;

namespace LayerUnpackPlugin.Headless.Contracts;

public enum ArchiveCheckScope { DirectoryOnly, FullContent }
public enum ArchiveCheckState { DirectoryRead, Completed, CompletedWithLimitations, Failed, Cancelled }

/// <summary>证据由实际读取器在成功验证后填写。计数只属于通过该项校验的文件，不能外推到其他条目或内嵌归档。</summary>
public sealed record ArchiveCheckEvidence(string Kind, int Files);

/// <summary>检查不接受用户输出目录，默认完整内容；临时目录只由调用方选择父位置，子目录归用例独占。
/// 不递归打开内嵌包，密码仅此调用使用，不进入请求序列化或字符串表示。</summary>
public sealed class ArchiveCheckRequest
{
    public ArchiveCheckRequest(string sourcePath, ArchiveCheckScope scope = ArchiveCheckScope.FullContent,
        IEnumerable<string>? passwords = null, UnpackLimits? limits = null,
        LegacyNameEncoding nameEncoding = LegacyNameEncoding.Gb18030, string? temporaryDirectory = null)
    {
        SourcePath = sourcePath; Scope = scope; Passwords = Array.AsReadOnly((passwords ?? []).ToArray());
        Limits = limits ?? new(); NameEncoding = nameEncoding; TemporaryDirectory = temporaryDirectory;
    }
    public string SourcePath { get; }
    public ArchiveCheckScope Scope { get; }
    [JsonIgnore] public IReadOnlyList<string> Passwords { get; }
    public UnpackLimits Limits { get; }
    public LegacyNameEncoding NameEncoding { get; }
    public string? TemporaryDirectory { get; }
    public override string ToString() => $"归档检查：{Scope}";
}

/// <summary>目录成功与正文通过使用不同状态；失败/取消不授予整包验证标签。
/// ExpandedBytes 包含失败密码尝试和组合格式中间数据，也是临时空间的累计上界；失败不退款。</summary>
public sealed record ArchiveCheckResult(ArchiveCheckState State, ArchiveCheckScope Scope, string? Format,
    int FilesRead, long ExpandedBytes, int Attempts, TimeSpan Elapsed, IReadOnlyList<ArchiveCheckEvidence> Evidence,
    IReadOnlyList<string> Limitations, UnpackDiagnostic? Error, string? CleanupWarning);
public sealed record ArchiveCheckProgress(int Entries, long ExpandedBytes, int Attempts);

/// <summary>所有读取入口共用的下一步建议。不检查异常文字猜密码；不确定密码与损坏时明确保留两种可能。</summary>
public static class ArchiveDiagnosticAdvice
{
    public static string For(UnpackError code) => code switch
    {
        UnpackError.PasswordRequiredOrInvalid => "补充可用密码后重试；若仍失败，请重新获取原包，加密内容也可能损坏。",
        UnpackError.CorruptArchive => "重新复制或下载原包，再执行完整内容检查。",
        UnpackError.UnsupportedFormat => "确认格式与文件完整性，或用创建该归档的工具处理。",
        UnpackError.UnsupportedEncryption => "使用支持该压缩或加密变体的工具，或请提供方导出普通 ZIP。",
        UnpackError.MissingVolume => "向提供方取得完整单文件归档；当前不能拼接分卷。",
        UnpackError.MissingVolumeOrCorruptArchive => "无法区分缺卷与截断损坏；请提供方重新导出完整单文件归档，当前不能拼接分卷。",
        UnpackError.InvalidNameEncoding => "调整旧 ZIP 文件名编码后重新检查，不要据乱码猜测文件身份。",
        UnpackError.InputUnavailable => "检查来源是否存在、可访问且未被其他程序独占。",
        UnpackError.InputChanged => "来源已改变，请重新加载并检查。",
        UnpackError.BudgetExceeded => "减少单次内容或由调用方评估并调整有限预算。",
        UnpackError.Timeout => "检查存储访问状况，或由调用方评估并调整时间上限。",
        UnpackError.UnsafePath => "请提供方移除链接或不安全路径后重新打包。",
        UnpackError.OutputError => "检查临时空间、权限和包内冲突名称。",
        _ => "保留原包，核对支持范围后重新检查。"
    };
}

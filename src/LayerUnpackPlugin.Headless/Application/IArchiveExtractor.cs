using LayerUnpackPlugin.Headless.Domain;
using LayerUnpackPlugin.Headless.Contracts;

namespace LayerUnpackPlugin.Headless.Application;

public sealed record ExtractedArchive(string Format, IReadOnlyList<string> RelativeFiles, bool UsedPassword, string? Warning = null);

/// <summary>单包引擎端口。实现必须遵守输出目录、预算、取消和脱敏错误契约，不负责递归或密码轮询。</summary>
public interface IArchiveExtractor
{
    Task<ExtractedArchive> ExtractAsync(string source, string destination, string? password, LegacyNameEncoding legacyNameEncoding,
        ExecutionBudget budget, Action<long> progress, CancellationToken cancellationToken);
}

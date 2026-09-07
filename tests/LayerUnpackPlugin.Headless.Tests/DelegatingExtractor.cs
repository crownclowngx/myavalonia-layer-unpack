using LayerUnpackPlugin.Headless.Application;
using LayerUnpackPlugin.Headless.Contracts;
using LayerUnpackPlugin.Headless.Domain;

namespace LayerUnpackPlugin.Headless.Tests;

/// <summary>只用于注入可控失败/阻塞，验证调度行为；真实格式测试始终使用实际引擎。</summary>
internal sealed class DelegatingExtractor(Func<ExtractionCall, Task<ExtractedArchive>> execute) : IArchiveExtractor
{
    public Task<ExtractedArchive> ExtractAsync(string source, string destination, string? password, LegacyNameEncoding encoding,
        ExecutionBudget budget, Action<long> progress, CancellationToken cancellationToken) =>
        execute(new(source, destination, password, encoding, budget, progress, cancellationToken));
}

internal sealed record ExtractionCall(string Source, string Destination, string? Password, LegacyNameEncoding Encoding,
    ExecutionBudget Budget, Action<long> Progress, CancellationToken Token)
{
    internal async Task<ExtractedArchive> WriteAsync(byte[] bytes)
    {
        Budget.AddEntry(); Budget.AddBytes(bytes.Length, bytes.Length);
        await File.WriteAllBytesAsync(Path.Combine(Destination, "result.txt"), bytes, Token);
        Progress(bytes.Length);
        return new("Test", ["result.txt"], Password is not null);
    }
}

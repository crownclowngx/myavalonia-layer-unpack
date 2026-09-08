using System.Security.Cryptography;
using System.Text;
using ICSharpCode.SharpZipLib.Zip;
using LayerUnpackPlugin.Headless.Contracts;
using LayerUnpackPlugin.Headless.Infrastructure;

namespace LayerUnpackPlugin.Headless.Application;

/// <summary>呈现层依赖的窄用例端口。实现负责后台枚举，调用者不必知道 ZIP 解析器或自行启动工作线程。</summary>
public interface IArchiveBrowseService
{
    Task<IArchiveBrowseSession> OpenAsync(BrowseRequest request, IProgress<BrowseProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

public interface IArchiveBrowseSession : IAsyncDisposable
{
    ArchiveCatalog Catalog { get; }
    bool IsInvalidated { get; }
    long ReadBytes { get; }
    long ExpandedBytes { get; }
    Task<BrowseExtractResult> ExtractAsync(BrowseSelection selection, string outputDirectory, string? password = null,
        IProgress<BrowseProgress>? progress = null, CancellationToken cancellationToken = default);
}

/// <summary>ZIP 首期浏览用例。格式矩阵与既有全量解压独立；不提前建立多格式策略框架。</summary>
public sealed class ArchiveBrowseService : IArchiveBrowseService
{
    public async Task<IArchiveBrowseSession> OpenAsync(BrowseRequest request, IProgress<BrowseProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var limits = request.Limits ?? new BrowseLimits(); limits.Validate();
        if (!Enum.IsDefined(request.NameEncoding)) throw new UnpackValidationException("NameEncoding", "请选择已支持的文件名编码。");
        var source = Path.GetFullPath(request.SourcePath);
        var budget = new BrowseBudget(limits);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(limits.Extraction.ArchiveTimeout);
        try
        {
            return await Task.Run(async () =>
            {
                var token = timeout.Token;
                await using var file = BrowseSource.Open(source, limits);
                using var input = new BrowseReadStream(file, budget, token);
                var header = new byte[512]; var count = await input.ReadAsync(header, token).ConfigureAwait(false);
                if (ArchiveProbe.Detect(header.AsSpan(0, count), source) != ArchiveKind.Zip)
                    throw new UnpackFailureException(UnpackError.UnsupportedFormat,
                        "本期仅提供可读中心目录的 ZIP 浏览；此格式请使用“全部解压”进入原解压任务，加密头在该任务内补密。");
                var modified = File.GetLastWriteTimeUtc(source);
                var hash = await BrowseSource.HashAsync(input, budget, progress, token).ConfigureAwait(false);
                var expectedCount = ZipDirectoryGuard.Validate(input, limits, token);
                using var zip = BrowseSource.OpenZip(input, request.NameEncoding);
                if (zip.Count != expectedCount) throw new UnpackFailureException(UnpackError.CorruptArchive, "ZIP 目录数量不一致。");
                var catalog = ZipBrowseCatalog.Read(zip, source, hash, limits,
                    n => progress?.Report(new(BrowseOperation.ReadingDirectory, n, budget.ReadBytes, 0)), token);
                await BrowseSource.VerifyAsync(source, hash, modified, budget, progress, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                return (IArchiveBrowseSession)new ArchiveBrowseSession(catalog, request.NameEncoding, modified, budget);
            }, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception) when (timeout.IsCancellationRequested)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new UnpackFailureException(UnpackError.Timeout, "读取归档目录超过时间上限，未保留目录快照。");
        }
        catch (UnpackFailureException) { throw; }
        catch (DecoderFallbackException) { throw new UnpackFailureException(UnpackError.InvalidNameEncoding, "ZIP 文件名无法解码，请更换旧文件名编码后重新加载。"); }
        catch (ICSharpCode.SharpZipLib.SharpZipBaseException) { throw new UnpackFailureException(UnpackError.CorruptArchive, "ZIP 中心目录无法验证，未加载不完整清单。"); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { throw new UnpackFailureException(UnpackError.InputUnavailable, "无法读取归档，请检查来源、权限和文件占用。"); }
    }
}

/// <summary>来源策略集中在单一边界：不跟随链接，完整 SHA-256 检查同尺寸同时间戳替换，所有扫描计入读取账本。</summary>
internal static class BrowseSource
{
    internal static FileStream Open(string source, BrowseLimits limits)
    {
        PathPolicy.EnsureNoLinks(source);
        var file = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true);
        if (file.Length <= limits.Extraction.MaxInputBytes) return file;
        file.Dispose(); throw new UnpackFailureException(UnpackError.BudgetExceeded, "源归档大小超过浏览输入预算。", true);
    }

    internal static ZipFile OpenZip(Stream input, LegacyNameEncoding encoding) => new(input, leaveOpen: true,
        StringCodec.FromEncoding(ZipArchiveExtractor.GetEncoding(encoding)).WithZipCryptoEncoding(Encoding.UTF8));

    internal static async Task<string> HashAsync(Stream input, BrowseBudget budget, IProgress<BrowseProgress>? progress, CancellationToken token)
    {
        input.Position = 0;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var bytes = new byte[131072]; int count; var blocks = 0;
        progress?.Report(new(BrowseOperation.VerifyingSource, 0, budget.ReadBytes, budget.Expansion.Bytes));
        while ((count = await input.ReadAsync(bytes, token).ConfigureAwait(false)) != 0)
        {
            hash.AppendData(bytes, 0, count);
            if (++blocks % 32 == 0) progress?.Report(new(BrowseOperation.VerifyingSource, 0, budget.ReadBytes, budget.Expansion.Bytes));
        }
        input.Position = 0;
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    internal static async Task VerifyAsync(string source, string hash, DateTime modified, BrowseBudget budget,
        IProgress<BrowseProgress>? progress, CancellationToken token)
    {
        if (!File.Exists(source) || File.GetLastWriteTimeUtc(source) != modified) Changed();
        await using var file = Open(source, budget.Limits);
        using var input = new BrowseReadStream(file, budget, token);
        if (await HashAsync(input, budget, progress, token).ConfigureAwait(false) != hash || File.GetLastWriteTimeUtc(source) != modified) Changed();
    }
    internal static void Changed() => throw new UnpackFailureException(UnpackError.InputChanged, "源归档已变化，旧选择已失效；请重新加载后再选择。");
}

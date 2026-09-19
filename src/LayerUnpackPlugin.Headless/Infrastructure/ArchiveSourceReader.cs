using System.Security.Cryptography;
using System.Text;
using LayerUnpackPlugin.Headless.Application;
using LayerUnpackPlugin.Headless.Contracts;

namespace LayerUnpackPlugin.Headless.Infrastructure;

/// <summary>
/// 一次操作的只读来源租约。成员在打开前冻结，所有句柄由此对象统一释放，解码器只借用流。
/// Windows 的共享读阻止写入和替换；跨平台仍用成员清单、元数据及内容摘要复核，避免旧会话接纳新卷。
/// 不拼接临时大文件；每个卷只有一个小缓冲，输入预算按所有成员的实际长度累计。
/// </summary>
internal sealed class ArchiveSourceReader : IAsyncDisposable
{
    private readonly ArchiveSource _source;
    private readonly UnpackLimits _limits;
    private readonly List<FileStream> _files = [];
    private readonly List<(long Length, DateTime Modified)> _metadata = [];
    public IReadOnlyList<Stream> Streams { get; private set; } = [];

    private ArchiveSourceReader(ArchiveSource source, UnpackLimits limits) { _source = source; _limits = limits; }

    public static async Task<ArchiveSourceReader> OpenAsync(ArchiveSource source, UnpackLimits limits, CancellationToken token)
    {
        var owner = new ArchiveSourceReader(source, limits);
        try
        {
            token.ThrowIfCancellationRequested();
            ArchiveSourceResolver.VerifyMembers(source, limits, token);
            if (source.Error is { } error)
                throw new UnpackFailureException(error.Code, error.Message, error.Code == UnpackError.BudgetExceeded);
            long length = 0;
            foreach (var path in source.Members)
            {
                token.ThrowIfCancellationRequested();
                PathPolicy.EnsureNoLinks(path);
                var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
                owner._files.Add(file);
                if (file.Length > limits.MaxInputBytes - length)
                    throw new UnpackFailureException(UnpackError.BudgetExceeded, "逻辑压缩包所有分卷的合计大小超过读取预算。", true);
                length += file.Length;
                owner._metadata.Add((file.Length, File.GetLastWriteTimeUtc(path)));
            }
            owner.Streams = Array.AsReadOnly(owner._files.Select(f => (Stream)new CancellableReadStream(f, token)).ToArray());
            return owner;
        }
        catch { await owner.DisposeAsync().ConfigureAwait(false); throw; }
    }

    public async Task<string> FingerprintAsync(CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        for (var i = 0; i < _files.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var file = _files[i]; file.Position = 0;
            // 路径和序号也属于快照；同内容换名、增删卷不能冒充同一个来源。
            hash.AppendData(Encoding.UTF8.GetBytes(i + "\0" + _source.Members[i] + "\0" + _metadata[i].Length + "\0" + _metadata[i].Modified.Ticks + "\0"));
            hash.AppendData(await SHA256.HashDataAsync(file, token).ConfigureAwait(false));
            file.Position = 0;
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    public async Task VerifyAsync(string fingerprint, CancellationToken token)
    {
        ArchiveSourceResolver.VerifyMembers(_source, _limits, token);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        for (var i = 0; i < _files.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            PathPolicy.EnsureNoLinks(_source.Members[i]);
            var info = new FileInfo(_source.Members[i]);
            if (!info.Exists || info.Length != _metadata[i].Length || info.LastWriteTimeUtc != _metadata[i].Modified)
                Changed();
            // 重新按路径打开，能识别 Unix 上原句柄仍有效但目录项已被替换的情况。
            await using var current = new FileStream(info.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
            hash.AppendData(Encoding.UTF8.GetBytes(i + "\0" + _source.Members[i] + "\0" + _metadata[i].Length + "\0" + _metadata[i].Modified.Ticks + "\0"));
            hash.AppendData(await SHA256.HashDataAsync(current, token).ConfigureAwait(false));
        }
        if (Convert.ToHexString(hash.GetHashAndReset()) != fingerprint) Changed();
    }

    public async Task<ArchiveKind> ProbeAsync(CancellationToken token)
    {
        var prefix = new byte[512]; var count = 0;
        // 极小卷可能把签名切开，固定探测窗口必须按逻辑字节顺序跨卷读取。
        foreach (var file in _files)
        {
            file.Position = 0;
            count += await file.ReadAtLeastAsync(prefix.AsMemory(count), prefix.Length - count, false, token).ConfigureAwait(false);
            file.Position = 0;
            if (count == prefix.Length) break;
        }
        return ArchiveProbe.Detect(prefix.AsSpan(0, count), _source.PrimaryPath);
    }

    private static void Changed() => throw new UnpackFailureException(UnpackError.InputChanged, "来源成员或内容已变化，请重新识别此组并创建新任务。");
    public async ValueTask DisposeAsync()
    {
        foreach (var file in _files) await file.DisposeAsync().ConfigureAwait(false);
        _files.Clear();
    }

    /// <summary>部分 7z 解码仍同步读取，必须把操作取消传递到同步 Read/Seek；借用方释放不会关闭租约句柄。</summary>
    private sealed class CancellableReadStream(Stream inner, CancellationToken token) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set { token.ThrowIfCancellationRequested(); inner.Position = value; } }
        public override int Read(byte[] buffer, int offset, int count) { token.ThrowIfCancellationRequested(); return inner.Read(buffer, offset, count); }
        public override int Read(Span<byte> buffer) { token.ThrowIfCancellationRequested(); return inner.Read(buffer); }
        public override int ReadByte() { token.ThrowIfCancellationRequested(); return inner.ReadByte(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, cancellationToken);
            return await inner.ReadAsync(buffer, linked.Token).ConfigureAwait(false);
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override long Seek(long offset, SeekOrigin origin) { token.ThrowIfCancellationRequested(); return inner.Seek(offset, origin); }
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

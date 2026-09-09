using System.Security.Cryptography;
using LayerUnpackPlugin.Headless.Application;
using LayerUnpackPlugin.Headless.Contracts;

namespace LayerUnpackPlugin.Headless.Infrastructure;

/// <summary>拉取式写入器的来源边界。TAR 主动读取源流，因此在读取处累计摘要与检查取消，
/// 写完后再次核对长度、摘要和元数据；不能仅凭写入器正常返回就提交被替换的来源。</summary>
internal sealed class VerifiedPackReadStream : Stream
{
    private readonly PackEntry _entry;
    private readonly FileStream _input;
    private readonly CancellationToken _token;
    private readonly Action<int> _copied;
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private long _read;
    internal VerifiedPackReadStream(PackEntry entry, Action<int> copied, CancellationToken token)
    {
        _entry = entry; _token = token; _copied = copied;
        try { _input = PackPlanner.OpenSource(entry); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { _hash.Dispose(); throw new PackFailureException(PackError.InputUnavailable, "来源不可用，请重新检查输入。"); }
    }
    internal void Verify()
    {
        _token.ThrowIfCancellationRequested();
        if (_read != _entry.Length || Convert.ToHexString(_hash.GetHashAndReset()) != _entry.Sha256) PackPlanner.SourceChanged();
        PackPlanner.CheckMetadata(_entry);
    }
    private void Record(ReadOnlySpan<byte> bytes)
    {
        _read += bytes.Length;
        if (_read > _entry.Length) PackPlanner.SourceChanged();
        _hash.AppendData(bytes); _copied(bytes.Length); _token.ThrowIfCancellationRequested();
    }
    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
    public override int Read(Span<byte> buffer)
    {
        _token.ThrowIfCancellationRequested();
        int count;
        try { count = _input.Read(buffer); }
        catch (IOException) { throw new PackFailureException(PackError.InputUnavailable, "读取来源中断。"); }
        Record(buffer[..count]); return count;
    }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        _token.ThrowIfCancellationRequested(); cancellationToken.ThrowIfCancellationRequested();
        int count;
        try { count = await _input.ReadAsync(buffer, _token).ConfigureAwait(false); }
        catch (IOException) { throw new PackFailureException(PackError.InputUnavailable, "读取来源中断。"); }
        Record(buffer.Span[..count]); return count;
    }
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    // 保留底层流的可定位语义；摘要验证仍要求实际消费序列等于准备内容，重复/遗漏读取不会被当作成功。
    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _entry.Length;
    public override long Position { get => _input.Position; set { _token.ThrowIfCancellationRequested(); _input.Position = value; } }
    public override long Seek(long offset, SeekOrigin origin)
    {
        _token.ThrowIfCancellationRequested(); return _input.Seek(offset, origin);
    }
    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    protected override void Dispose(bool disposing) { if (disposing) { _input.Dispose(); _hash.Dispose(); } base.Dispose(disposing); }
}

using LayerUnpackPlugin.Headless.Contracts;

namespace LayerUnpackPlugin.Headless.Infrastructure;

/// <summary>在真实写入位置约束归档长度；ZIP 回填头部不重复累计预算。保留可定位流语义，归档关闭后由事务关闭底层文件。</summary>
internal sealed class PackOutputStream(Stream inner, long limit, CancellationToken token) : Stream
{
    public override bool CanRead => false;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => true;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set { token.ThrowIfCancellationRequested(); inner.Position = value; } }
    private void Check(int count)
    {
        token.ThrowIfCancellationRequested();
        if (count > limit - Position) throw new PackFailureException(PackError.BudgetExceeded, "生成 ZIP 超过归档输出上限。");
    }
    public override void Write(byte[] buffer, int offset, int count) { Check(count); inner.Write(buffer, offset, count); }
    public override void Write(ReadOnlySpan<byte> buffer) { Check(buffer.Length); inner.Write(buffer); }
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    { Check(buffer.Length); await inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false); }
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    public override void Flush() { token.ThrowIfCancellationRequested(); inner.Flush(); }
    public override Task FlushAsync(CancellationToken cancellationToken) { token.ThrowIfCancellationRequested(); return inner.FlushAsync(cancellationToken); }
    public override long Seek(long offset, SeekOrigin origin) { token.ThrowIfCancellationRequested(); return inner.Seek(offset, origin); }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

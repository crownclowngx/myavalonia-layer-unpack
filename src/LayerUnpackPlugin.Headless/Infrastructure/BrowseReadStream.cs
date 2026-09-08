using LayerUnpackPlugin.Headless.Contracts;
using LayerUnpackPlugin.Headless.Domain;

namespace LayerUnpackPlugin.Headless.Infrastructure;

/// <summary>原包读取账本与展开账本分开；中心目录的重复读取、源摘要和补密失败均按实际读取字节计费。</summary>
internal sealed class BrowseBudget(BrowseLimits limits)
{
    internal BrowseLimits Limits { get; } = limits;
    internal ExecutionBudget Expansion { get; } = new(limits.Extraction);
    internal long ReadBytes { get; private set; }
    internal void CheckRead(int requested)
    {
        if (requested > Limits.MaxReadBytes - ReadBytes)
            throw new UnpackFailureException(UnpackError.BudgetExceeded, "浏览会话累计读取量超过预算，请结束本次会话。", true);
    }
    internal void AddRead(int actual) => ReadBytes += actual;
}

/// <summary>在第三方同步解析器下面观察取消和读取预算，使中心目录构造期间也有中断点。此包装不拥有底层文件。</summary>
internal sealed class BrowseReadStream(Stream inner, BrowseBudget budget, CancellationToken token) : Stream
{
    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set { token.ThrowIfCancellationRequested(); inner.Position = value; } }
    private int BeforeRead(int requested)
    {
        token.ThrowIfCancellationRequested();
        var count = (int)Math.Min(requested, Math.Max(0, Length - Position));
        budget.CheckRead(count); return count;
    }
    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
    public override int Read(Span<byte> buffer)
    {
        var count = BeforeRead(buffer.Length);
        var actual = inner.Read(buffer[..count]); budget.AddRead(actual); return actual;
    }
    public override int ReadByte()
    {
        Span<byte> one = stackalloc byte[1]; return Read(one) == 0 ? -1 : one[0];
    }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var count = BeforeRead(buffer.Length);
        var actual = await inner.ReadAsync(buffer[..count], token).ConfigureAwait(false); budget.AddRead(actual); return actual;
    }
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    public override long Seek(long offset, SeekOrigin origin) { token.ThrowIfCancellationRequested(); return inner.Seek(offset, origin); }
    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

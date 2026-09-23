namespace Deskspan.Net;

internal sealed class PrefixedStream : Stream
{
    private readonly Stream _inner;
    private readonly byte[] _prefix;
    private int _prefixOffset;

    public PrefixedStream(Stream inner, byte[] prefix)
    {
        _inner = inner;
        _prefix = prefix;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush() => _inner.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_prefixOffset < _prefix.Length)
        {
            var count = Math.Min(buffer.Length, _prefix.Length - _prefixOffset);
            _prefix.AsSpan(_prefixOffset, count).CopyTo(buffer.Span);
            _prefixOffset += count;
            return count;
        }

        return await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
        _inner.WriteAsync(buffer, cancellationToken);
}

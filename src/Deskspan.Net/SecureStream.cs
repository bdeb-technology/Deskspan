using System.Buffers.Binary;

namespace Deskspan.Net;

public sealed class SecureStream : IAsyncDisposable
{
    private readonly Stream _stream;
    private readonly AeadBox _box;
    private readonly SemaphoreSlim _write = new(1, 1);

    public SecureStream(Stream stream, byte[] key)
    {
        _stream = stream;
        _box = new AeadBox(key, "DSTCP");
    }

    public async Task SendAsync(NetMessage message, CancellationToken cancellationToken)
    {
        var packet = _box.Seal(message.Encode());
        if (packet.Length > Protocol.MaxFrame)
            throw new InvalidOperationException("Message is too large.");
        var header = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(header, (ushort)packet.Length);
        await _write.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
            await _stream.WriteAsync(packet, cancellationToken).ConfigureAwait(false);
            await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _write.Release();
        }
    }

    public async Task<NetMessage> ReceiveAsync(CancellationToken cancellationToken)
    {
        var header = new byte[2];
        await PairingHandshake.ReadExactlyAsync(_stream, header, cancellationToken).ConfigureAwait(false);
        int length = BinaryPrimitives.ReadUInt16LittleEndian(header);
        if (length < 28 || length > Protocol.MaxFrame)
            throw new InvalidDataException("Frame length is invalid.");
        var packet = new byte[length];
        await PairingHandshake.ReadExactlyAsync(_stream, packet, cancellationToken).ConfigureAwait(false);
        return NetMessage.Decode(_box.Open(packet));
    }

    public async ValueTask DisposeAsync()
    {
        _box.Dispose();
        _write.Dispose();
        await _stream.DisposeAsync().ConfigureAwait(false);
    }
}

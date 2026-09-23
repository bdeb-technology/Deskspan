namespace Deskspan.Net;

public static class SessionHeader
{
    public static async Task WriteAsync(Stream stream, byte[] deviceId, CancellationToken cancellationToken)
    {
        var packet = new byte[20];
        var magic = "DSSE"u8.ToArray();
        Buffer.BlockCopy(magic, 0, packet, 0, 4);
        Buffer.BlockCopy(deviceId, 0, packet, 4, 16);
        await stream.WriteAsync(packet, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<Guid> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        var magic = new byte[4];
        await PairingHandshake.ReadExactlyAsync(stream, magic, cancellationToken).ConfigureAwait(false);
        if (!magic.AsSpan().SequenceEqual(Protocol.SessionMagic))
            throw new InvalidDataException("Unexpected session header.");
        var id = new byte[16];
        await PairingHandshake.ReadExactlyAsync(stream, id, cancellationToken).ConfigureAwait(false);
        return new Guid(id);
    }
}

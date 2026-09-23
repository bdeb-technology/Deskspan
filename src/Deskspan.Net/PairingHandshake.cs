using System.Buffers.Binary;
using System.Text;

namespace Deskspan.Net;

public readonly record struct PairedSecrets(Guid PeerId, string PeerName, byte[] PeerPublicKey, SessionKeys Keys);

public sealed class QuickConnectRequest
{
    private readonly TaskCompletionSource<bool> _decision = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public required Guid PeerId { get; init; }
    public required string PeerName { get; init; }

    internal Task<bool> Decision => _decision.Task;

    public void Accept() => _decision.TrySetResult(true);

    public void Reject() => _decision.TrySetResult(false);
}

public static class PairingHandshake
{
    public static async Task<PairedSecrets> JoinQuickAsync(Stream stream, DeviceIdentity self, CancellationToken cancellationToken)
    {
        var nameBytes = Encoding.UTF8.GetBytes(self.Name);
        var body = new byte[16 + 32 + 1 + nameBytes.Length];
        self.IdBytes.CopyTo(body, 0);
        self.PublicKey.CopyTo(body, 16);
        body[16 + 32] = (byte)nameBytes.Length;
        nameBytes.CopyTo(body, 16 + 32 + 1);
        await WriteFrameAsync(stream, Protocol.QuickMagic.ToArray(), body, cancellationToken).ConfigureAwait(false);

        var status = new byte[1];
        await ReadExactlyAsync(stream, status, cancellationToken).ConfigureAwait(false);
        if (status[0] != 0)
            throw new InvalidOperationException("The other computer did not allow the connection.");

        var idBytes = new byte[16];
        var publicKey = new byte[32];
        var nameLength = new byte[1];
        await ReadExactlyAsync(stream, idBytes, cancellationToken).ConfigureAwait(false);
        await ReadExactlyAsync(stream, publicKey, cancellationToken).ConfigureAwait(false);
        await ReadExactlyAsync(stream, nameLength, cancellationToken).ConfigureAwait(false);
        if (nameLength[0] > Protocol.MaxNameBytes)
            throw new InvalidDataException("Name is invalid.");
        var peerName = new byte[nameLength[0]];
        await ReadExactlyAsync(stream, peerName, cancellationToken).ConfigureAwait(false);
        var peerId = new Guid(idBytes);
        var name = Encoding.UTF8.GetString(peerName);
        return new PairedSecrets(peerId, name, publicKey, self.KeysWith(publicKey, peerId.ToByteArray()));
    }

    public static async Task<PairedSecrets?> TryHostQuickAsync(byte[] body, Stream stream, Func<QuickConnectRequest, Task<bool>> approve, DeviceIdentity self, CancellationToken cancellationToken)
    {
        if (body.Length < 16 + 32 + 1)
            return null;
        var offset = 0;
        var peerId = new Guid(body.AsSpan(offset, 16));
        offset += 16;
        var peerPublic = body.AsSpan(offset, 32).ToArray();
        offset += 32;
        var name = ReadName(body, ref offset);
        var request = new QuickConnectRequest { PeerId = peerId, PeerName = name };
        bool allowed;
        try
        {
            allowed = await approve(request).ConfigureAwait(false);
        }
        catch (Exception)
        {
            allowed = false;
        }

        if (!allowed)
        {
            try { await stream.WriteAsync(new byte[] { 1 }, cancellationToken).ConfigureAwait(false); } catch { }
            return null;
        }

        await WriteAcceptanceAsync(stream, self, cancellationToken).ConfigureAwait(false);
        return new PairedSecrets(peerId, name, peerPublic, self.KeysWith(peerPublic, peerId.ToByteArray()));
    }

    public static Task<PairedSecrets> HostAsync(Stream stream, string code, DeviceIdentity self, CancellationToken cancellationToken)
    {
        var expected = ParseCode(code);
        return HostCoreAsync(stream, given => given == expected, self, cancellationToken);
    }

    public static async Task<PairedSecrets?> TryHostAsync(Stream stream, Func<uint, bool> codeIsValid, DeviceIdentity self, CancellationToken cancellationToken)
    {
        try
        {
            return await HostCoreAsync(stream, codeIsValid, self, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static async Task<PairedSecrets> HostCoreAsync(Stream stream, Func<uint, bool> codeIsValid, DeviceIdentity self, CancellationToken cancellationToken)
    {
        var payload = await ReadPayloadAsync(stream, cancellationToken).ConfigureAwait(false);
        var offset = 0;
        var given = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(offset));
        offset += 4;
        if (!codeIsValid(given))
        {
            await stream.WriteAsync(new byte[] { 1 }, cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException("Pairing code was not accepted.");
        }

        var peerId = new Guid(payload.AsSpan(offset, 16));
        offset += 16;
        var peerPublic = payload.AsSpan(offset, 32).ToArray();
        offset += 32;
        var name = ReadName(payload, ref offset);
        await WriteAcceptanceAsync(stream, self, cancellationToken).ConfigureAwait(false);
        return new PairedSecrets(peerId, name, peerPublic, self.KeysWith(peerPublic, peerId.ToByteArray()));
    }

    public static async Task<PairedSecrets> JoinAsync(Stream stream, string code, DeviceIdentity self, CancellationToken cancellationToken)
    {
        var body = BuildJoinRequest(code, self);
        await WriteFrameAsync(stream, "DSP1"u8.ToArray(), body, cancellationToken).ConfigureAwait(false);

        var status = new byte[1];
        await ReadExactlyAsync(stream, status, cancellationToken).ConfigureAwait(false);
        if (status[0] != 0)
            throw new InvalidOperationException("Pairing code was not accepted.");

        var idBytes = new byte[16];
        var publicKey = new byte[32];
        var nameLength = new byte[1];
        await ReadExactlyAsync(stream, idBytes, cancellationToken).ConfigureAwait(false);
        await ReadExactlyAsync(stream, publicKey, cancellationToken).ConfigureAwait(false);
        await ReadExactlyAsync(stream, nameLength, cancellationToken).ConfigureAwait(false);
        if (nameLength[0] > Protocol.MaxNameBytes)
            throw new InvalidDataException("Name is invalid.");
        var peerName = new byte[nameLength[0]];
        await ReadExactlyAsync(stream, peerName, cancellationToken).ConfigureAwait(false);
        var peerId = new Guid(idBytes);
        var name = Encoding.UTF8.GetString(peerName);
        return new PairedSecrets(peerId, name, publicKey, self.KeysWith(publicKey, peerId.ToByteArray()));
    }

    private static byte[] BuildJoinRequest(string code, DeviceIdentity self)
    {
        var nameBytes = Encoding.UTF8.GetBytes(self.Name);
        var body = new byte[4 + 16 + 32 + 1 + nameBytes.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(body, ParseCode(code));
        self.IdBytes.CopyTo(body, 4);
        self.PublicKey.CopyTo(body, 4 + 16);
        body[4 + 16 + 32] = (byte)nameBytes.Length;
        nameBytes.CopyTo(body, 4 + 16 + 32 + 1);
        return body;
    }

    public static uint ParseCode(string code)
    {
        var digits = new string((code ?? "").Where(char.IsDigit).ToArray());
        if (digits.Length != 6 || !uint.TryParse(digits, out var value))
            throw new ArgumentException("Enter the 6-digit pairing code.");
        return value;
    }

    private static async Task WriteAcceptanceAsync(Stream stream, DeviceIdentity self, CancellationToken cancellationToken)
    {
        var nameBytes = Encoding.UTF8.GetBytes(self.Name);
        var body = new byte[1 + 16 + 32 + 1 + nameBytes.Length];
        body[0] = 0;
        self.IdBytes.CopyTo(body.AsSpan(1));
        self.PublicKey.CopyTo(body.AsSpan(1 + 16));
        body[1 + 16 + 32] = (byte)nameBytes.Length;
        nameBytes.CopyTo(body.AsSpan(1 + 16 + 32 + 1));
        await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<byte[]> ReadPayloadAsync(Stream stream, CancellationToken cancellationToken)
    {
        var magic = new byte[4];
        await ReadExactlyAsync(stream, magic, cancellationToken).ConfigureAwait(false);
        if (!magic.AsSpan().SequenceEqual(Protocol.PairMagic))
            throw new InvalidDataException("Unexpected pairing header.");
        var lengthBytes = new byte[2];
        await ReadExactlyAsync(stream, lengthBytes, cancellationToken).ConfigureAwait(false);
        int length = BinaryPrimitives.ReadUInt16LittleEndian(lengthBytes);
        if (length < 4 + 16 + 32 + 1 || length > Protocol.MaxPairingFrame)
            throw new InvalidDataException("Pairing payload length is invalid.");
        var body = new byte[length];
        await ReadExactlyAsync(stream, body, cancellationToken).ConfigureAwait(false);
        return body;
    }

    private static async Task WriteFrameAsync(Stream stream, byte[] magic, byte[] body, CancellationToken cancellationToken)
    {
        var packet = new byte[magic.Length + 2 + body.Length];
        Buffer.BlockCopy(magic, 0, packet, 0, magic.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(magic.Length), (ushort)body.Length);
        Buffer.BlockCopy(body, 0, packet, magic.Length + 2, body.Length);
        await stream.WriteAsync(packet, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string ReadName(byte[] payload, ref int offset)
    {
        if (offset >= payload.Length)
            throw new InvalidDataException("Name is missing.");
        int length = payload[offset++];
        if (length > Protocol.MaxNameBytes || offset + length > payload.Length)
            throw new InvalidDataException("Name is invalid.");
        var name = Encoding.UTF8.GetString(payload, offset, length);
        offset += length;
        return name;
    }

    public static async Task ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int n = await stream.ReadAsync(buffer[read..], cancellationToken).ConfigureAwait(false);
            if (n == 0)
                throw new EndOfStreamException("The other PC closed the connection.");
            read += n;
        }
    }
}

using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Deskspan.Net;

public readonly record struct PairedSecrets(Guid PeerId, string PeerName, byte[] PeerPublicKey, SessionKeys Keys);

public sealed class PairingException(string message) : InvalidOperationException(message);

public sealed class QuickConnectRequest
{
    private readonly TaskCompletionSource<bool> _decision = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public required Guid PeerId { get; init; }
    public required string PeerName { get; init; }
    public required string VerifyCode { get; init; }

    internal Task<bool> Decision => _decision.Task;

    public void Accept() => _decision.TrySetResult(true);

    public void Reject() => _decision.TrySetResult(false);
}

public static class PairingHandshake
{
    public const int CodeDigits = 9;
    public const int RoutingDigits = 6;
    private const int ConfirmLength = 32;
    private const int NonceLength = 16;
    private const byte Accepted = 0;
    private const byte Refused = 1;
    private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(30);

    private readonly record struct Party(Guid Id, byte[] PublicKey, string Name, byte[] Encoded);

    public static string NewCode()
    {
        var digits = new char[CodeDigits];
        for (var i = 0; i < digits.Length; i++)
            digits[i] = (char)('0' + RandomNumberGenerator.GetInt32(10));
        return new string(digits);
    }

    public static string NormalizeCode(string? code)
    {
        var digits = new string((code ?? "").Where(char.IsAsciiDigit).ToArray());
        if (digits.Length != CodeDigits)
            throw new ArgumentException("Enter the 9-digit pairing code.");
        return digits;
    }

    public static string RoutingPart(string code) => NormalizeCode(code)[..RoutingDigits];

    public static string FormatCode(string code) =>
        code.Length == CodeDigits ? code[..3] + " " + code[3..6] + " " + code[6..] : code;

    public static async Task<PairedSecrets> JoinAsync(Stream stream, string code, DeviceIdentity self, CancellationToken cancellationToken)
    {
        var password = NormalizeCode(code);
        var me = Encode(self);
        using var step = StepToken(cancellationToken);
        var token = step.Token;

        var spake = new Spake2(true, password);
        await WriteFrameAsync(stream, Protocol.PairMagic.ToArray(), [.. me, .. spake.Share], token).ConfigureAwait(false);

        if (await ReadStatusAsync(stream, token).ConfigureAwait(false) != Accepted)
            throw new PairingException("That code is not active on the other PC. Create a new code there and try again.");
        var reply = await ReadBodyAsync(stream, token).ConfigureAwait(false);
        var offset = 0;
        var host = ReadParty(reply, ref offset);
        var hostShare = Take(reply, ref offset, Spake2.ShareLength);
        var hostConfirm = Take(reply, ref offset, ConfirmLength);
        if (offset != reply.Length)
            throw new InvalidDataException("Pairing reply is invalid.");

        var keys = spake.Finish(hostShare, me, host.Encoded);
        if (!CryptographicOperations.FixedTimeEquals(hostConfirm, keys.PeerConfirmation))
            throw new PairingException("The code did not match. Create a new code on the other PC and try again.");

        await stream.WriteAsync(keys.OwnConfirmation, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
        if (await ReadStatusAsync(stream, token).ConfigureAwait(false) != Accepted)
            throw new PairingException("The other PC did not accept the pairing. Create a new code there and try again.");
        return Secrets(self, host);
    }

    public static Task<PairedSecrets> HostAsync(Stream stream, string code, DeviceIdentity self, CancellationToken cancellationToken)
    {
        var password = NormalizeCode(code);
        var used = 0;
        return HostCoreAsync(stream, () => Interlocked.Exchange(ref used, 1) == 0 ? password : null, self, cancellationToken);
    }

    public static async Task<PairedSecrets?> TryHostAsync(Stream stream, Func<string?> claimCode, DeviceIdentity self, CancellationToken cancellationToken)
    {
        try
        {
            return await HostCoreAsync(stream, claimCode, self, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is PairingException or InvalidDataException or IOException)
        {
            return null;
        }
    }

    private static async Task<PairedSecrets> HostCoreAsync(Stream stream, Func<string?> claimCode, DeviceIdentity self, CancellationToken cancellationToken)
    {
        using var step = StepToken(cancellationToken);
        var token = step.Token;
        var request = await ReadFrameAsync(stream, Protocol.PairMagic.ToArray(), token).ConfigureAwait(false);
        var offset = 0;
        var joiner = ReadParty(request, ref offset);
        var joinerShare = Take(request, ref offset, Spake2.ShareLength);
        if (offset != request.Length)
            throw new InvalidDataException("Pairing request is invalid.");

        var password = claimCode();
        if (password == null)
        {
            await WriteStatusAsync(stream, Refused, token).ConfigureAwait(false);
            throw new PairingException("No pairing code is active.");
        }

        var me = Encode(self);
        var spake = new Spake2(false, password);
        var keys = spake.Finish(joinerShare, joiner.Encoded, me);
        var reply = new byte[1 + 2 + me.Length + Spake2.ShareLength + ConfirmLength];
        reply[0] = Accepted;
        BinaryPrimitives.WriteUInt16LittleEndian(reply.AsSpan(1), (ushort)(reply.Length - 3));
        me.CopyTo(reply, 3);
        spake.Share.CopyTo(reply, 3 + me.Length);
        keys.OwnConfirmation.CopyTo(reply, 3 + me.Length + Spake2.ShareLength);
        await stream.WriteAsync(reply, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);

        var joinerConfirm = new byte[ConfirmLength];
        await ReadExactlyAsync(stream, joinerConfirm, token).ConfigureAwait(false);
        if (!CryptographicOperations.FixedTimeEquals(joinerConfirm, keys.PeerConfirmation))
        {
            await WriteStatusAsync(stream, Refused, token).ConfigureAwait(false);
            throw new PairingException("The code did not match.");
        }

        await WriteStatusAsync(stream, Accepted, token).ConfigureAwait(false);
        return Secrets(self, joiner);
    }

    public static async Task<PairedSecrets> JoinQuickAsync(Stream stream, DeviceIdentity self, Action<string>? showVerifyCode, CancellationToken cancellationToken)
    {
        var me = Encode(self);
        var nonce = RandomNumberGenerator.GetBytes(NonceLength);
        PairedSecrets secrets;
        string verify;
        using (var step = StepToken(cancellationToken))
        {
            await WriteFrameAsync(stream, Protocol.QuickMagic.ToArray(), [.. me, .. Commit(nonce, me)], step.Token).ConfigureAwait(false);
            var reply = await ReadBodyAsync(stream, step.Token).ConfigureAwait(false);
            var offset = 0;
            var host = ReadParty(reply, ref offset);
            var hostNonce = Take(reply, ref offset, NonceLength);
            if (offset != reply.Length)
                throw new InvalidDataException("Quick connect reply is invalid.");
            await stream.WriteAsync(nonce, step.Token).ConfigureAwait(false);
            await stream.FlushAsync(step.Token).ConfigureAwait(false);
            verify = VerifyCode(me, host.Encoded, nonce, hostNonce);
            secrets = Secrets(self, host);
        }

        showVerifyCode?.Invoke(verify);
        if (await ReadStatusAsync(stream, cancellationToken).ConfigureAwait(false) != Accepted)
            throw new PairingException("The other computer did not allow the connection.");
        return secrets;
    }

    public static async Task<PairedSecrets?> TryHostQuickAsync(Stream stream, Func<QuickConnectRequest, Task<bool>> approve, DeviceIdentity self, CancellationToken cancellationToken)
    {
        Party joiner;
        string verify;
        var me = Encode(self);
        using (var step = StepToken(cancellationToken))
        {
            var request = await ReadFrameAsync(stream, Protocol.QuickMagic.ToArray(), step.Token).ConfigureAwait(false);
            var offset = 0;
            joiner = ReadParty(request, ref offset);
            var commitment = Take(request, ref offset, 32);
            if (offset != request.Length)
                return null;

            var nonce = RandomNumberGenerator.GetBytes(NonceLength);
            byte[] reply = [.. me, .. nonce];
            var header = new byte[2];
            BinaryPrimitives.WriteUInt16LittleEndian(header, (ushort)reply.Length);
            await stream.WriteAsync(header, step.Token).ConfigureAwait(false);
            await stream.WriteAsync(reply, step.Token).ConfigureAwait(false);
            await stream.FlushAsync(step.Token).ConfigureAwait(false);

            var joinerNonce = new byte[NonceLength];
            await ReadExactlyAsync(stream, joinerNonce, step.Token).ConfigureAwait(false);
            if (!CryptographicOperations.FixedTimeEquals(commitment, Commit(joinerNonce, joiner.Encoded)))
                return null;
            verify = VerifyCode(joiner.Encoded, me, joinerNonce, nonce);
        }

        var ask = new QuickConnectRequest { PeerId = joiner.Id, PeerName = joiner.Name, VerifyCode = verify };
        bool allowed;
        try
        {
            allowed = await approve(ask).ConfigureAwait(false);
        }
        catch (Exception)
        {
            allowed = false;
        }

        try
        {
            await WriteStatusAsync(stream, allowed ? Accepted : Refused, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (!allowed)
        {
        }

        return allowed ? Secrets(self, joiner) : null;
    }

    internal static string VerifyCode(byte[] joiner, byte[] host, byte[] joinerNonce, byte[] hostNonce)
    {
        var hash = SHA256.HashData(Spake2.Transcript("Deskspan quick verify"u8.ToArray(), joiner, host, joinerNonce, hostNonce));
        var value = BinaryPrimitives.ReadUInt32BigEndian(hash) % 1_000_000;
        return value.ToString("D6");
    }

    private static byte[] Commit(byte[] nonce, byte[] party) =>
        SHA256.HashData(Spake2.Transcript("Deskspan quick commit"u8.ToArray(), nonce, party));

    private static PairedSecrets Secrets(DeviceIdentity self, Party peer) =>
        new(peer.Id, peer.Name, peer.PublicKey, self.KeysWith(peer.PublicKey, peer.Id.ToByteArray()));

    private static CancellationTokenSource StepToken(CancellationToken cancellationToken)
    {
        var step = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        step.CancelAfter(StepTimeout);
        return step;
    }

    private static byte[] Encode(DeviceIdentity self)
    {
        var nameBytes = Encoding.UTF8.GetBytes(self.Name);
        if (nameBytes.Length > Protocol.MaxNameBytes)
            nameBytes = nameBytes[..Protocol.MaxNameBytes];
        var body = new byte[16 + 32 + 1 + nameBytes.Length];
        self.IdBytes.CopyTo(body, 0);
        self.PublicKey.CopyTo(body, 16);
        body[48] = (byte)nameBytes.Length;
        nameBytes.CopyTo(body, 49);
        return body;
    }

    private static Party ReadParty(byte[] payload, ref int offset)
    {
        var start = offset;
        var id = new Guid(Take(payload, ref offset, 16));
        var publicKey = Take(payload, ref offset, 32);
        var name = ReadName(payload, ref offset);
        return new Party(id, publicKey, name, payload[start..offset]);
    }

    private static byte[] Take(byte[] payload, ref int offset, int count)
    {
        if (offset + count > payload.Length)
            throw new InvalidDataException("Pairing message is too short.");
        var part = payload.AsSpan(offset, count).ToArray();
        offset += count;
        return part;
    }

    private static async Task<byte> ReadStatusAsync(Stream stream, CancellationToken cancellationToken)
    {
        var status = new byte[1];
        await ReadExactlyAsync(stream, status, cancellationToken).ConfigureAwait(false);
        return status[0];
    }

    private static async Task WriteStatusAsync(Stream stream, byte status, CancellationToken cancellationToken)
    {
        await stream.WriteAsync(new[] { status }, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<byte[]> ReadFrameAsync(Stream stream, byte[] expectedMagic, CancellationToken cancellationToken)
    {
        var magic = new byte[4];
        await ReadExactlyAsync(stream, magic, cancellationToken).ConfigureAwait(false);
        if (!magic.AsSpan().SequenceEqual(expectedMagic))
            throw new InvalidDataException("Unexpected pairing header.");
        return await ReadBodyAsync(stream, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<byte[]> ReadBodyAsync(Stream stream, CancellationToken cancellationToken)
    {
        var lengthBytes = new byte[2];
        await ReadExactlyAsync(stream, lengthBytes, cancellationToken).ConfigureAwait(false);
        int length = BinaryPrimitives.ReadUInt16LittleEndian(lengthBytes);
        if (length < 16 + 32 + 1 || length > Protocol.MaxPairingFrame)
            throw new InvalidDataException("Pairing payload length is invalid.");
        var body = new byte[length];
        await ReadExactlyAsync(stream, body, cancellationToken).ConfigureAwait(false);
        return body;
    }

    private static async Task WriteFrameAsync(Stream stream, byte[] magic, byte[] body, CancellationToken cancellationToken)
    {
        var packet = new byte[magic.Length + 2 + body.Length];
        magic.CopyTo(packet, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(magic.Length), (ushort)body.Length);
        body.CopyTo(packet, magic.Length + 2);
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

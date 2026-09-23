using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace Deskspan.Net;

public static class UdpRendezvous
{
    public const int TokenLength = 16;
    public const int MaxPayload = 256;
    private const int HeaderLength = 5 + TokenLength;

    public static ReadOnlySpan<byte> RegisterMagic => "DSUR"u8;
    public static ReadOnlySpan<byte> AnswerMagic => "DSUA"u8;
    public static ReadOnlySpan<byte> ForwardMagic => "DSUF"u8;
    public static ReadOnlySpan<byte> PunchMagic => "DSUP"u8;

    public static byte[] Token(byte[] udpKey)
    {
        var input = new byte["DSUDPRV"u8.Length + udpKey.Length];
        "DSUDPRV"u8.CopyTo(input);
        udpKey.CopyTo(input, "DSUDPRV"u8.Length);
        return SHA256.HashData(input)[..TokenLength];
    }

    public static byte[] Register(byte[] token) => Frame(RegisterMagic, token, ReadOnlySpan<byte>.Empty);

    public static byte[] Forward(byte[] token, ReadOnlySpan<byte> payload) => Frame(ForwardMagic, token, payload);

    public static byte[] Punch(byte[] token, bool hearing) => Frame(PunchMagic, token, [hearing ? (byte)1 : (byte)0]);

    public static byte[] Answer(byte[] token, IPEndPoint other)
    {
        var address = other.Address.MapToIPv4().GetAddressBytes();
        var body = new byte[6];
        address.CopyTo(body, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(4), (ushort)other.Port);
        return Frame(AnswerMagic, token, body);
    }

    public static bool TryRead(ReadOnlySpan<byte> packet, ReadOnlySpan<byte> magic, out ReadOnlySpan<byte> token, out ReadOnlySpan<byte> payload)
    {
        token = default;
        payload = default;
        if (packet.Length < HeaderLength || !packet[..4].SequenceEqual(magic) || packet[4] != Protocol.Version)
            return false;
        token = packet.Slice(5, TokenLength);
        payload = packet[HeaderLength..];
        return payload.Length <= MaxPayload;
    }

    public static bool TryReadAnswer(ReadOnlySpan<byte> packet, ReadOnlySpan<byte> expectedToken, out IPEndPoint? other)
    {
        other = null;
        if (!TryRead(packet, AnswerMagic, out var token, out var payload) || payload.Length != 6 || !token.SequenceEqual(expectedToken))
            return false;
        var port = BinaryPrimitives.ReadUInt16LittleEndian(payload[4..]);
        if (port == 0)
            return false;
        other = new IPEndPoint(new IPAddress(payload[..4]), port);
        return true;
    }

    public static bool TryReadPunch(ReadOnlySpan<byte> packet, ReadOnlySpan<byte> expectedToken, out bool hearing)
    {
        hearing = false;
        if (!TryRead(packet, PunchMagic, out var token, out var payload) || !token.SequenceEqual(expectedToken))
            return false;
        hearing = payload.Length > 0 && payload[0] == 1;
        return true;
    }

    private static byte[] Frame(ReadOnlySpan<byte> magic, byte[] token, ReadOnlySpan<byte> payload)
    {
        var packet = new byte[HeaderLength + payload.Length];
        magic.CopyTo(packet);
        packet[4] = Protocol.Version;
        token.AsSpan(0, TokenLength).CopyTo(packet.AsSpan(5));
        payload.CopyTo(packet.AsSpan(HeaderLength));
        return packet;
    }
}

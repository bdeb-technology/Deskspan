using System.Buffers.Binary;

namespace Deskspan.Net;

public static class MouseDatagram
{
    public const byte MoveKind = 1;
    public const byte InputKind = 2;

    public static byte[] Seal(AeadBox box, byte[] senderId, uint sequence, ushort x, ushort y)
    {
        Span<byte> plain = stackalloc byte[9];
        plain[0] = MoveKind;
        BinaryPrimitives.WriteUInt32LittleEndian(plain[1..], sequence);
        BinaryPrimitives.WriteUInt16LittleEndian(plain[5..], x);
        BinaryPrimitives.WriteUInt16LittleEndian(plain[7..], y);
        return Frame(box, senderId, plain);
    }

    public static int Write(AeadBox box, ReadOnlySpan<byte> senderId, ReadOnlySpan<byte> plain, Span<byte> destination)
    {
        if (senderId.Length < 16 || destination.Length < 16 + 12 + plain.Length + 16)
            return 0;
        senderId[..16].CopyTo(destination);
        return 16 + box.SealInto(plain, destination[16..]);
    }

    public static byte[] SealInput(AeadBox box, byte[] senderId, NetMessage message)
    {
        var body = message.Encode();
        var plain = new byte[1 + body.Length];
        plain[0] = InputKind;
        body.CopyTo(plain, 1);
        return Frame(box, senderId, plain);
    }

    public static bool TryOpen(AeadBox box, ReadOnlySpan<byte> packet, byte[] expectedSender, uint latestSequence, out uint sequence, out ushort x, out ushort y)
    {
        sequence = 0;
        x = 0;
        y = 0;
        if (!TryOpenAny(box, packet, expectedSender, out var plain) || plain.Length != 9 || plain[0] != MoveKind)
            return false;
        sequence = BinaryPrimitives.ReadUInt32LittleEndian(plain.AsSpan(1));
        if (!Sequence.IsNewer(sequence, latestSequence))
            return false;
        x = BinaryPrimitives.ReadUInt16LittleEndian(plain.AsSpan(5));
        y = BinaryPrimitives.ReadUInt16LittleEndian(plain.AsSpan(7));
        return true;
    }

    public static bool TryOpenAny(AeadBox box, ReadOnlySpan<byte> packet, byte[] expectedSender, out byte[] plain)
    {
        plain = [];
        if (packet.Length < 16 + 28 + 1 || !packet[..16].SequenceEqual(expectedSender))
            return false;
        try
        {
            plain = box.Open(packet[16..]);
        }
        catch (Exception)
        {
            return false;
        }

        return plain.Length > 0;
    }

    private static byte[] Frame(AeadBox box, ReadOnlySpan<byte> senderId, ReadOnlySpan<byte> plain)
    {
        var packet = new byte[16 + 12 + plain.Length + 16];
        var length = Write(box, senderId, plain, packet);
        return length == packet.Length ? packet : packet[..length];
    }
}

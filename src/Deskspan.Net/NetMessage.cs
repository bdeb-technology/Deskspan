using System.Buffers.Binary;
using System.Text;

namespace Deskspan.Net;

public abstract record NetMessage
{
    public sealed record Hello(string Name, int VirtualX, int VirtualY, int VirtualWidth, int VirtualHeight, int UdpPort) : NetMessage;
    public sealed record Heartbeat : NetMessage;
    public sealed record KeyStroke(bool Down, ushort VirtualKey, ushort ScanCode, bool Extended, uint Id = 0) : NetMessage;
    public sealed record PointerButton(byte Button, bool Down, ushort X, ushort Y, uint Id = 0) : NetMessage;
    public sealed record Wheel(short Delta, bool Horizontal, ushort X, ushort Y, uint Id = 0) : NetMessage;
    public sealed record ControlState(bool Active, uint Id = 0) : NetMessage;
    public sealed record Release : NetMessage;
    public sealed record ClipboardText(string Text) : NetMessage;
    public sealed record PointerMove(uint Sequence, ushort X, ushort Y) : NetMessage;
    public sealed record Ignored(byte Type) : NetMessage;

    private const byte HelloType = 1;
    private const byte HeartbeatType = 2;
    private const byte KeyType = 3;
    private const byte ButtonType = 4;
    private const byte WheelType = 5;
    private const byte ControlType = 6;
    private const byte ReleaseType = 7;
    private const byte ClipboardType = 8;
    private const byte MoveType = 9;

    public byte[] Encode()
    {
        using var stream = new MemoryStream(64);
        using var writer = new BinaryWriter(stream);
        switch (this)
        {
            case Hello hello:
                writer.Write(HelloType);
                WriteName(writer, hello.Name);
                writer.Write(hello.VirtualX);
                writer.Write(hello.VirtualY);
                writer.Write(hello.VirtualWidth);
                writer.Write(hello.VirtualHeight);
                writer.Write(hello.UdpPort);
                break;
            case Heartbeat:
                writer.Write(HeartbeatType);
                break;
            case KeyStroke key:
                writer.Write(KeyType);
                writer.Write(key.Down);
                writer.Write(key.VirtualKey);
                writer.Write(key.ScanCode);
                writer.Write(key.Extended);
                writer.Write(key.Id);
                break;
            case PointerButton button:
                writer.Write(ButtonType);
                writer.Write(button.Button);
                writer.Write(button.Down);
                writer.Write(button.X);
                writer.Write(button.Y);
                writer.Write(button.Id);
                break;
            case Wheel wheel:
                writer.Write(WheelType);
                writer.Write(wheel.Delta);
                writer.Write(wheel.Horizontal);
                writer.Write(wheel.X);
                writer.Write(wheel.Y);
                writer.Write(wheel.Id);
                break;
            case ControlState control:
                writer.Write(ControlType);
                writer.Write(control.Active);
                writer.Write(control.Id);
                break;
            case Release:
                writer.Write(ReleaseType);
                break;
            case ClipboardText clipboard:
                var text = LimitClipboard(clipboard.Text);
                var textBytes = Encoding.UTF8.GetBytes(text);
                writer.Write(ClipboardType);
                writer.Write(textBytes.Length);
                writer.Write(textBytes.AsSpan());
                break;
            case PointerMove move:
                writer.Write(MoveType);
                writer.Write(move.Sequence);
                writer.Write(move.X);
                writer.Write(move.Y);
                break;
            default:
                throw new InvalidOperationException("Unknown message.");
        }

        return stream.ToArray();
    }

    public static NetMessage Decode(ReadOnlySpan<byte> payload)
    {
        if (payload.Length == 0)
            throw new InvalidDataException("Empty message.");
        var offset = 1;
        return payload[0] switch
        {
            HelloType => new Hello(
                ReadName(payload, ref offset),
                ReadInt(payload, ref offset),
                ReadInt(payload, ref offset),
                ReadInt(payload, ref offset),
                ReadInt(payload, ref offset),
                ReadInt(payload, ref offset)),
            HeartbeatType => new Heartbeat(),
            KeyType => new KeyStroke(
                ReadBool(payload, ref offset),
                ReadUShort(payload, ref offset),
                ReadUShort(payload, ref offset),
                ReadBool(payload, ref offset),
                ReadUInt(payload, ref offset)),
            ButtonType => new PointerButton(
                ReadByte(payload, ref offset),
                ReadBool(payload, ref offset),
                ReadUShort(payload, ref offset),
                ReadUShort(payload, ref offset),
                ReadUInt(payload, ref offset)),
            WheelType => new Wheel(
                ReadShort(payload, ref offset),
                ReadBool(payload, ref offset),
                ReadUShort(payload, ref offset),
                ReadUShort(payload, ref offset),
                ReadUInt(payload, ref offset)),
            ControlType => new ControlState(ReadBool(payload, ref offset), ReadUInt(payload, ref offset)),
            ReleaseType => new Release(),
            ClipboardType => new ClipboardText(ReadClipboard(payload, ref offset)),
            MoveType => new PointerMove(ReadUInt(payload, ref offset), ReadUShort(payload, ref offset), ReadUShort(payload, ref offset)),
            _ => new Ignored(payload[0])
        };
    }

    public static uint InputId(NetMessage message) => message switch
    {
        KeyStroke key => key.Id,
        PointerButton button => button.Id,
        Wheel wheel => wheel.Id,
        ControlState control => control.Id,
        _ => 0
    };

    public static NetMessage? WithInputId(NetMessage message, uint id) => message switch
    {
        KeyStroke key => key with { Id = id },
        PointerButton button => button with { Id = id },
        Wheel wheel => wheel with { Id = id },
        ControlState control => control with { Id = id },
        _ => null
    };

    public static string LimitClipboard(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return "";
        var utf8 = Encoding.UTF8.GetBytes(text);
        if (utf8.Length <= Protocol.MaxClipboardBytes)
            return text;
        var chars = new char[text.Length];
        var decoder = Encoding.UTF8.GetDecoder();
        decoder.Convert(utf8, 0, Protocol.MaxClipboardBytes, chars, 0, chars.Length, false, out _, out var used, out _);
        return new string(chars, 0, used);
    }

    private static string ReadClipboard(ReadOnlySpan<byte> payload, ref int offset)
    {
        int length = ReadInt(payload, ref offset);
        if (length < 0 || length > Protocol.MaxClipboardBytes || offset + length > payload.Length)
            throw new InvalidDataException("Clipboard text is invalid.");
        var text = Encoding.UTF8.GetString(payload.Slice(offset, length));
        offset += length;
        return text;
    }

    private static void WriteName(BinaryWriter writer, string name)
    {
        var bytes = Encoding.UTF8.GetBytes(DeviceIdentity.Sanitize(name));
        writer.Write((byte)bytes.Length);
        writer.Write(bytes);
    }

    private static string ReadName(ReadOnlySpan<byte> payload, ref int offset)
    {
        int length = ReadByte(payload, ref offset);
        if (length > Protocol.MaxNameBytes || offset + length > payload.Length)
            throw new InvalidDataException("Name is invalid.");
        var name = Encoding.UTF8.GetString(payload.Slice(offset, length));
        offset += length;
        return name;
    }

    private static int ReadInt(ReadOnlySpan<byte> payload, ref int offset)
    {
        var value = BinaryPrimitives.ReadInt32LittleEndian(payload[offset..]);
        offset += 4;
        return value;
    }

    private static short ReadShort(ReadOnlySpan<byte> payload, ref int offset)
    {
        var value = BinaryPrimitives.ReadInt16LittleEndian(payload[offset..]);
        offset += 2;
        return value;
    }

    private static uint ReadUInt(ReadOnlySpan<byte> payload, ref int offset)
    {
        var value = BinaryPrimitives.ReadUInt32LittleEndian(payload[offset..]);
        offset += 4;
        return value;
    }

    private static ushort ReadUShort(ReadOnlySpan<byte> payload, ref int offset)
    {
        var value = BinaryPrimitives.ReadUInt16LittleEndian(payload[offset..]);
        offset += 2;
        return value;
    }

    private static byte ReadByte(ReadOnlySpan<byte> payload, ref int offset)
    {
        if (offset >= payload.Length)
            throw new InvalidDataException("Message ended early.");
        return payload[offset++];
    }

    private static bool ReadBool(ReadOnlySpan<byte> payload, ref int offset) => ReadByte(payload, ref offset) != 0;
}

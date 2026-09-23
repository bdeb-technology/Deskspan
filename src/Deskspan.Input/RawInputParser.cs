using System.Runtime.InteropServices;

namespace Deskspan.Input;

[StructLayout(LayoutKind.Sequential)]
internal struct RawInputHeader
{
    public uint Type;
    public uint Size;
    public nint Device;
    public nint Parameter;
}

[StructLayout(LayoutKind.Explicit)]
internal struct RawMouse
{
    [FieldOffset(0)] public ushort Flags;
    [FieldOffset(4)] public ushort ButtonFlags;
    [FieldOffset(6)] public ushort ButtonData;
    [FieldOffset(8)] public uint RawButtons;
    [FieldOffset(12)] public int LastX;
    [FieldOffset(16)] public int LastY;
    [FieldOffset(20)] public uint ExtraInformation;
}

[StructLayout(LayoutKind.Explicit)]
internal struct RawKeyboard
{
    [FieldOffset(0)] public ushort MakeCode;
    [FieldOffset(2)] public ushort Flags;
    [FieldOffset(4)] public ushort Reserved;
    [FieldOffset(6)] public ushort VirtualKey;
    [FieldOffset(8)] public uint Message;
    [FieldOffset(16)] public nuint ExtraInformation;
}

public readonly record struct RawPointerEvent(int DeltaX, int DeltaY, bool HasMove, ushort ButtonFlags, ushort ButtonData, uint ExtraInformation);

public readonly record struct RawKeyEvent(ushort VirtualKey, ushort ScanCode, bool IsUp, bool Extended, uint ExtraInformation);

public static class RawInputParser
{
    public static int HeaderSize => Marshal.SizeOf<RawInputHeader>();

    public static bool TryParseMouse(ReadOnlySpan<byte> raw, out RawPointerEvent pointer)
    {
        pointer = default;
        if (raw.Length < HeaderSize + Marshal.SizeOf<RawMouse>() || BitConverter.ToUInt32(raw) != 0)
            return false;
        var data = raw[HeaderSize..];
        var flags = BitConverter.ToUInt16(data);
        var buttons = BitConverter.ToUInt16(data[4..]);
        var buttonData = BitConverter.ToUInt16(data[6..]);
        var x = BitConverter.ToInt32(data[12..]);
        var y = BitConverter.ToInt32(data[16..]);
        var extra = BitConverter.ToUInt32(data[20..]);
        var relative = (flags & 1) == 0;
        pointer = new RawPointerEvent(x, y, relative && (x != 0 || y != 0), buttons, buttonData, extra);
        return true;
    }

    public static bool TryParseKey(ReadOnlySpan<byte> raw, out RawKeyEvent key)
    {
        key = default;
        if (raw.Length < HeaderSize + 16 || BitConverter.ToUInt32(raw) != 1)
            return false;
        var data = raw[HeaderSize..];
        var scan = BitConverter.ToUInt16(data);
        var flags = BitConverter.ToUInt16(data[2..]);
        var virtualKey = BitConverter.ToUInt16(data[6..]);
        var extra = BitConverter.ToUInt32(data[12..]);
        key = new RawKeyEvent(virtualKey, scan, (flags & 1) != 0, (flags & 2) != 0, extra);
        return true;
    }
}

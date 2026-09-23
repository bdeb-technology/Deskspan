namespace Deskspan.Input;

public sealed class ModifierState
{
    private const int Control = 1;
    private const int LeftControl = 2;
    private const int RightControl = 4;
    private const int Alt = 8;
    private const int LeftAlt = 16;
    private const int RightAlt = 32;
    private const int Shift = 64;
    private const int LeftShift = 128;
    private const int RightShift = 256;
    private const int LeftWindows = 512;
    private const int RightWindows = 1024;

    private int _down;

    public void Note(int virtualKey, bool down)
    {
        var bit = Bit(virtualKey);
        if (bit == 0)
            return;
        if (down)
            _down |= bit;
        else
            _down &= ~bit;
    }

    public bool TrackedDown(int virtualKey, bool altFromEvent)
    {
        if (altFromEvent && virtualKey is 0x12 or 0xA4 or 0xA5)
            return true;
        var mask = Mask(virtualKey);
        return mask != 0 && (_down & mask) != 0;
    }

    private static int Mask(int virtualKey) => virtualKey switch
    {
        0x11 or 0xA2 or 0xA3 => Control | LeftControl | RightControl,
        0x12 or 0xA4 or 0xA5 => Alt | LeftAlt | RightAlt,
        0x10 or 0xA0 or 0xA1 => Shift | LeftShift | RightShift,
        0x5B or 0x5C => LeftWindows | RightWindows,
        _ => 0
    };

    private static int Bit(int virtualKey) => virtualKey switch
    {
        0x11 => Control,
        0xA2 => LeftControl,
        0xA3 => RightControl,
        0x12 => Alt,
        0xA4 => LeftAlt,
        0xA5 => RightAlt,
        0x10 => Shift,
        0xA0 => LeftShift,
        0xA1 => RightShift,
        0x5B => LeftWindows,
        0x5C => RightWindows,
        _ => 0
    };
}

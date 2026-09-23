namespace Deskspan.Input;

public readonly record struct HookKey(ushort VirtualKey, ushort ScanCode, bool Down, bool Extended)
{
    public const uint ExtendedFlag = 0x01;

    public static bool TryCreate(uint virtualKey, uint scanCode, uint flags, bool down, bool up, out HookKey key)
    {
        key = default;
        if (virtualKey is 0 or > ushort.MaxValue || (!down && !up))
            return false;
        key = new HookKey((ushort)virtualKey, (ushort)scanCode, down, (flags & ExtendedFlag) != 0);
        return true;
    }
}

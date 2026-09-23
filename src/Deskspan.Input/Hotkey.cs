namespace Deskspan.Input;

[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Control = 1,
    Alt = 2,
    Shift = 4,
    Windows = 8
}

public readonly record struct Hotkey(HotkeyModifiers Modifiers, ushort VirtualKey)
{
    public static Hotkey Default { get; } = new(HotkeyModifiers.Control, 0x04);

    public bool IsMouseButton => VirtualKey is >= 0x01 and <= 0x06;

    public bool ModifiersMatch(Func<int, bool> isDown)
    {
        if (Modifiers == HotkeyModifiers.None)
            return false;
        var control = isDown(0x11) || isDown(0xA2) || isDown(0xA3);
        var alt = isDown(0x12) || isDown(0xA4) || isDown(0xA5);
        var shift = isDown(0x10) || isDown(0xA0) || isDown(0xA1);
        var windows = isDown(0x5B) || isDown(0x5C);
        return Modifiers.HasFlag(HotkeyModifiers.Control) == control
            && Modifiers.HasFlag(HotkeyModifiers.Alt) == alt
            && Modifiers.HasFlag(HotkeyModifiers.Shift) == shift
            && Modifiers.HasFlag(HotkeyModifiers.Windows) == windows;
    }

    public bool ModifiersMatch(Func<int, bool> trackedDown, Func<int, bool> asyncDown)
    {
        if (Modifiers == HotkeyModifiers.None)
            return false;
        var control = Group(Modifiers.HasFlag(HotkeyModifiers.Control), trackedDown, asyncDown, 0x11, 0xA2, 0xA3);
        var alt = Group(Modifiers.HasFlag(HotkeyModifiers.Alt), trackedDown, asyncDown, 0x12, 0xA4, 0xA5);
        var shift = Group(Modifiers.HasFlag(HotkeyModifiers.Shift), trackedDown, asyncDown, 0x10, 0xA0, 0xA1);
        var windows = Group(Modifiers.HasFlag(HotkeyModifiers.Windows), trackedDown, asyncDown, 0x5B, 0x5C);
        return Modifiers.HasFlag(HotkeyModifiers.Control) == control
            && Modifiers.HasFlag(HotkeyModifiers.Alt) == alt
            && Modifiers.HasFlag(HotkeyModifiers.Shift) == shift
            && Modifiers.HasFlag(HotkeyModifiers.Windows) == windows;
    }

    public bool TriggerDown(int virtualKey, bool isKeyDown, Func<int, bool> isDown) =>
        isKeyDown && virtualKey == VirtualKey && ModifiersMatch(isDown);

    public bool TriggerDown(int virtualKey, bool isKeyDown, Func<int, bool> trackedDown, Func<int, bool> asyncDown) =>
        isKeyDown && virtualKey == VirtualKey && ModifiersMatch(trackedDown, asyncDown);

    private static bool Group(bool required, Func<int, bool> trackedDown, Func<int, bool> asyncDown, params int[] keys)
    {
        foreach (var key in keys)
        {
            if (trackedDown(key))
                return true;
        }

        if (!required)
            return false;
        foreach (var key in keys)
        {
            if (asyncDown(key))
                return true;
        }

        return false;
    }

    public string Format()
    {
        var parts = new List<string>(5);
        if (Modifiers.HasFlag(HotkeyModifiers.Control))
            parts.Add("Ctrl");
        if (Modifiers.HasFlag(HotkeyModifiers.Alt))
            parts.Add("Alt");
        if (Modifiers.HasFlag(HotkeyModifiers.Shift))
            parts.Add("Shift");
        if (Modifiers.HasFlag(HotkeyModifiers.Windows))
            parts.Add("Win");
        parts.Add(KeyName(VirtualKey));
        return string.Join(" + ", parts);
    }

    public static string KeyName(ushort virtualKey) => virtualKey switch
    {
        >= 0x41 and <= 0x5A => ((char)virtualKey).ToString(),
        >= 0x30 and <= 0x39 => ((char)virtualKey).ToString(),
        >= 0x70 and <= 0x87 => "F" + (virtualKey - 0x6F),
        0x20 => "Space",
        0x0D => "Enter",
        0x09 => "Tab",
        0x1B => "Esc",
        0x08 => "Backspace",
        0x2E => "Delete",
        0x2D => "Insert",
        0x24 => "Home",
        0x23 => "End",
        0x21 => "Page Up",
        0x22 => "Page Down",
        0x25 => "Left",
        0x26 => "Up",
        0x27 => "Right",
        0x28 => "Down",
        0x01 => "Left click",
        0x02 => "Right click",
        0x04 => "Mouse scroll click",
        0x05 => "Mouse 4",
        0x06 => "Mouse 5",
        _ => "Key " + virtualKey
    };
}

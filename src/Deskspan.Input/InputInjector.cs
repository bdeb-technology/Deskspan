namespace Deskspan.Input;

public static class InputInjector
{
    public static void MoveNormalized(ushort x, ushort y)
    {
        var input = Mouse(x, y, 0, NativeMethods.MouseMove | NativeMethods.MouseAbsolute | NativeMethods.MouseVirtualDesk);
        Send(input);
    }

    public static void Button(byte button, bool down)
    {
        var flags = (button, down) switch
        {
            (0, true) => 0x0002u,
            (0, false) => 0x0004u,
            (1, true) => 0x0008u,
            (1, false) => 0x0010u,
            (2, true) => 0x0020u,
            (2, false) => 0x0040u,
            (3, true) => 0x0080u,
            (3, false) => 0x0100u,
            (4, true) => 0x0080u,
            (4, false) => 0x0100u,
            _ => 0u
        };
        if (flags == 0)
            return;
        uint data = button switch
        {
            3 => 1u << 16,
            4 => 2u << 16,
            _ => 0u
        };
        Send(Mouse(0, 0, data, flags));
    }

    public static void Wheel(int delta, bool horizontal)
    {
        var flags = horizontal ? NativeMethods.MouseHorizontalWheel : NativeMethods.MouseWheel;
        Send(Mouse(0, 0, unchecked((uint)delta), flags));
    }

    public readonly record struct KeyPlan(ushort VirtualKey, ushort ScanCode, uint Flags);

    public static KeyPlan PlanKey(ushort virtualKey, ushort scanCode, bool down, bool extended)
    {
        var scan = (ushort)(scanCode & 0xFF);
        var extendedKey = extended || (scanCode & 0xFF00) == 0xE000;
        uint flags = 0;
        if (!down)
            flags |= NativeMethods.KeyUp;
        if (extendedKey)
            flags |= NativeMethods.KeyExtended;
        if (scan != 0)
        {
            flags |= NativeMethods.KeyScanCode;
            return new KeyPlan(0, scan, flags);
        }

        return new KeyPlan(virtualKey, 0, flags);
    }

    public static void Key(ushort virtualKey, ushort scanCode, bool down, bool extended)
    {
        var plan = PlanKey(virtualKey, scanCode, down, extended);
        var input = new NativeMethods.INPUT
        {
            Type = 1,
            Data = new NativeMethods.InputUnion
            {
                Keyboard = new NativeMethods.KEYBDINPUT
                {
                    VirtualKey = plan.VirtualKey,
                    ScanCode = plan.ScanCode,
                    Flags = plan.Flags,
                    ExtraInfo = NativeMethods.Stamp
                }
            }
        };
        Send(input);
    }

    public static void ClipToCurrentCursor()
    {
        if (!NativeMethods.GetCursorPos(out var point))
            return;
        var rect = new NativeMethods.RECT
        {
            Left = point.X,
            Top = point.Y,
            Right = point.X + 1,
            Bottom = point.Y + 1
        };
        NativeMethods.ClipCursor(ref rect);
    }

    public static void ReleaseClip() => NativeMethods.ClipCursor(0);

    public static int PointerSpeedScale()
    {
        var speed = 10;
        NativeMethods.SystemParametersInfo(0x0070, 0, ref speed, 0);
        speed = Math.Clamp(speed, 1, 20);
        return speed;
    }

    private static NativeMethods.INPUT Mouse(int x, int y, uint data, uint flags) => new()
    {
        Type = 0,
        Data = new NativeMethods.InputUnion
        {
            Mouse = new NativeMethods.MOUSEINPUT
            {
                X = x,
                Y = y,
                MouseData = data,
                Flags = flags,
                ExtraInfo = NativeMethods.Stamp
            }
        }
    };

    private static void Send(NativeMethods.INPUT input)
    {
        var sent = NativeMethods.SendInput(1, [input], System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.INPUT>());
        _ = sent;
    }
}

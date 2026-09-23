using System.Runtime.InteropServices;

namespace Deskspan.Input.Tests;

public class InputLayoutTests
{
    [Fact]
    public void SendInputStructIsTheWindows64Size() =>
        Assert.Equal(40, NativeLayout.InputStructSize);

    [Fact]
    public void RawMouseLayoutMatchesTheParser()
    {
        Assert.Equal(24, RawInputParser.HeaderSize);
        Assert.Equal(4, Marshal.OffsetOf<DeskspanProbe>(nameof(DeskspanProbe.Buttons)).ToInt32());
        Assert.Equal(12, Marshal.OffsetOf<DeskspanProbe>(nameof(DeskspanProbe.LastX)).ToInt32());
        Assert.Equal(20, Marshal.OffsetOf<DeskspanProbe>(nameof(DeskspanProbe.Extra)).ToInt32());

        var raw = new byte[RawInputParser.HeaderSize + 24];
        raw[0] = 0;
        WriteInt(raw, RawInputParser.HeaderSize + 12, 7);
        WriteInt(raw, RawInputParser.HeaderSize + 16, -3);
        Assert.True(RawInputParser.TryParseMouse(raw, out var pointer));
        Assert.True(pointer.HasMove);
        Assert.Equal(7, pointer.DeltaX);
        Assert.Equal(-3, pointer.DeltaY);
    }

    [Fact]
    public void NormalizedCursorRoundTripsWithinOnePixel()
    {
        var screen = new VirtualScreen(-1920, 0, 3840, 1080);
        for (var pixel = screen.X; pixel < screen.X + screen.Width; pixel += 17)
        {
            var normalized = screen.NormalizeX(pixel);
            var back = screen.PixelX(normalized);
            Assert.InRange(Math.Abs(back - pixel), 0, 1);
        }
    }

    [Fact]
    public void DefaultShortcutIsCtrlScrollClick()
    {
        Assert.Equal("Ctrl + Mouse scroll click", Hotkey.Default.Format());
        Assert.True(Hotkey.Default.IsMouseButton);
        Assert.True(Hotkey.Default.TriggerDown(0x04, true, vk => vk == 0x11));
        Assert.False(Hotkey.Default.TriggerDown(0x04, true, vk => vk is 0x11 or 0x12));
        Assert.False(Hotkey.Default.TriggerDown(0x04, false, vk => vk == 0x11));
        Assert.True(ShortcutMatches(control: true, spuriousShift: true));
        Assert.False(ShortcutMatches(control: true, alt: true));
        Assert.False(ShortcutMatches(control: true, shiftHeld: true));
        Assert.True(ShortcutMatches(control: true, shiftReleased: true, spuriousShift: true));
        Assert.True(MouseChord.Button(0x0207, 0, out var down) == 0x04 && down);
        Assert.True(MouseChord.Button(0x0208, 0, out down) == 0x04 && !down);
    }

    [Fact]
    public void KeyboardPacketUsesTheHardwareScanCode()
    {
        var raw = new byte[RawInputParser.HeaderSize + 16];
        raw[0] = 1;
        raw[RawInputParser.HeaderSize] = 0x1E;
        raw[RawInputParser.HeaderSize + 6] = 0x41;
        Assert.True(RawInputParser.TryParseKey(raw, out var key));
        Assert.Equal((ushort)0x41, key.VirtualKey);
        Assert.Equal((ushort)0x1E, key.ScanCode);
        Assert.False(key.IsUp);

        var down = InputInjector.PlanKey(0x41, 0x1E, true, false);
        Assert.Equal((ushort)0, down.VirtualKey);
        Assert.Equal((ushort)0x1E, down.ScanCode);
        Assert.Equal(8u, down.Flags);

        var arrow = InputInjector.PlanKey(0x25, 0xE04B, false, true);
        Assert.Equal((ushort)0x4B, arrow.ScanCode);
        Assert.Equal(0x0008u | 0x0001u | 0x0002u, arrow.Flags);
    }

    [Fact]
    public void SwallowedHookKeyKeepsScanCodeAndExtendedFlag()
    {
        Assert.True(HookKey.TryCreate(0x25, 0x4B, HookKey.ExtendedFlag, true, false, out var left));
        Assert.Equal((ushort)0x25, left.VirtualKey);
        Assert.Equal((ushort)0x4B, left.ScanCode);
        Assert.True(left.Down);
        Assert.True(left.Extended);
        Assert.True(HookKey.TryCreate(0x41, 0x1E, 0, false, true, out var released));
        Assert.False(released.Down);
        Assert.False(HookKey.TryCreate(0x41, 0x1E, 0, false, false, out _));
    }

    private static bool ShortcutMatches(bool control, bool alt = false, bool altFromEvent = false, bool shiftHeld = false, bool shiftReleased = false, bool spuriousShift = false)
    {
        var state = new ModifierState();
        if (control)
            state.Note(0xA2, true);
        if (alt)
            state.Note(0xA4, true);
        if (shiftHeld || shiftReleased)
            state.Note(0xA0, true);
        if (shiftReleased)
            state.Note(0xA0, false);

        return Hotkey.Default.TriggerDown(
            Hotkey.Default.VirtualKey,
            true,
            vk => state.TrackedDown(vk, altFromEvent),
            vk => spuriousShift && vk is 0x10 or 0xA0 or 0xA1);
    }

    private static void WriteInt(byte[] buffer, int offset, int value)
    {
        buffer[offset] = (byte)value;
        buffer[offset + 1] = (byte)(value >> 8);
        buffer[offset + 2] = (byte)(value >> 16);
        buffer[offset + 3] = (byte)(value >> 24);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DeskspanProbe
    {
        public ushort Flags;
        public uint Buttons;
        public uint RawButtons;
        public int LastX;
        public int LastY;
        public uint Extra;
    }
}

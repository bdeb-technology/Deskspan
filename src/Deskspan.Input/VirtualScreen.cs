namespace Deskspan.Input;

public readonly record struct VirtualScreen(int X, int Y, int Width, int Height)
{
    public static VirtualScreen FromSystem() => new(
        NativeMethods.GetSystemMetrics(76),
        NativeMethods.GetSystemMetrics(77),
        Math.Max(1, NativeMethods.GetSystemMetrics(78)),
        Math.Max(1, NativeMethods.GetSystemMetrics(79)));

    public ushort NormalizeX(int pixel)
    {
        var span = Math.Max(1, Width - 1);
        var local = Math.Clamp(pixel - X, 0, span);
        return (ushort)(local * 65535L / span);
    }

    public ushort NormalizeY(int pixel)
    {
        var span = Math.Max(1, Height - 1);
        var local = Math.Clamp(pixel - Y, 0, span);
        return (ushort)(local * 65535L / span);
    }

    public int PixelX(ushort normalized)
    {
        var span = Math.Max(1, Width - 1);
        return X + (int)(normalized * (long)span / 65535L);
    }

    public int PixelY(ushort normalized)
    {
        var span = Math.Max(1, Height - 1);
        return Y + (int)(normalized * (long)span / 65535L);
    }
}

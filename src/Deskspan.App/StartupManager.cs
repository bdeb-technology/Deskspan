using Microsoft.Win32;

namespace Deskspan;

// Registers Deskspan to open automatically when Windows starts, so the paired
// connection comes back on its own after a reboot. Writes a per-user Run entry
// (no admin needed) and self-heals the stored path on every launch.
internal static class StartupManager
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Deskspan";

    public static void Apply(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (key == null)
                return;
            if (enabled)
            {
                var exe = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(exe) && exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    key.SetValue(ValueName, "\"" + exe + "\" --min");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch (Exception)
        {
        }
    }
}

using System.Drawing;
using System.Windows.Forms;
using Deskspan.Net;

namespace Deskspan;

internal sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _toggle;

    public TrayIcon(ShareController controller, SettingsWindow settings)
    {
        _toggle = new ToolStripMenuItem("Start control", null, (_, _) => controller.Toggle());
        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem("Open", null, (_, _) => settings.ShowFromTray()));
        menu.Items.Add(_toggle);
        menu.Items.Add(new ToolStripMenuItem("Create a code", null, (_, _) =>
        {
            controller.CreatePairCode();
            settings.ShowFromTray();
        }));
        menu.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) => System.Windows.Application.Current.Shutdown()));
        _icon = new NotifyIcon
        {
            Icon = Draw(Color.FromArgb(92, 107, 122)),
            Visible = true,
            Text = ShareController.Edition.Name,
            ContextMenuStrip = menu
        };
        _icon.DoubleClick += (_, _) => settings.ShowFromTray();
        menu.Opening += (_, _) => UpdateToggle(controller.Snapshot());
    }

    public void Apply(AppSnapshot snapshot)
    {
        var color = snapshot.Mode switch
        {
            ShareMode.Controlling => Color.FromArgb(180, 83, 9),
            ShareMode.Controlled => Color.FromArgb(29, 78, 216),
            _ => snapshot.Linked ? Color.FromArgb(15, 118, 110) : Color.FromArgb(92, 107, 122)
        };
        var previous = _icon.Icon;
        _icon.Icon = Draw(color);
        previous?.Dispose();
        var text = ShareController.Edition.Name + " — " + snapshot.Status;
        _icon.Text = text.Length <= 63 ? text : text[..63];
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }

    private void UpdateToggle(AppSnapshot snapshot)
    {
        _toggle.Text = snapshot.Mode == ShareMode.Controlling ? "Return to this PC" : "Start control";
    }

    private static Icon Draw(Color color)
    {
        using var bitmap = new Bitmap(32, 32);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            graphics.Clear(Color.Transparent);
            using var brush = new SolidBrush(color);
            graphics.FillEllipse(brush, 1, 1, 30, 30);
            using var white = new SolidBrush(Color.White);
            graphics.FillPolygon(white, new[] { new Point(12, 8), new Point(23, 16), new Point(17, 17), new Point(20, 25), new Point(17, 26), new Point(14, 18), new Point(11, 23) });
        }

        var handle = bitmap.GetHicon();
        try
        {
            using var icon = Icon.FromHandle(handle);
            return (Icon)icon.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool DestroyIcon(nint handle);
}

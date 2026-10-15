using System.Drawing;
using System.Windows.Forms;
using Deskspan.Net;

namespace Deskspan;

internal sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly Icon _brand;
    private readonly ToolStripMenuItem _toggle;

    public TrayIcon(ShareController controller, SettingsWindow settings)
    {
        _brand = BrandIcon.Load(32);
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
            Icon = _brand,
            Visible = true,
            Text = ShareController.Edition.Name,
            ContextMenuStrip = menu
        };
        _icon.DoubleClick += (_, _) => settings.ShowFromTray();
        menu.Opening += (_, _) => UpdateToggle(controller.Snapshot());
    }

    public void Apply(AppSnapshot snapshot)
    {
        var text = ShareController.Edition.Name + " — " + snapshot.Status;
        _icon.Text = text.Length <= 63 ? text : text[..63];
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _brand.Dispose();
    }

    private void UpdateToggle(AppSnapshot snapshot)
    {
        _toggle.Text = snapshot.Mode == ShareMode.Controlling ? "Return to this PC" : "Start control";
    }
}

internal static class BrandIcon
{
    public static Icon Load(int size)
    {
        var stream = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/Deskspan.ico"))?.Stream
            ?? throw new InvalidOperationException("Deskspan.ico is missing.");
        using (stream)
            return new Icon(stream, size, size);
    }
}

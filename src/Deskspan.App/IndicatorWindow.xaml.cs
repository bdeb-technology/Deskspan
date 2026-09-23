using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Color = System.Windows.Media.Color;
using Deskspan.Net;

namespace Deskspan;

public partial class IndicatorWindow : Window
{
    private ShareMode _shown = ShareMode.Idle;

    public IndicatorWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => MakeClickThrough();
    }

    public void Apply(AppSnapshot snapshot)
    {
        if (snapshot.Mode == _shown)
            return;
        _shown = snapshot.Mode;
        var hotkey = snapshot.Hotkey;
        switch (snapshot.Mode)
        {
            case ShareMode.Controlling:
                Bar.Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x7C, 0x2D, 0x12));
                Label.Text = "Controlling " + (snapshot.PeerName ?? "the other PC") + "  ·  " + hotkey + " to return";
                break;
            case ShareMode.Controlled:
                Bar.Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x1E, 0x3A, 0x8A));
                Label.Text = (snapshot.PeerName ?? "The other PC") + " is using this PC  ·  " + hotkey + " to take it back";
                break;
            default:
                Bar.Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x1E, 0x29, 0x3B));
                Label.Text = "Back on this PC";
                break;
        }

        Flash();
    }

    private void Flash()
    {
        BeginAnimation(OpacityProperty, null);
        Opacity = 0;
        Show();
        UpdateLayout();
        var area = SystemParameters.WorkArea;
        Left = area.Left + (area.Width - ActualWidth) / 2;
        Top = area.Top + 10;
        var fade = new DoubleAnimationUsingKeyFrames();
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(0.85, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(150))));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(0.85, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1950))));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(2350))));
        fade.Completed += (_, _) =>
        {
            if (Opacity <= 0.01)
                Hide();
        };
        BeginAnimation(OpacityProperty, fade);
    }

    private void MakeClickThrough()
    {
        var handle = new WindowInteropHelper(this).Handle;
        var style = GetWindowLongPtr(handle, -20);
        SetWindowLongPtr(handle, -20, style | 0x20 | 0x80 | 0x08000000);
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(nint window, int index, nint value);
}

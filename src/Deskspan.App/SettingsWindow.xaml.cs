using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using Deskspan.Input;
using Deskspan.Net;

namespace Deskspan;

public partial class SettingsWindow : Window
{
    private const int WmClipboardUpdate = 0x031D;

    private readonly ShareController _controller;
    private bool _searching;
    private System.Windows.Threading.DispatcherTimer? _searchTimer;
    private IntPtr _clipboardHwnd;

    public SettingsWindow(ShareController controller)
    {
        _controller = controller;
        InitializeComponent();
        _controller.RemoteClipboard += text => Dispatcher.BeginInvoke(() => ApplyRemoteClipboard(text));
    }

    public void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    public void Apply(AppSnapshot snapshot)
    {
        StatusText.Text = snapshot.Status;
        DetailText.Text = snapshot.Detail;
        HotkeyText.Text = snapshot.Hotkey;
        if (!NameBox.IsKeyboardFocused)
            NameBox.Text = snapshot.Name;
        var showingCode = snapshot.PairCode != null;
        CodePanel.Visibility = showingCode ? Visibility.Visible : Visibility.Collapsed;
        CreateCodeButton.Visibility = showingCode ? Visibility.Collapsed : Visibility.Visible;
        CodeText.Text = showingCode ? snapshot.PairCode![..3] + "  " + snapshot.PairCode[3..] : "";
        var paired = snapshot.PeerName != null;
        PairedCard.Visibility = paired ? Visibility.Visible : Visibility.Collapsed;
        ShareCard.Visibility = paired && !showingCode ? Visibility.Collapsed : Visibility.Visible;
        EnterCodeCard.Visibility = paired ? Visibility.Collapsed : Visibility.Visible;
        PeerText.Text = snapshot.PeerName == null ? "" : "Paired with " + snapshot.PeerName;
        PairErrorText.Text = snapshot.PairError ?? "";
        HotkeyNote.Text = snapshot.HotkeyNote;
        ApplyQuickRequest(snapshot);
        ApplyNearby(snapshot);
        PaintShare(ShareMouseButton, snapshot.ControlShare == ControlShare.Mouse);
        PaintShare(ShareKeyboardButton, snapshot.ControlShare == ControlShare.Keyboard);
        PaintShare(ShareBothButton, snapshot.ControlShare == ControlShare.Both);
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();
    }

    private void NameBox_LostFocus(object sender, RoutedEventArgs e) => _controller.SetName(NameBox.Text);

    private void ApplyQuickRequest(AppSnapshot snapshot)
    {
        if (string.IsNullOrEmpty(snapshot.QuickRequestName))
        {
            QuickRequestCard.Visibility = Visibility.Collapsed;
            return;
        }

        QuickRequestText.Text = snapshot.QuickRequestName + " wants to use this computer's keyboard and mouse.";
        QuickRequestCard.Visibility = Visibility.Visible;
    }

    private void ApplyNearby(AppSnapshot snapshot)
    {
        var show = snapshot.QuickRequestName == null;
        NearbyCard.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show)
        {
            NearbyPanel.Children.Clear();
            return;
        }

        NearbyPanel.Children.Clear();
        foreach (var computer in snapshot.NearbyComputers)
        {
            var button = new System.Windows.Controls.Button
            {
                Content = computer.Name,
                Style = (Style)FindResource("PrimaryButton"),
                Margin = new Thickness(0, 8, 0, 0),
                Tag = computer.Id
            };
            button.Click += NearbyComputer_Click;
            NearbyPanel.Children.Add(button);
        }

        if (_searching)
            NearbyHint.Text = "Searching this Wi-Fi…";
        else if (snapshot.NearbyComputers.Count > 0)
            NearbyHint.Text = "Tap a computer to connect. It will ask them to allow it.";
        else
            NearbyHint.Text = "No other computers found yet. Open Deskspan on the other PC on the same Wi-Fi, then tap Search.";
    }

    private void Search_Click(object sender, RoutedEventArgs e)
    {
        _controller.SearchNearby();
        _searching = true;
        NearbyHint.Text = "Searching this Wi-Fi…";
        _searchTimer ??= new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _searchTimer.Tick -= SearchDone;
        _searchTimer.Tick += SearchDone;
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    private void SearchDone(object? sender, EventArgs e)
    {
        _searchTimer?.Stop();
        _searching = false;
        Apply(_controller.Snapshot());
    }

    private async void NearbyComputer_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button button && button.Tag is Guid id)
        {
            button.IsEnabled = false;
            await _controller.ConnectNearbyAsync(id);
        }
    }

    private void AllowQuick_Click(object sender, RoutedEventArgs e) => _controller.RespondToQuickConnect(true);

    private void DenyQuick_Click(object sender, RoutedEventArgs e) => _controller.RespondToQuickConnect(false);

    private void ShareMouse_Click(object sender, RoutedEventArgs e) => _controller.SetControlShare(ControlShare.Mouse);

    private void ShareKeyboard_Click(object sender, RoutedEventArgs e) => _controller.SetControlShare(ControlShare.Keyboard);

    private void ShareBoth_Click(object sender, RoutedEventArgs e) => _controller.SetControlShare(ControlShare.Both);

    private static void PaintShare(System.Windows.Controls.Button button, bool selected)
    {
        button.Background = selected
            ? (System.Windows.Media.Brush)button.FindResource("Accent")
            : System.Windows.Media.Brushes.Transparent;
        button.Foreground = selected
            ? (System.Windows.Media.Brush)button.FindResource("AccentInk")
            : (System.Windows.Media.Brush)button.FindResource("Muted");
        button.FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal;
    }

    private void CreateCode_Click(object sender, RoutedEventArgs e) => _controller.CreatePairCode();

    private void CancelCode_Click(object sender, RoutedEventArgs e) => _controller.CancelPairCode();

    private async void Pair_Click(object sender, RoutedEventArgs e)
    {
        PairButton.IsEnabled = false;
        try
        {
            await _controller.PairWithCodeAsync(CodeBox.Text);
        }
        finally
        {
            PairButton.IsEnabled = true;
        }
    }

    private void Forget_Click(object sender, RoutedEventArgs e) => _controller.Forget();

    private void ProLink_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo(PairDirectory.ProPage) { UseShellExecute = true });

    private void ApplyRemoteClipboard(string text)
    {
        try
        {
            System.Windows.Clipboard.SetText(text);
        }
        catch (Exception ex)
        {
            ErrorLog.Write(ex);
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _clipboardHwnd = new WindowInteropHelper(this).Handle;
        if (AddClipboardFormatListener(_clipboardHwnd))
            HwndSource.FromHwnd(_clipboardHwnd)?.AddHook(ClipboardHook);
        try
        {
            var dark = 1;
            DwmSetWindowAttribute(_clipboardHwnd, 20, ref dark, sizeof(int));
        }
        catch (Exception)
        {
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_clipboardHwnd != IntPtr.Zero)
            RemoveClipboardFormatListener(_clipboardHwnd);
        base.OnClosed(e);
    }

    private IntPtr ClipboardHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmClipboardUpdate)
        {
            try
            {
                if (System.Windows.Clipboard.ContainsText())
                    _controller.NoteLocalClipboard(System.Windows.Clipboard.GetText());
            }
            catch (Exception)
            {
            }
        }

        return IntPtr.Zero;
    }

    private void CodeBox_PreviewTextInput(object sender, TextCompositionEventArgs e) =>
        e.Handled = e.Text.Any(ch => !char.IsDigit(ch));

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
}

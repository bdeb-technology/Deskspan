using System.Windows;
using System.Windows.Threading;

namespace Deskspan;

public partial class App : System.Windows.Application
{
    private Mutex? _mutex;
    private ShareController? _controller;
    private SettingsWindow? _settings;
    private IndicatorWindow? _indicator;
    private TrayIcon? _tray;

    private bool _ownsMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _mutex = new Mutex(true, @"Local\Deskspan.SingleInstance", out var created);
        _ownsMutex = created;
        if (!created)
        {
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnDispatcherException;
        _controller = new ShareController();
        _settings = new SettingsWindow(_controller);
        _indicator = new IndicatorWindow();
        _tray = new TrayIcon(_controller, _settings);
        _controller.Changed += Refresh;
        _controller.ShowRequested += () => Dispatcher.BeginInvoke(() => _settings.ShowFromTray());
        _controller.Start();
        Refresh();
        _settings.Show();
    }

    private void Refresh()
    {
        if (_controller == null)
            return;
        var snapshot = _controller.Snapshot();
        Dispatcher.BeginInvoke(() =>
        {
            _settings?.Apply(snapshot);
            _indicator?.Apply(snapshot);
            _tray?.Apply(snapshot);
        });
    }

    private void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        ErrorLog.Write(e.Exception);
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _indicator?.Close();
        _controller?.Dispose();
        if (_ownsMutex)
            _mutex?.ReleaseMutex();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}

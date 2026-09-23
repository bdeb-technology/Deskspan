using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using Deskspan.Input;
using Deskspan.Net;

namespace Deskspan;

public sealed class AppSnapshot
{
    public string Status { get; init; } = "";
    public string Detail { get; init; } = "";
    public string Hotkey { get; init; } = "";
    public string Name { get; init; } = "";
    public string? PairCode { get; init; }
    public string Addresses { get; init; } = "";
    public string? PeerName { get; init; }
    public string? PairError { get; init; }
    public bool Linked { get; init; }
    public ShareMode Mode { get; init; }
    public IReadOnlyList<DiscoveredPeer> Nearby { get; init; } = [];
    public string HotkeyNote { get; init; } = "";
    public string DeviceId { get; init; } = "";
    public ControlShare ControlShare { get; init; } = ControlShare.Both;
    public IReadOnlyList<NearbyComputer> NearbyComputers { get; init; } = [];
    public string? QuickRequestName { get; init; }
}

public sealed record NearbyComputer(Guid Id, string Name);

public sealed class ShareController : IDisposable
{
    public static readonly Edition Edition = Edition.Free;

    private readonly object _gate = new();
    private readonly SynchronizationContext _ui;
    private readonly DeviceIdentity _identity;
    private readonly InputHub _hub;
    private readonly NetworkNode _node;
    private readonly Dictionary<ushort, (ushort Scan, bool Extended)> _keys = new();
    private readonly HashSet<byte> _buttons = new();
    private Hotkey _hotkey;
    private ControlShare _share = ControlShare.Both;
    private (string Name, TaskCompletionSource<bool> Decision)? _pendingQuick;
    private StoredPeer? _peer;
    private ShareMode _mode = ShareMode.Idle;
    private string _detail = "Create a code, or enter one from the other computer.";
    private CancellationTokenSource? _publishCancel;
    private string? _publishedCode;
    private CancellationTokenSource? _relayCancel;
    private string? _relayCode;
    private string? _pairError;
    private string? _lastSentClipboard;
    private string? _lastAppliedClipboard;
    private int _cursorX;
    private int _cursorY;
    private int _sequence;
    private int _disposed;

    public ShareController()
    {
        _ui = SynchronizationContext.Current ?? new SynchronizationContext();
        var profile = ProfileStore.Load();
        _identity = profile.Identity;
        _share = profile.ControlShare;
        _peer = profile.Peer;
        _hotkey = Hotkey.Default;
        _hub = new InputHub { Hotkey = _hotkey };
        _node = new NetworkNode(_identity, Peer, Hello);
        _hub.HotkeyPressed += OnHotkey;
        _hub.MouseDelta += OnMouseDelta;
        _hub.PointerButton += OnPointerButton;
        _hub.WheelMoved += OnWheel;
        _hub.KeyReceived += OnKey;
        _node.Paired += OnPaired;
        _node.QuickConnectApprover = ApproveQuickAsync;
        var saved = Peer();
        if (saved != null && saved.Relay && saved.Dial == false && !string.IsNullOrWhiteSpace(saved.RelayCode))
            BeginRelay(saved.RelayCode);
        _node.MessageReceived += OnMessage;
        _node.MouseMoved += OnMouseMoved;
        _node.LinkLost += OnLinkLost;
        _node.Changed += OnNodeChanged;
        _node.PathChanged += path => ErrorLog.Note("path: " + path);
    }

    public event Action? Changed;
    public event Action? ShowRequested;

    public void Toggle() => OnHotkey();

    public void Start()
    {
        _hub.Start();
        if (!_hub.KeyboardHookInstalled && !_hub.SystemHotkeyInstalled)
            _detail = "The shortcut could not be installed. Restart Deskspan.";
        _node.Start();
        if (!string.IsNullOrWhiteSpace(_node.BindError))
            _detail = "Close any other Deskspan, then open this one again.";
        RaiseChanged();
    }

    public AppSnapshot Snapshot()
    {
        var nearby = _node.Nearby();
        var linked = _node.IsLinked;
        var code = _node.ActiveCode;
        lock (_gate)
        {
            var others = nearby
                .Where(peer => peer.Id != _identity.Id)
                .Select(peer => new NearbyComputer(peer.Id, string.IsNullOrWhiteSpace(peer.Name) ? "A computer" : peer.Name))
                .ToArray();
            return new AppSnapshot
            {
                Status = StatusText(linked, code),
                Detail = _detail,
                Hotkey = _hotkey.Format(),
                Name = _identity.Name,
                PairCode = code,
                Addresses = LocalAddresses(),
                PeerName = _peer?.Name,
                PairError = _pairError,
                Linked = linked,
                Mode = _mode,
                Nearby = nearby,
                HotkeyNote = HotkeyNote(),
                DeviceId = _identity.Id.ToString("D"),
                ControlShare = _share,
                NearbyComputers = others,
                QuickRequestName = _pendingQuick?.Name
            };
        }
    }

    public void SetName(string name)
    {
        lock (_gate)
        {
            _identity.Name = DeviceIdentity.Sanitize(name);
            Save();
        }

        RaiseChanged();
    }

    public void SearchNearby() => _node.SearchNearby();

    public async Task ConnectNearbyAsync(Guid peerId)
    {
        var peer = _node.Nearby().FirstOrDefault(item => item.Id == peerId);
        if (peer.Id == Guid.Empty || peer.TcpPort <= 0)
        {
            lock (_gate)
                _pairError = "That computer went away. Try again.";
            RaiseChanged();
            return;
        }

        lock (_gate)
        {
            _pairError = null;
            _detail = "Asking " + (string.IsNullOrWhiteSpace(peer.Name) ? "that computer" : peer.Name) + " to allow the connection…";
        }

        RaiseChanged();
        try
        {
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(65));
            var secrets = await _node.PairQuickAsync(new IPEndPoint(peer.Address, peer.TcpPort), limit.Token).ConfigureAwait(false);
            SavePeer(secrets, peer.Address.ToString(), true, peer.TcpPort);
            lock (_gate)
            {
                _pairError = null;
                _detail = "Press the shortcut to switch.";
            }
        }
        catch (InvalidOperationException)
        {
            lock (_gate)
            {
                _pairError = "That computer did not allow the connection.";
                _detail = "Create a code, or enter one from the other computer.";
            }
        }
        catch (Exception)
        {
            lock (_gate)
            {
                _pairError = "Could not reach that computer. Keep both apps open and try again.";
                _detail = "Create a code, or enter one from the other computer.";
            }
        }

        RaiseChanged();
    }

    public void RespondToQuickConnect(bool allow)
    {
        (string Name, TaskCompletionSource<bool> Decision)? pending;
        lock (_gate)
        {
            pending = _pendingQuick;
            _pendingQuick = null;
        }

        pending?.Decision.TrySetResult(allow);
        RaiseChanged();
    }

    private Task<bool> ApproveQuickAsync(QuickConnectRequest request)
    {
        var decision = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        (string Name, TaskCompletionSource<bool> Decision)? previous;
        lock (_gate)
        {
            previous = _pendingQuick;
            _pendingQuick = (string.IsNullOrWhiteSpace(request.PeerName) ? "A computer" : request.PeerName, decision);
        }

        previous?.Decision.TrySetResult(false);
        Post(() => ShowRequested?.Invoke());
        Post(RaiseChanged);
        return decision.Task;
    }

    public void SetControlShare(ControlShare share)
    {
        (ushort Key, ushort Scan, bool Extended)[] keys = [];
        byte[] buttons = [];
        var placePointer = false;
        lock (_gate)
        {
            if (_share == share)
                return;
            var previous = _share;
            _share = share;
            if (_mode == ShareMode.Controlling)
            {
                ApplyCapture();
                if (previous != ControlShare.Mouse && share == ControlShare.Mouse)
                {
                    keys = _keys.Select(pair => (pair.Key, pair.Value.Scan, pair.Value.Extended)).ToArray();
                    _keys.Clear();
                }

                if (previous != ControlShare.Keyboard && share == ControlShare.Keyboard)
                {
                    buttons = _buttons.ToArray();
                    _buttons.Clear();
                }

                placePointer = share != ControlShare.Keyboard && previous == ControlShare.Keyboard;
                _detail = ControllingDetail();
            }

            Save();
        }

        foreach (var key in keys)
            _ = _node.SendInputAsync(new NetMessage.KeyStroke(false, key.Key, key.Scan, key.Extended));
        foreach (var button in buttons)
            _ = _node.SendInputAsync(new NetMessage.PointerButton(button, false, 0, 0));
        if (placePointer)
            SendPointer();
        RaiseChanged();
    }

    public void CreatePairCode()
    {
        var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
        _node.SetPairCode(code);
        lock (_gate)
        {
            _pairError = null;
            _detail = "Type this code on the other computer.";
        }

        BeginPublish(code);
        BeginRelay(code);
        RaiseChanged();
    }

    public void CancelPairCode()
    {
        _node.SetPairCode(null);
        EndPublish();
        EndRelay();
        lock (_gate)
            _detail = "Create a code, or enter one from the other computer.";
        RaiseChanged();
    }

    public async Task PairWithCodeAsync(string code)
    {
        try
        {
            PairingHandshake.ParseCode(code);
            lock (_gate)
            {
                _pairError = null;
                _detail = "Connecting…";
            }

            RaiseChanged();
            var digits = new string(code.Where(char.IsDigit).ToArray());
            var lookup = PairDirectory.FindPairAsync(ServerUrl(), digits, CancellationToken.None);
            var until = Environment.TickCount64 + 2500;
            while (Environment.TickCount64 < until && _node.Nearby().Count == 0 && !lookup.IsCompleted)
                await Task.Delay(200).ConfigureAwait(false);

            var connected = false;
            var rejected = false;
            var unreachable = false;
            var tried = new HashSet<string>(StringComparer.Ordinal);
            foreach (var peer in _node.Nearby())
            {
                if (peer.Id == _identity.Id || peer.TcpPort <= 0)
                    continue;
                if (!tried.Add(peer.Address + ":" + peer.TcpPort))
                    continue;
                var outcome = await TryPairAsync(new IPEndPoint(peer.Address, peer.TcpPort), code).ConfigureAwait(false);
                if (outcome == PairTry.Connected)
                {
                    connected = true;
                    break;
                }

                if (outcome == PairTry.Rejected)
                    rejected = true;
            }

            if (!connected)
            {
                var relay = await TryRelayAsync(digits).ConfigureAwait(false);
                connected = relay == PairTry.Connected;
                rejected |= relay == PairTry.Rejected;
                unreachable = relay == PairTry.Unreachable;
            }

            if (!connected)
            {
                var address = await lookup.ConfigureAwait(false);
                if (IPAddress.TryParse(address, out var ip) && tried.Add(ip + ":" + Protocol.SessionPort))
                {
                    var outcome = await TryPairAsync(new IPEndPoint(ip, Protocol.SessionPort), code).ConfigureAwait(false);
                    connected = outcome == PairTry.Connected;
                    rejected |= outcome == PairTry.Rejected;
                }
            }

            lock (_gate)
            {
                if (connected)
                {
                    _pairError = null;
                    _detail = "Press the shortcut to switch.";
                }
                else
                {
                    _pairError = rejected
                        ? "That code was not accepted."
                        : unreachable
                            ? "Could not reach that computer. Keep both apps open and try again."
                            : "That code was not found. Create a code on the other computer and try again.";
                    _detail = "Create a code, or enter one from the other computer.";
                }
            }
        }
        catch (Exception ex)
        {
            lock (_gate)
                _pairError = ex is ArgumentException ? ex.Message : "That code was not found. Create a code on the other computer.";
        }

        RaiseChanged();
    }

    private void BeginPublish(string code)
    {
        CancellationToken token;
        lock (_gate)
        {
            _publishCancel?.Cancel();
            _publishCancel?.Dispose();
            _publishCancel = new CancellationTokenSource();
            _publishedCode = code;
            token = _publishCancel.Token;
        }

        _ = PublishLoopAsync(code, token);
    }

    private void EndPublish()
    {
        string? code;
        string server;
        lock (_gate)
        {
            code = _publishedCode;
            _publishedCode = null;
            server = PairDirectory.DefaultServer;
            _publishCancel?.Cancel();
            _publishCancel?.Dispose();
            _publishCancel = null;
        }

        if (code != null)
            _ = PairDirectory.WithdrawPairAsync(server, code);
    }

    private async Task PublishLoopAsync(string code, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                if (_node.ActiveCode != code)
                    break;
                await PairDirectory.PublishPairAsync(ServerUrl(), code, token).ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromSeconds(20), token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (ObjectDisposedException)
        {
            return;
        }
        catch (Exception ex)
        {
            ErrorLog.Write(ex);
        }

        if (!token.IsCancellationRequested && _node.ActiveCode != code)
            EndPublish();
    }

    private static string ServerUrl() => PairDirectory.DefaultServer;

    private void BeginRelay(string code)
    {
        CancellationToken token;
        lock (_gate)
        {
            _relayCancel?.Cancel();
            _relayCancel?.Dispose();
            _relayCancel = new CancellationTokenSource();
            _relayCode = code;
            token = _relayCancel.Token;
        }

        _ = RelayHostLoopAsync(code, token);
    }

    private void EndRelay()
    {
        lock (_gate)
        {
            _relayCode = null;
            _relayCancel?.Cancel();
            _relayCancel?.Dispose();
            _relayCancel = null;
        }
    }

    private async Task RelayHostLoopAsync(string code, CancellationToken token)
    {
        var host = RelayHost();
        while (!token.IsCancellationRequested)
        {
            try
            {
                lock (_gate)
                {
                    if (_relayCode != code)
                        break;
                }

                var client = await PairRelay.ConnectAsync(host, PairRelay.Port, true, code, token, TimeSpan.FromMinutes(3)).ConfigureAwait(false);
                _ = _node.ReceiveConnectionAsync(client, true);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (Exception ex)
            {
                ErrorLog.Write(ex);
                try
                {
                    await Task.Delay(800, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private string RelayHost()
    {
        var server = ServerUrl();
        return Uri.TryCreate(server, UriKind.Absolute, out var uri) && !string.IsNullOrWhiteSpace(uri.Host)
            ? uri.Host
            : "deskspan.bdebtech.in";
    }

    private async Task<PairTry> TryRelayAsync(string code)
    {
        try
        {
            var host = RelayHost();
            var client = await PairRelay.ConnectAsync(host, PairRelay.Port, false, code, CancellationToken.None, TimeSpan.FromSeconds(20)).ConfigureAwait(false);
            try
            {
                var secrets = await PairingHandshake.JoinAsync(client.GetStream(), code, _identity, CancellationToken.None).ConfigureAwait(false);
                SavePeer(secrets, host, true, Protocol.SessionPort, true, code, host);
                return PairTry.Connected;
            }
            finally
            {
                client.Dispose();
            }
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase))
        {
            return PairTry.Missing;
        }
        catch (InvalidOperationException)
        {
            return PairTry.Rejected;
        }
        catch (Exception)
        {
            return PairTry.Unreachable;
        }
    }

    private async Task<PairTry> TryPairAsync(IPEndPoint endpoint, string code)
    {
        try
        {
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            var secrets = await _node.PairAsync(endpoint, code, limit.Token).ConfigureAwait(false);
            SavePeer(secrets, endpoint.Address.ToString(), true, endpoint.Port);
            return PairTry.Connected;
        }
        catch (InvalidOperationException)
        {
            return PairTry.Rejected;
        }
        catch (Exception)
        {
            return PairTry.Unreachable;
        }
    }

    private enum PairTry
    {
        Connected,
        Rejected,
        Unreachable,
        Missing
    }

    public void Forget()
    {
        EndRelay();
        HeldInput lift;
        lock (_gate)
        {
            _peer = null;
            StopCapture();
            lift = TakeHeld();
            _mode = ShareMode.Idle;
            Save();
            _detail = "Create a code, or enter one from the other computer.";
        }

        Lift(lift);
        _node.ForgetLink();
        RaiseChanged();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        EndPublish();
        EndRelay();
        _hub.CaptureMouse = false;
        _hub.CaptureKeyboard = false;
        _hub.Dispose();
        _node.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private void OnHotkey()
    {
        var release = false;
        var lift = HeldInput.None;
        lock (_gate)
        {
            var next = ShareMachine.OnHotkey(_mode, _node.IsLinked);
            if (_mode == ShareMode.Idle && next == ShareMode.Idle)
            {
                _detail = "Pair with a code first.";
                Post(() => ShowRequested?.Invoke());
                Post(RaiseChanged);
                return;
            }

            release = _mode == ShareMode.Controlled && next == ShareMode.Idle;
            lift = Apply(next);
        }

        Lift(lift);
        if (release)
            _ = _node.SendAsync(new NetMessage.Release());
        Post(RaiseChanged);
    }

    private void OnMouseDelta(int dx, int dy)
    {
        ushort x;
        ushort y;
        uint sequence;
        lock (_gate)
        {
            if (_mode != ShareMode.Controlling || _share == ControlShare.Keyboard)
                return;
            var screen = RemoteScreen();
            _cursorX = Math.Clamp(_cursorX + dx, screen.X, screen.X + Math.Max(1, screen.Width) - 1);
            _cursorY = Math.Clamp(_cursorY + dy, screen.Y, screen.Y + Math.Max(1, screen.Height) - 1);
            x = screen.NormalizeX(_cursorX);
            y = screen.NormalizeY(_cursorY);
            sequence = (uint)Interlocked.Increment(ref _sequence);
        }

        _node.SendMouse(sequence, x, y);
    }

    private void OnPointerButton(byte button, bool down)
    {
        ushort x;
        ushort y;
        lock (_gate)
        {
            if (_mode != ShareMode.Controlling || _share == ControlShare.Keyboard)
                return;
            if (down)
                _buttons.Add(button);
            else
                _buttons.Remove(button);
            var screen = RemoteScreen();
            x = screen.NormalizeX(_cursorX);
            y = screen.NormalizeY(_cursorY);
        }

        _ = _node.SendInputAsync(new NetMessage.PointerButton(button, down, x, y));
    }

    private void OnWheel(int delta, bool horizontal)
    {
        ushort x;
        ushort y;
        lock (_gate)
        {
            if (_mode != ShareMode.Controlling || _share == ControlShare.Keyboard)
                return;
            var screen = RemoteScreen();
            x = screen.NormalizeX(_cursorX);
            y = screen.NormalizeY(_cursorY);
        }

        _ = _node.SendInputAsync(new NetMessage.Wheel((short)delta, horizontal, x, y));
    }

    private void OnKey(ushort virtualKey, ushort scan, bool down, bool extended)
    {
        lock (_gate)
        {
            if (_mode != ShareMode.Controlling || _share == ControlShare.Mouse)
                return;
            if (down)
                _keys[virtualKey] = (scan, extended);
            else
                _keys.Remove(virtualKey);
        }

        _ = _node.SendInputAsync(new NetMessage.KeyStroke(down, virtualKey, scan, extended));
    }

    private void OnMouseMoved(ushort x, ushort y)
    {
        lock (_gate)
        {
            if (_mode != ShareMode.Controlled)
                return;
        }

        InputInjector.MoveNormalized(x, y);
    }

    private void OnMessage(NetMessage message)
    {
        switch (message)
        {
            case NetMessage.ControlState control:
                HeldInput controlled;
                lock (_gate)
                    controlled = Apply(ShareMachine.OnPeerControl(_mode, control.Active));
                Lift(controlled);
                Post(RaiseChanged);
                break;
            case NetMessage.Release:
                HeldInput released;
                lock (_gate)
                    released = Apply(ShareMachine.OnReleaseRequest(_mode));
                Lift(released);
                Post(RaiseChanged);
                break;
            case NetMessage.KeyStroke key:
                lock (_gate)
                {
                    if (_mode != ShareMode.Controlled)
                        return;
                    if (key.Down)
                        _keys[key.VirtualKey] = (key.ScanCode, key.Extended);
                    else
                        _keys.Remove(key.VirtualKey);
                }

                InputInjector.Key(key.VirtualKey, key.ScanCode, key.Down, key.Extended);
                break;
            case NetMessage.PointerButton button:
                lock (_gate)
                {
                    if (_mode != ShareMode.Controlled)
                        return;
                    if (button.Down)
                        _buttons.Add(button.Button);
                    else
                        _buttons.Remove(button.Button);
                }

                InputInjector.MoveNormalized(button.X, button.Y);
                InputInjector.Button(button.Button, button.Down);
                break;
            case NetMessage.Wheel wheel:
                lock (_gate)
                {
                    if (_mode != ShareMode.Controlled)
                        return;
                }

                InputInjector.MoveNormalized(wheel.X, wheel.Y);
                InputInjector.Wheel(wheel.Delta, wheel.Horizontal);
                break;
            case NetMessage.ClipboardText clipboard:
                if (clipboard.Text.Length == 0)
                    break;
                lock (_gate)
                    _lastAppliedClipboard = clipboard.Text;
                Post(() => RemoteClipboard?.Invoke(clipboard.Text));
                break;
        }
    }

    private void OnNodeChanged()
    {
        lock (_gate)
        {
            if (_node.IsLinked && _mode == ShareMode.Idle && _detail == "Create a code, or enter one from the other computer.")
                _detail = "Press the shortcut to switch.";
        }

        Post(RaiseChanged);
    }

    private void OnLinkLost()
    {
        HeldInput lift;
        lock (_gate)
        {
            StopCapture();
            lift = TakeHeld();
            _mode = ShareMode.Idle;
            if (_peer != null)
                _detail = "The other computer disconnected.";
        }

        Lift(lift);
        Post(RaiseChanged);
    }

    private void OnPaired(PairedSecrets secrets, string address, bool fromRelay)
    {
        EndPublish();
        string? relayCode;
        string relayHost;
        lock (_gate)
        {
            relayCode = fromRelay ? _relayCode : null;
            relayHost = RelayHost();
        }

        if (!fromRelay)
            EndRelay();
        SavePeer(secrets, address, false, Protocol.SessionPort, fromRelay, relayCode, fromRelay ? relayHost : null);
        lock (_gate)
            _detail = "Press the shortcut to switch.";
        Post(RaiseChanged);
    }

    private HeldInput Apply(ShareMode next)
    {
        if (_mode == next)
            return HeldInput.None;
        var previous = _mode;
        if (previous == ShareMode.Controlling)
            StopCapture();
        var lift = previous == ShareMode.Controlled ? TakeHeld() : HeldInput.None;
        _mode = next;
        if (next == ShareMode.Controlling)
            StartCapture();
        _detail = next switch
        {
            ShareMode.Controlling => ControllingDetail(),
            ShareMode.Controlled => "Press the shortcut to take this computer back.",
            _ => _node.IsLinked ? "Press the shortcut to switch." : "Create a code, or enter one from the other computer."
        };
        return lift;
    }

    private void StartCapture()
    {
        var screen = RemoteScreen();
        _cursorX = screen.X + screen.Width / 2;
        _cursorY = screen.Y + screen.Height / 2;
        ApplyCapture();
        if (_share != ControlShare.Keyboard)
            SendPointer();
        _ = _node.SendInputAsync(new NetMessage.ControlState(true));
    }

    private void ApplyCapture()
    {
        _hub.CaptureMouse = _share != ControlShare.Keyboard;
        _hub.CaptureKeyboard = _share != ControlShare.Mouse;
    }

    private void SendPointer()
    {
        var screen = RemoteScreen();
        _node.SendMouse((uint)Interlocked.Increment(ref _sequence), screen.NormalizeX(_cursorX), screen.NormalizeY(_cursorY));
    }

    private string ControllingDetail() => _share switch
    {
        ControlShare.Mouse => "Only the mouse is shared. Press the shortcut again to come back.",
        ControlShare.Keyboard => "Only the keyboard is shared. Press the shortcut again to come back.",
        _ => "Keyboard and mouse are shared. Press the shortcut again to come back."
    };

    private void StopCapture()
    {
        _hub.CaptureMouse = false;
        _hub.CaptureKeyboard = false;
        var keys = _keys.ToArray();
        var buttons = _buttons.ToArray();
        _keys.Clear();
        _buttons.Clear();
        foreach (var key in keys)
            _ = _node.SendInputAsync(new NetMessage.KeyStroke(false, key.Key, key.Value.Scan, key.Value.Extended));
        foreach (var button in buttons)
            _ = _node.SendInputAsync(new NetMessage.PointerButton(button, false, 0, 0));
        _ = _node.SendInputAsync(new NetMessage.ControlState(false));
    }

    private HeldInput TakeHeld()
    {
        var held = new HeldInput(
            _keys.Select(pair => (pair.Key, pair.Value.Scan, pair.Value.Extended)).ToArray(),
            _buttons.ToArray());
        _keys.Clear();
        _buttons.Clear();
        return held;
    }

    private static void Lift(HeldInput held)
    {
        foreach (var key in held.Keys)
            InputInjector.Key(key.Key, key.Scan, false, key.Extended);
        foreach (var button in held.Buttons)
            InputInjector.Button(button, false);
    }

    private readonly record struct HeldInput((ushort Key, ushort Scan, bool Extended)[] Keys, byte[] Buttons)
    {
        public static HeldInput None { get; } = new([], []);
    }

    private VirtualScreen RemoteScreen()
    {
        var hello = _node.RemoteHello;
        if (hello == null || hello.VirtualWidth <= 1 || hello.VirtualHeight <= 1)
            return new VirtualScreen(0, 0, 1920, 1080);
        return new VirtualScreen(hello.VirtualX, hello.VirtualY, hello.VirtualWidth, hello.VirtualHeight);
    }

    private string StatusText(bool linked, string? code)
    {
        var name = string.IsNullOrWhiteSpace(_peer?.Name) ? "the other PC" : _peer.Name;
        if (_mode == ShareMode.Controlling)
            return "Controlling " + name;
        if (_mode == ShareMode.Controlled)
            return name + " is controlling this PC";
        if (linked)
            return "Connected to " + name;
        if (_peer != null)
            return "Connecting";
        if (code != null)
            return "Share this code";
        return "Ready to pair";
    }

    private void SavePeer(PairedSecrets secrets, string address, bool dial, int port, bool relay = false, string? relayCode = null, string? relayHost = null)
    {
        lock (_gate)
        {
            _peer = new StoredPeer
            {
                Id = secrets.PeerId,
                Name = secrets.PeerName,
                PublicKey = secrets.PeerPublicKey,
                Address = address,
                Port = port,
                Dial = dial,
                Relay = relay,
                RelayCode = relayCode,
                RelayHost = relayHost,
                RelayPort = relay ? PairRelay.Port : 0
            };
            Save();
        }
    }

    private StoredPeer? Peer()
    {
        lock (_gate)
            return _peer;
    }

    private NetMessage.Hello Hello()
    {
        var screen = VirtualScreen.FromSystem();
        return new NetMessage.Hello(_identity.Name, screen.X, screen.Y, screen.Width, screen.Height, _node.MousePort);
    }

    public event Action<string>? RemoteClipboard;

    public void NoteLocalClipboard(string? text)
    {
        if (string.IsNullOrEmpty(text) || !_node.IsLinked)
            return;
        text = NetMessage.LimitClipboard(text);
        lock (_gate)
        {
            if (text == _lastAppliedClipboard || text == _lastSentClipboard)
                return;
            _lastSentClipboard = text;
        }

        _ = _node.SendAsync(new NetMessage.ClipboardText(text));
    }

    private static string HotkeyNote() => "Press it to switch. Press it again to come back.";

    private void Save() => ProfileStore.Save(new UserProfile
    {
        Identity = _identity,
        Hotkey = Hotkey.Default,
        ControlShare = _share,
        Peer = _peer
    });

    private void Post(Action action) => _ui.Post(_ => action(), null);

    private void RaiseChanged() => Changed?.Invoke();

    private static string LocalAddresses()
    {
        var addresses = new List<string>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                continue;
            foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
            {
                if (unicast.Address.AddressFamily == AddressFamily.InterNetwork)
                    addresses.Add(unicast.Address.ToString());
            }
        }

        return addresses.Count == 0 ? "No IPv4 address" : string.Join(", ", addresses.Distinct());
    }
}

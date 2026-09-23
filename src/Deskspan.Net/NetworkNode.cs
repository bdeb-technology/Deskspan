using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Deskspan.Net;

public sealed class NetworkNode : IAsyncDisposable
{
    private readonly DeviceIdentity _self;
    private readonly Func<StoredPeer?> _peer;
    private readonly Func<NetMessage.Hello> _hello;
    private readonly CancellationTokenSource _cancel = new();
    private readonly object _gate = new();
    private readonly SemaphoreSlim _linkGate = new(1, 1);
    private readonly Dictionary<Guid, (DiscoveredPeer Peer, long Seen)> _seen = new();
    private TcpListener? _listener;
    private Socket? _discovery;
    private Socket? _mouse;
    private LiveLink? _link;
    private TcpClient? _sessionClient;
    private AeadBox? _mouseSeal;
    private AeadBox? _mouseOpen;
    private uint _latestMouseSequence;
    private IPEndPoint? _peerMouse;
    private byte[]? _rendezvousToken;
    private string? _rendezvousHost;
    private int _rendezvousPort;
    private IPEndPoint? _rendezvousServer;
    private IPEndPoint? _punchedPeer;
    private long _directHeardAt;
    private long _directConfirmedAt;
    private uint _inputId;
    private readonly InputSequencer _inputs;
    private readonly int _sessionPort;
    private readonly int _discoveryPort;
    private readonly int _mousePreferPort;
    private string? _activeCode;
    private long _codeExpiresAt;
    private long _lastChangeTick;

    public NetworkNode(DeviceIdentity self, Func<StoredPeer?> peer, Func<NetMessage.Hello> hello)
        : this(self, peer, hello, Protocol.SessionPort, Protocol.DiscoveryPort, Protocol.MousePort)
    {
    }

    public NetworkNode(DeviceIdentity self, Func<StoredPeer?> peer, Func<NetMessage.Hello> hello, int sessionPort, int discoveryPort, int mousePort)
    {
        _self = self;
        _peer = peer;
        _hello = hello;
        _sessionPort = sessionPort;
        _discoveryPort = discoveryPort;
        _mousePreferPort = mousePort;
        _inputs = new InputSequencer(Deliver);
    }

    public string? BindError { get; private set; }
    public int MousePort { get; private set; }
    public int SessionPort { get; private set; }
    public bool IsLinked { get; private set; }
    public NetMessage.Hello? RemoteHello { get; private set; }

    public Func<QuickConnectRequest, Task<bool>>? QuickConnectApprover { get; set; }

    public event Action? Changed;
    public event Action<PairedSecrets, string, bool>? Paired;
    public event Action<NetMessage>? MessageReceived;
    public event Action<ushort, ushort>? MouseMoved;
    public event Action? LinkLost;
    public event Action<string>? PathChanged;

    public string? ActiveCode
    {
        get
        {
            lock (_gate)
                return Environment.TickCount64 <= _codeExpiresAt ? _activeCode : null;
        }
    }

    public IReadOnlyList<DiscoveredPeer> Nearby()
    {
        lock (_gate)
            return _seen.Values.Select(item => item.Peer).ToArray();
    }

    public void Start()
    {
        try
        {
            _listener = new TcpListener(IPAddress.Any, _sessionPort);
            _listener.Start();
            if (_listener.LocalEndpoint is IPEndPoint session)
                SessionPort = session.Port;
            _ = Task.Run(AcceptLoopAsync);
        }
        catch (Exception ex)
        {
            BindError = "Could not listen on TCP port " + _sessionPort + ". " + ex.Message;
        }

        try
        {
            _discovery = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            _discovery.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _discovery.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Broadcast, true);
            _discovery.Bind(new IPEndPoint(IPAddress.Any, _discoveryPort));
            _ = Task.Run(DiscoverLoop);
        }
        catch (Exception ex)
        {
            BindError = (BindError == null ? "" : BindError + " ") + "Discovery is unavailable. " + ex.Message;
        }

        _mouse = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            _mouse.Bind(new IPEndPoint(IPAddress.Any, _mousePreferPort));
        }
        catch (SocketException)
        {
            _mouse.Bind(new IPEndPoint(IPAddress.Any, 0));
        }

        MousePort = ((IPEndPoint)_mouse.LocalEndPoint!).Port;
        _ = Task.Run(MouseLoop);
        _ = Task.Run(DialLoopAsync);
        _ = Task.Run(RendezvousLoopAsync);
        RaiseChanged(force: true);
    }

    public bool DirectMousePath
    {
        get
        {
            lock (_gate)
                return _peerMouse != null || Environment.TickCount64 - _directConfirmedAt < 3000;
        }
    }

    public void SetPairCode(string? code)
    {
        lock (_gate)
        {
            _activeCode = code;
            _codeExpiresAt = code == null ? 0 : Environment.TickCount64 + (long)TimeSpan.FromMinutes(3).TotalMilliseconds;
        }

        RaiseChanged(force: true);
    }

    public async Task<PairedSecrets> PairAsync(IPEndPoint endpoint, string code, CancellationToken cancellationToken)
    {
        using var client = new TcpClient { NoDelay = true };
        await client.ConnectAsync(endpoint.Address, endpoint.Port, cancellationToken).ConfigureAwait(false);
        return await PairingHandshake.JoinAsync(client.GetStream(), code, _self, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PairedSecrets> PairQuickAsync(IPEndPoint endpoint, CancellationToken cancellationToken)
    {
        using var client = new TcpClient { NoDelay = true };
        await client.ConnectAsync(endpoint.Address, endpoint.Port, cancellationToken).ConfigureAwait(false);
        return await PairingHandshake.JoinQuickAsync(client.GetStream(), _self, cancellationToken).ConfigureAwait(false);
    }

    public async Task SendAsync(NetMessage message)
    {
        LiveLink? link;
        lock (_gate)
            link = _link;
        if (link == null)
            return;
        try
        {
            await link.SendAsync(message, _cancel.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            _ = DropLinkAsync(link);
        }
    }

    public void SendMouse(uint sequence, ushort x, ushort y)
    {
        var direct = DirectMousePath;
        if (!direct)
            _ = SendAsync(new NetMessage.PointerMove(sequence, x, y));
        AeadBox? box;
        lock (_gate)
            box = _mouseSeal;
        if (box == null || _mouse == null)
            return;
        SendFast(MouseDatagram.Seal(box, _self.IdBytes, sequence, x, y), direct);
    }

    public Task SendInputAsync(NetMessage message)
    {
        AeadBox? box;
        uint id;
        lock (_gate)
        {
            if (_link == null)
                return Task.CompletedTask;
            box = _mouseSeal;
            id = ++_inputId;
        }

        var stamped = NetMessage.WithInputId(message, id) ?? message;
        if (box != null && _mouse != null)
            SendFast(MouseDatagram.SealInput(box, _self.IdBytes, stamped), DirectMousePath);
        return SendAsync(stamped);
    }

    private void SendFast(byte[] packet, bool direct)
    {
        IPEndPoint? endpoint;
        IPEndPoint? punched;
        IPEndPoint? server;
        byte[]? token;
        lock (_gate)
        {
            endpoint = _peerMouse;
            punched = _punchedPeer;
            server = _rendezvousServer;
            token = _rendezvousToken;
        }

        if (endpoint != null)
        {
            SendDatagram(packet, endpoint);
            return;
        }

        if (token == null)
            return;
        if (punched != null)
            SendDatagram(packet, punched);
        if (!direct && server != null)
            SendDatagram(UdpRendezvous.Forward(token, packet), server);
    }

    public string PathDescription
    {
        get
        {
            lock (_gate)
            {
                if (_link == null)
                    return "none";
                if (_peerMouse != null)
                    return "local network";
                if (_rendezvousToken == null)
                    return "tcp";
                if (Environment.TickCount64 - _directConfirmedAt < 3000)
                    return "internet direct";
                return _rendezvousServer != null ? "internet via server" : "internet tcp";
            }
        }
    }

    private void SendDatagram(byte[] packet, IPEndPoint target)
    {
        try
        {
            _mouse?.SendTo(packet, target);
        }
        catch (SocketException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private async Task RendezvousLoopAsync()
    {
        var lastPath = "";
        while (!_cancel.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(500, _cancel.Token).ConfigureAwait(false);
                var path = PathDescription;
                if (path != lastPath)
                {
                    lastPath = path;
                    PathChanged?.Invoke(path);
                }

                byte[]? token;
                string? host;
                int port;
                IPEndPoint? server;
                IPEndPoint? punched;
                bool hearing;
                lock (_gate)
                {
                    token = _rendezvousToken;
                    host = _rendezvousHost;
                    port = _rendezvousPort;
                    server = _rendezvousServer;
                    punched = _punchedPeer;
                    hearing = Environment.TickCount64 - _directHeardAt < 3000;
                }

                if (token == null || string.IsNullOrWhiteSpace(host) || port <= 0)
                    continue;
                if (server == null)
                {
                    server = await ResolveServerAsync(host, port).ConfigureAwait(false);
                    if (server == null)
                        continue;
                    lock (_gate)
                    {
                        if (ReferenceEquals(_rendezvousToken, token))
                            _rendezvousServer = server;
                    }
                }

                SendDatagram(UdpRendezvous.Register(token), server);
                if (punched != null)
                    SendDatagram(UdpRendezvous.Punch(token, hearing), punched);
            }
            catch (OperationCanceledException) when (_cancel.IsCancellationRequested)
            {
                break;
            }
            catch (Exception)
            {
            }
        }
    }

    private static async Task<IPEndPoint?> ResolveServerAsync(string host, int port)
    {
        if (IPAddress.TryParse(host, out var literal))
            return literal.AddressFamily == AddressFamily.InterNetwork ? new IPEndPoint(literal, port) : null;
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host).ConfigureAwait(false);
            var v4 = addresses.FirstOrDefault(item => item.AddressFamily == AddressFamily.InterNetwork);
            return v4 == null ? null : new IPEndPoint(v4, port);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public void ForgetLink() => _ = DropLinkAsync(null);

    private async Task AcceptLoopAsync()
    {
        while (!_cancel.IsCancellationRequested && _listener != null)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_cancel.Token).ConfigureAwait(false);
            }
            catch (Exception)
            {
                break;
            }

            client.NoDelay = true;
            _ = Task.Run(() => HandleClientAsync(client, false));
        }
    }

    public Task ReceiveConnectionAsync(TcpClient client, bool fromRelay) => HandleClientAsync(client, fromRelay);

    private async Task HandleClientAsync(TcpClient client, bool fromRelay)
    {
        var handedOff = false;
        try
        {
            var stream = client.GetStream();
            using var readLimit = CancellationTokenSource.CreateLinkedTokenSource(_cancel.Token);
            readLimit.CancelAfter(TimeSpan.FromSeconds(5));
            var magic = new byte[4];
            await PairingHandshake.ReadExactlyAsync(stream, magic, readLimit.Token).ConfigureAwait(false);
            var prefixed = new PrefixedStream(stream, magic);
            if (magic.AsSpan().SequenceEqual(Protocol.PairMagic))
            {
                var secrets = await PairingHandshake.TryHostAsync(prefixed, CodeIsValid, _self, _cancel.Token).ConfigureAwait(false);
                if (secrets != null)
                {
                    lock (_gate)
                        _activeCode = null;
                    var address = ((IPEndPoint)client.Client.RemoteEndPoint!).Address.ToString();
                    Paired?.Invoke(secrets.Value, address, fromRelay);
                    RaiseChanged(force: true);
                }

                client.Dispose();
                return;
            }

            if (magic.AsSpan().SequenceEqual(Protocol.QuickMagic))
            {
                var approver = QuickConnectApprover;
                if (approver == null)
                {
                    client.Dispose();
                    return;
                }

                var lengthBytes = new byte[2];
                await PairingHandshake.ReadExactlyAsync(stream, lengthBytes, readLimit.Token).ConfigureAwait(false);
                int length = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(lengthBytes);
                if (length < 16 + 32 + 1 || length > Protocol.MaxPairingFrame)
                {
                    client.Dispose();
                    return;
                }

                var body = new byte[length];
                await PairingHandshake.ReadExactlyAsync(stream, body, readLimit.Token).ConfigureAwait(false);
                using var decision = CancellationTokenSource.CreateLinkedTokenSource(_cancel.Token);
                decision.CancelAfter(TimeSpan.FromSeconds(60));
                var secrets = await PairingHandshake.TryHostQuickAsync(body, stream, approver, _self, decision.Token).ConfigureAwait(false);
                if (secrets != null)
                {
                    var address = ((IPEndPoint)client.Client.RemoteEndPoint!).Address.ToString();
                    Paired?.Invoke(secrets.Value, address, false);
                    RaiseChanged(force: true);
                }

                client.Dispose();
                return;
            }

            if (!magic.AsSpan().SequenceEqual(Protocol.SessionMagic))
            {
                client.Dispose();
                return;
            }

            var peerId = await SessionHeader.ReadAsync(prefixed, _cancel.Token).ConfigureAwait(false);
            var peer = _peer();
            if (peer == null || peer.Id != peerId)
            {
                client.Dispose();
                return;
            }

            var remote = ((IPEndPoint)client.Client.RemoteEndPoint!).Address;
            if (await AdoptLinkAsync(stream, remote, fromRelay).ConfigureAwait(false))
            {
                Hold(client);
                handedOff = true;
            }
            else
            {
                client.Dispose();
            }
        }
        catch (Exception)
        {
            if (!handedOff)
                client.Dispose();
        }
    }

    private async Task DialLoopAsync()
    {
        while (!_cancel.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(1000, _cancel.Token).ConfigureAwait(false);
                if (IsLinked)
                    continue;
                var peer = _peer();
                if (peer == null || peer.PublicKey.Length != 32)
                    continue;
                var dial = peer.Dial ?? DeviceIdentity.CompareIds(_self.IdBytes, peer.Id.ToByteArray()) < 0;
                if (!dial)
                    continue;

                DiscoveredPeer? nearby = null;
                lock (_gate)
                {
                    if (_seen.TryGetValue(peer.Id, out var seen) && seen.Peer.TcpPort > 0)
                        nearby = seen.Peer;
                }

                if (nearby is { } local && await DialDirectAsync(local.Address, local.TcpPort).ConfigureAwait(false))
                    continue;

                var relay = peer.Relay && PairRelay.TryCode(peer.RelayCode, out _);
                if (relay)
                {
                    await DialRelayAsync(peer).ConfigureAwait(false);
                    continue;
                }

                if (nearby == null && IPAddress.TryParse(peer.Address, out var address))
                    await DialDirectAsync(address, peer.Port > 0 ? peer.Port : Protocol.SessionPort).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_cancel.IsCancellationRequested)
            {
                break;
            }
            catch (Exception)
            {
            }
        }
    }

    private async Task<bool> DialDirectAsync(IPAddress address, int port)
    {
        var client = new TcpClient { NoDelay = true };
        var handedOff = false;
        try
        {
            using var connectLimit = CancellationTokenSource.CreateLinkedTokenSource(_cancel.Token);
            connectLimit.CancelAfter(TimeSpan.FromSeconds(4));
            await client.ConnectAsync(address, port, connectLimit.Token).ConfigureAwait(false);
            var stream = client.GetStream();
            await SessionHeader.WriteAsync(stream, _self.IdBytes, _cancel.Token).ConfigureAwait(false);
            if (await AdoptLinkAsync(stream, address, false).ConfigureAwait(false))
            {
                Hold(client);
                handedOff = true;
                return true;
            }

            return false;
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            if (!handedOff)
                client.Dispose();
        }
    }

    private async Task<bool> DialRelayAsync(StoredPeer peer)
    {
        var host = string.IsNullOrWhiteSpace(peer.RelayHost) ? "deskspan.bdebtech.in" : peer.RelayHost;
        var port = peer.RelayPort > 0 ? peer.RelayPort : PairRelay.Port;
        TcpClient client;
        try
        {
            client = await PairRelay.ConnectAsync(host, port, false, peer.RelayCode!, _cancel.Token, TimeSpan.FromSeconds(20)).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return false;
        }

        var handedOff = false;
        try
        {
            var stream = client.GetStream();
            await SessionHeader.WriteAsync(stream, _self.IdBytes, _cancel.Token).ConfigureAwait(false);
            if (await AdoptLinkAsync(stream, IPAddress.Loopback, true).ConfigureAwait(false))
            {
                Hold(client);
                handedOff = true;
                return true;
            }

            client.Dispose();
            return false;
        }
        catch (Exception)
        {
            if (!handedOff)
                client.Dispose();
            return false;
        }
    }

    private async Task<bool> AdoptLinkAsync(Stream stream, IPAddress remoteIp, bool viaRelay)
    {
        await _linkGate.WaitAsync(_cancel.Token).ConfigureAwait(false);
        try
        {
            if (IsLinked)
                return false;
            var peer = _peer();
            if (peer == null || peer.PublicKey.Length != 32)
                return false;
            var keys = _self.KeysWith(peer.PublicKey, peer.Id.ToByteArray());
            var adopted = await LiveLink.StartAsync(stream, keys.TcpKey, _hello(), null, _cancel.Token).ConfigureAwait(false);
            adopted.MessageReceived += OnMessage;
            adopted.Closed += () => _ = Task.Run(() => DropLinkAsync(adopted));
            lock (_gate)
            {
                _link = adopted;
                _mouseSeal?.Dispose();
                _mouseOpen?.Dispose();
                _mouseSeal = new AeadBox(keys.UdpKey, "DSUDP");
                _mouseOpen = new AeadBox(keys.UdpKey, "DSUDP");
                _latestMouseSequence = 0;
                _inputId = 0;
                _inputs.Reset();
                IsLinked = true;
                RemoteHello = adopted.RemoteHello;
                _punchedPeer = null;
                _rendezvousServer = null;
                _directHeardAt = 0;
                _directConfirmedAt = 0;
                if (viaRelay)
                {
                    _peerMouse = null;
                    _rendezvousToken = UdpRendezvous.Token(keys.UdpKey);
                    _rendezvousHost = string.IsNullOrWhiteSpace(peer.RelayHost) ? "deskspan.bdebtech.in" : peer.RelayHost;
                    _rendezvousPort = peer.RelayPort > 0 ? peer.RelayPort : PairRelay.Port;
                }
                else
                {
                    _rendezvousToken = null;
                    _rendezvousHost = null;
                    _rendezvousPort = 0;
                    if (RemoteHello != null)
                        _peerMouse = new IPEndPoint(remoteIp, RemoteHello.UdpPort);
                }
            }

            RaiseChanged(force: true);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            _linkGate.Release();
        }
    }

    private void Hold(TcpClient client)
    {
        TcpClient? previous;
        lock (_gate)
        {
            previous = _sessionClient;
            _sessionClient = client;
        }

        if (previous != null && !ReferenceEquals(previous, client))
            previous.Dispose();
    }

    private void PublishMouse(uint sequence, ushort x, ushort y)
    {
        lock (_gate)
        {
            if (!Sequence.IsNewer(sequence, _latestMouseSequence))
                return;
            _latestMouseSequence = sequence;
        }

        MouseMoved?.Invoke(x, y);
    }

    private void OnMessage(NetMessage message)
    {
        if (message is NetMessage.PointerMove move)
        {
            PublishMouse(move.Sequence, move.X, move.Y);
            return;
        }

        if (message is NetMessage.Hello hello)
        {
            lock (_gate)
            {
                RemoteHello = hello;
                if (_peerMouse != null)
                    _peerMouse = new IPEndPoint(_peerMouse.Address, hello.UdpPort);
            }

            RaiseChanged(force: true);
        }

        var id = NetMessage.InputId(message);
        if (id != 0)
        {
            _inputs.Offer(id, message);
            return;
        }

        Deliver(message);
    }

    private void Deliver(NetMessage message)
    {
        try
        {
            MessageReceived?.Invoke(message);
        }
        catch (Exception)
        {
        }
    }

    private async Task DropLinkAsync(LiveLink? expected)
    {
        LiveLink? link;
        TcpClient? client;
        lock (_gate)
        {
            if (expected != null && !ReferenceEquals(_link, expected))
                return;
            link = _link;
            client = _sessionClient;
            _link = null;
            _sessionClient = null;
            IsLinked = false;
            RemoteHello = null;
            _peerMouse = null;
            _rendezvousToken = null;
            _rendezvousHost = null;
            _rendezvousPort = 0;
            _rendezvousServer = null;
            _punchedPeer = null;
            _directHeardAt = 0;
            _directConfirmedAt = 0;
            _mouseSeal?.Dispose();
            _mouseOpen?.Dispose();
            _mouseSeal = null;
            _mouseOpen = null;
        }

        if (link != null)
            await link.DisposeAsync().ConfigureAwait(false);
        client?.Dispose();
        LinkLost?.Invoke();
        RaiseChanged(force: true);
    }

    private bool CodeIsValid(uint code)
    {
        lock (_gate)
        {
            if (_activeCode == null || Environment.TickCount64 > _codeExpiresAt)
                return false;
            return PairingHandshake.ParseCode(_activeCode) == code;
        }
    }

    private void DiscoverLoop()
    {
        if (_discovery == null)
            return;
        var buffer = new byte[512];
        while (!_cancel.IsCancellationRequested)
        {
            try
            {
                Broadcast();
                var until = Environment.TickCount64 + 1000;
                while (Environment.TickCount64 < until && !_cancel.IsCancellationRequested)
                {
                    if (!_discovery.Poll(200_000, SelectMode.SelectRead))
                        continue;
                    EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
                    int read = _discovery.ReceiveFrom(buffer, ref remote);
                    if (DiscoveryBeacon.IsProbe(buffer.AsSpan(0, read)))
                    {
                        Broadcast();
                        continue;
                    }

                    if (remote is not IPEndPoint ip || !DiscoveryBeacon.TryDecode(buffer.AsSpan(0, read), ip.Address, out var peer))
                        continue;
                    if (peer.Id == _self.Id)
                        continue;
                    lock (_gate)
                        _seen[peer.Id] = (peer, Environment.TickCount64);
                    RaiseChanged(force: false);
                }

                lock (_gate)
                {
                    var stale = _seen.Where(item => Environment.TickCount64 - item.Value.Seen > 5000).Select(item => item.Key).ToArray();
                    foreach (var id in stale)
                        _seen.Remove(id);
                }

                RaiseChanged(force: false);
            }
            catch (Exception) when (!_cancel.IsCancellationRequested)
            {
                Thread.Sleep(500);
            }
        }
    }

    public void SearchNearby()
    {
        SendToLocalNetworks(DiscoveryBeacon.EncodeProbe());
        Broadcast();
    }

    private void Broadcast()
    {
        if (_listener == null)
            return;
        SendToLocalNetworks(DiscoveryBeacon.Encode(_self.Id, _self.Name, SessionPort, MousePort));
    }

    private void SendToLocalNetworks(byte[] packet)
    {
        if (_discovery == null)
            return;
        var targets = new List<IPAddress> { IPAddress.Broadcast };
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                continue;
            foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
            {
                if (unicast.Address.AddressFamily != AddressFamily.InterNetwork || unicast.IPv4Mask == null)
                    continue;
                var address = unicast.Address.GetAddressBytes();
                var mask = unicast.IPv4Mask.GetAddressBytes();
                var broadcast = new byte[4];
                for (var i = 0; i < 4; i++)
                    broadcast[i] = (byte)(address[i] | ~mask[i]);
                targets.Add(new IPAddress(broadcast));
            }
        }

        foreach (var target in targets.Distinct())
        {
            try
            {
                _discovery.SendTo(packet, new IPEndPoint(target, Protocol.DiscoveryPort));
            }
            catch (SocketException)
            {
            }
        }
    }

    private void MouseLoop()
    {
        if (_mouse == null)
            return;
        var buffer = new byte[1024];
        while (!_cancel.IsCancellationRequested)
        {
            try
            {
                EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
                int read = _mouse.ReceiveFrom(buffer, ref remote);
                var packet = buffer.AsSpan(0, read);
                if (remote is IPEndPoint from && HandleRendezvous(packet, from))
                    continue;

                StoredPeer? peer = _peer();
                AeadBox? box;
                uint latest;
                lock (_gate)
                {
                    box = _mouseOpen;
                    latest = _latestMouseSequence;
                }

                if (peer == null || box == null)
                    continue;
                if (!MouseDatagram.TryOpenAny(box, packet, peer.Id.ToByteArray(), out var plain))
                    continue;
                if (remote is IPEndPoint sender)
                    NoteDirectMouse(sender);
                if (plain[0] == MouseDatagram.InputKind)
                {
                    OfferInput(plain.AsSpan(1));
                    continue;
                }

                if (plain.Length != 9 || plain[0] != MouseDatagram.MoveKind)
                    continue;
                var sequence = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(plain.AsSpan(1));
                if (!Sequence.IsNewer(sequence, latest))
                    continue;
                PublishMouse(
                    sequence,
                    System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(plain.AsSpan(5)),
                    System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(plain.AsSpan(7)));
            }
            catch (Exception) when (!_cancel.IsCancellationRequested)
            {
                Thread.Sleep(50);
            }
        }
    }

    private void OfferInput(ReadOnlySpan<byte> body)
    {
        NetMessage message;
        try
        {
            message = NetMessage.Decode(body);
        }
        catch (Exception)
        {
            return;
        }

        var id = NetMessage.InputId(message);
        if (id != 0)
            _inputs.Offer(id, message);
    }

    private bool HandleRendezvous(ReadOnlySpan<byte> packet, IPEndPoint from)
    {
        byte[]? token;
        IPEndPoint? server;
        lock (_gate)
        {
            token = _rendezvousToken;
            server = _rendezvousServer;
        }

        if (token == null)
            return false;
        if (UdpRendezvous.TryReadAnswer(packet, token, out var other) && other != null)
        {
            lock (_gate)
                _punchedPeer = other;
            SendDatagram(UdpRendezvous.Punch(token, false), other);
            return true;
        }

        if (!UdpRendezvous.TryReadPunch(packet, token, out var hearing))
            return false;
        var now = Environment.TickCount64;
        lock (_gate)
        {
            _directHeardAt = now;
            if (hearing)
                _directConfirmedAt = now;
            if (_punchedPeer == null && (server == null || !from.Equals(server)))
                _punchedPeer = from;
        }

        return true;
    }

    private void NoteDirectMouse(IPEndPoint sender)
    {
        IPEndPoint? server;
        byte[]? token;
        lock (_gate)
        {
            token = _rendezvousToken;
            server = _rendezvousServer;
        }

        if (token == null || (server != null && sender.Equals(server)))
            return;
        lock (_gate)
            _directHeardAt = Environment.TickCount64;
    }

    private void RaiseChanged(bool force)
    {
        var now = Environment.TickCount64;
        lock (_gate)
        {
            if (!force && now - _lastChangeTick < 400)
                return;
            _lastChangeTick = now;
        }

        Changed?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        _cancel.Cancel();
        try { _listener?.Stop(); } catch { }
        try { _discovery?.Dispose(); } catch { }
        try { _mouse?.Dispose(); } catch { }
        await DropLinkAsync(null).ConfigureAwait(false);
        _cancel.Dispose();
        _linkGate.Dispose();
    }
}

using System.Net;
using System.Net.Sockets;

namespace Deskspan.Net.Tests;

public class LinkTests
{
    [Fact]
    public async Task JoinerDialsTheAddressItCanReachAndControlsArrive()
    {
        var first = DeviceIdentity.Create("first");
        var second = DeviceIdentity.Create("second");
        var hostId = first;
        var joinId = second;
        if (DeviceIdentity.CompareIds(first.IdBytes, second.IdBytes) > 0)
        {
            hostId = second;
            joinId = first;
        }

        Assert.True(DeviceIdentity.CompareIds(joinId.IdBytes, hostId.IdBytes) > 0);

        StoredPeer? hostPeer = null;
        StoredPeer? joinPeer = null;
        NetworkNode host = null!;
        NetworkNode joiner = null!;
        host = new NetworkNode(hostId, () => hostPeer, () => Hello(host), 0, 0, 0);
        joiner = new NetworkNode(joinId, () => joinPeer, () => Hello(joiner), 0, 0, 0);
        var messages = new List<NetMessage>();
        var moves = new List<(ushort X, ushort Y)>();
        var returned = new List<string>();
        host.MessageReceived += message =>
        {
            lock (messages)
                messages.Add(message);
        };
        NetMessage[] Snapshot()
        {
            lock (messages)
                return messages.ToArray();
        }

        host.MouseMoved += (x, y) => moves.Add((x, y));
        joiner.MessageReceived += message =>
        {
            if (message is NetMessage.ClipboardText clipboard)
                returned.Add(clipboard.Text);
        };

        try
        {
            host.Start();
            joiner.Start();
            Assert.True(host.SessionPort > 0);
            host.SetPairCode("482913705");
            await joiner.PairAsync(new IPEndPoint(IPAddress.Loopback, host.SessionPort), "482913705", CancellationToken.None);
            hostPeer = Peer(joinId, "127.0.0.1", joiner.SessionPort, dial: false);
            joinPeer = Peer(hostId, "127.0.0.1", host.SessionPort, dial: true);

            var linked = await WaitAsync(() => host.IsLinked && joiner.IsLinked, TimeSpan.FromSeconds(8));
            Assert.True(linked);

            await joiner.SendInputAsync(new NetMessage.ControlState(true));
            await joiner.SendInputAsync(new NetMessage.KeyStroke(true, 0x41, 30, false));
            await joiner.SendInputAsync(new NetMessage.PointerButton(0, true, 10, 20));
            await joiner.SendInputAsync(new NetMessage.Wheel(-120, false, 10, 20));
            await joiner.SendAsync(new NetMessage.ClipboardText("from the first pc"));
            joiner.SendMouse(4, 640, 480);
            await host.SendAsync(new NetMessage.ClipboardText("from the second pc"));

            var got = await WaitAsync(() =>
                Snapshot().OfType<NetMessage.Wheel>().Any()
                && Snapshot().OfType<NetMessage.ClipboardText>().Any(item => item.Text == "from the first pc")
                && moves.Any(item => item.X == 640 && item.Y == 480)
                && returned.Contains("from the second pc"), TimeSpan.FromSeconds(3));
            Assert.True(got);
            await Task.Delay(300);
            var inputs = Snapshot().Where(item => NetMessage.InputId(item) != 0).ToArray();
            Assert.Collection(inputs,
                item => Assert.True(Assert.IsType<NetMessage.ControlState>(item).Active),
                item => Assert.IsType<NetMessage.KeyStroke>(item),
                item => Assert.IsType<NetMessage.PointerButton>(item),
                item => Assert.IsType<NetMessage.Wheel>(item));
        }
        finally
        {
            await host.DisposeAsync();
            await joiner.DisposeAsync();
        }
    }

    [Fact]
    public async Task NearbyComputerConnectsAfterTheOtherOneAllowsIt()
    {
        var hostId = DeviceIdentity.Create("host-quick");
        var joinId = DeviceIdentity.Create("join-quick");
        StoredPeer? hostPeer = null;
        StoredPeer? joinPeer = null;
        NetworkNode host = null!;
        NetworkNode joiner = null!;
        host = new NetworkNode(hostId, () => hostPeer, () => Hello(host), 0, 0, 0);
        joiner = new NetworkNode(joinId, () => joinPeer, () => Hello(joiner), 0, 0, 0);
        QuickConnectRequest? seen = null;
        host.QuickConnectApprover = request =>
        {
            seen = request;
            return Task.FromResult(true);
        };
        host.Paired += (secrets, address, _) =>
        {
            hostPeer = Peer(joinId, address, joiner.SessionPort, dial: false);
        };

        try
        {
            host.Start();
            joiner.Start();
            var secrets = await joiner.PairQuickAsync(new IPEndPoint(IPAddress.Loopback, host.SessionPort), null, CancellationToken.None);
            joinPeer = Peer(hostId, "127.0.0.1", host.SessionPort, dial: true);
            joinPeer = new StoredPeer
            {
                Id = secrets.PeerId,
                Name = secrets.PeerName,
                PublicKey = secrets.PeerPublicKey,
                Address = "127.0.0.1",
                Port = host.SessionPort,
                Dial = true
            };

            var linked = await WaitAsync(() => host.IsLinked && joiner.IsLinked, TimeSpan.FromSeconds(8));
            Assert.True(linked);
            Assert.NotNull(seen);
            Assert.Equal("host-quick", secrets.PeerName);
        }
        finally
        {
            await host.DisposeAsync();
            await joiner.DisposeAsync();
        }
    }

    [Fact]
    public async Task ADeniedNearbyRequestDoesNotConnect()
    {
        var hostId = DeviceIdentity.Create("host-deny");
        var joinId = DeviceIdentity.Create("join-deny");
        var host = new NetworkNode(hostId, () => null, () => Hello(null), 0, 0, 0);
        var joiner = new NetworkNode(joinId, () => null, () => Hello(null), 0, 0, 0);
        host.QuickConnectApprover = _ => Task.FromResult(false);
        try
        {
            host.Start();
            joiner.Start();
            await Assert.ThrowsAsync<PairingException>(() =>
                joiner.PairQuickAsync(new IPEndPoint(IPAddress.Loopback, host.SessionPort), null, CancellationToken.None));
            Assert.False(host.IsLinked);
        }
        finally
        {
            await host.DisposeAsync();
            await joiner.DisposeAsync();
        }
    }

    [Fact]
    public async Task OnlyTheNewestPointerMoveHasToArrive()
    {
        var hostId = DeviceIdentity.Create("host-move");
        var joinId = DeviceIdentity.Create("join-move");
        StoredPeer? hostPeer = null;
        StoredPeer? joinPeer = null;
        NetworkNode host = null!;
        NetworkNode joiner = null!;
        host = new NetworkNode(hostId, () => hostPeer, () => Hello(host), 0, 0, 0);
        joiner = new NetworkNode(joinId, () => joinPeer, () => Hello(joiner), 0, 0, 0);
        var moves = new List<(ushort X, ushort Y)>();
        host.MouseMoved += (x, y) => moves.Add((x, y));
        try
        {
            host.Start();
            joiner.Start();
            host.SetPairCode("246810357");
            await joiner.PairAsync(new IPEndPoint(IPAddress.Loopback, host.SessionPort), "246810357", CancellationToken.None);
            hostPeer = Peer(joinId, "127.0.0.1", joiner.SessionPort, dial: false);
            joinPeer = Peer(hostId, "127.0.0.1", host.SessionPort, dial: true);
            var linked = await WaitAsync(() => host.IsLinked && joiner.IsLinked, TimeSpan.FromSeconds(8));
            Assert.True(linked);

            for (uint i = 1; i <= 500; i++)
                joiner.SendMouse(i, (ushort)i, (ushort)(1000 - i));

            var got = await WaitAsync(() => moves.Any(item => item.X == 500 && item.Y == 500), TimeSpan.FromSeconds(3));
            Assert.True(got);
        }
        finally
        {
            await host.DisposeAsync();
            await joiner.DisposeAsync();
        }
    }

    [Fact]
    public async Task WrongCodeDoesNotOpenASession()
    {
        var hostId = DeviceIdentity.Create("host");
        var joinId = DeviceIdentity.Create("join");
        var host = new NetworkNode(hostId, () => null, () => Hello(null), 0, 0, 0);
        var joiner = new NetworkNode(joinId, () => null, () => Hello(null), 0, 0, 0);
        try
        {
            host.Start();
            joiner.Start();
            var failed = false;
            host.PairingFailed += () => failed = true;
            host.SetPairCode("111111111");
            await Assert.ThrowsAnyAsync<Exception>(() =>
                joiner.PairAsync(new IPEndPoint(IPAddress.Loopback, host.SessionPort), "111111222", CancellationToken.None));
            Assert.True(await WaitAsync(() => failed, TimeSpan.FromSeconds(3)));
            Assert.Null(host.ActiveCode);
            Assert.False(host.IsLinked);
        }
        finally
        {
            await host.DisposeAsync();
            await joiner.DisposeAsync();
        }
    }

    private static NetMessage.Hello Hello(NetworkNode? node) =>
        new(node == null ? "pc" : "pc", 0, 0, 1920, 1080, node?.MousePort ?? Protocol.MousePort);

    private static StoredPeer Peer(DeviceIdentity identity, string address, int port, bool dial) => new()
    {
        Id = identity.Id,
        Name = identity.Name,
        PublicKey = identity.PublicKey,
        Address = address,
        Port = port,
        Dial = dial
    };

    private static async Task<bool> WaitAsync(Func<bool> ready, TimeSpan timeout)
    {
        var until = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        while (Environment.TickCount64 < until)
        {
            if (ready())
                return true;
            await Task.Delay(50);
        }

        return ready();
    }
}

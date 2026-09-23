using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Deskspan.Net.Tests;

public class ProtocolTests
{
    [Fact]
    public void BothSidesDeriveTheSameSessionKeys()
    {
        var left = DeviceIdentity.Create("left");
        var right = DeviceIdentity.Create("right");
        var leftKeys = left.KeysWith(right.PublicKey, right.IdBytes);
        var rightKeys = right.KeysWith(left.PublicKey, left.IdBytes);
        Assert.Equal(leftKeys.TcpKey, rightKeys.TcpKey);
        Assert.Equal(leftKeys.UdpKey, rightKeys.UdpKey);
        Assert.NotEqual(leftKeys.TcpKey, leftKeys.UdpKey);
    }

    [Fact]
    public void AeadRoundTripRejectsTampering()
    {
        var key = new byte[32];
        RandomNumberGenerator.Fill(key);
        using var box = new AeadBox(key, "DSTCP");
        var packet = box.Seal("hello"u8);
        Assert.Equal("hello"u8.ToArray(), box.Open(packet));
        packet[20] ^= 0xFF;
        Assert.ThrowsAny<CryptographicException>(() => box.Open(packet));
    }

    [Theory]
    [InlineData(1u, 0u, true)]
    [InlineData(5u, 5u, false)]
    [InlineData(4u, 5u, false)]
    [InlineData(0u, uint.MaxValue, true)]
    public void SequenceNewer(uint incoming, uint latest, bool newer) =>
        Assert.Equal(newer, Sequence.IsNewer(incoming, latest));

    [Fact]
    public void InputArrivesOnceAndInOrderFromEitherPath()
    {
        var delivered = new List<uint>();
        var sequencer = new InputSequencer(message => delivered.Add(NetMessage.InputId(message)));
        NetMessage Key(uint id, bool down) => new NetMessage.KeyStroke(down, 0x41, 30, false, id);

        sequencer.Offer(1, Key(1, true));
        sequencer.Offer(1, Key(1, true));
        sequencer.Offer(3, Key(3, true));
        Assert.Equal([1u], delivered);
        sequencer.Offer(2, Key(2, false));
        sequencer.Offer(3, Key(3, true));
        sequencer.Offer(2, Key(2, false));
        Assert.Equal([1u, 2u, 3u], delivered);

        sequencer.Reset();
        sequencer.Offer(1, Key(1, true));
        Assert.Equal([1u, 2u, 3u, 1u], delivered);
    }

    [Fact]
    public void InputDatagramCarriesTheEvent()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        using var box = new AeadBox(key, "DSUDP");
        var sender = Guid.NewGuid().ToByteArray();
        var packet = MouseDatagram.SealInput(box, sender, new NetMessage.KeyStroke(false, 0x41, 30, false, 7));
        Assert.True(MouseDatagram.TryOpenAny(box, packet, sender, out var plain));
        Assert.Equal(MouseDatagram.InputKind, plain[0]);
        var decoded = Assert.IsType<NetMessage.KeyStroke>(NetMessage.Decode(plain.AsSpan(1)));
        Assert.Equal(7u, decoded.Id);
        Assert.False(decoded.Down);
        Assert.False(MouseDatagram.TryOpen(box, packet, sender, 0, out _, out _, out _));
    }

    [Fact]
    public void SearchProbeIsToldApartFromAnAnnounce()
    {
        var probe = DiscoveryBeacon.EncodeProbe();
        Assert.True(DiscoveryBeacon.IsProbe(probe));
        Assert.False(DiscoveryBeacon.TryDecode(probe, IPAddress.Loopback, out _));

        var announce = DiscoveryBeacon.Encode(Guid.NewGuid(), "a-pc", 47842, 47843);
        Assert.False(DiscoveryBeacon.IsProbe(announce));
        Assert.True(DiscoveryBeacon.TryDecode(announce, IPAddress.Loopback, out var peer));
        Assert.Equal("a-pc", peer.Name);
    }

    [Fact]
    public void DiscoveryBeaconRoundTrip()
    {
        var id = Guid.NewGuid();
        var packet = DiscoveryBeacon.Encode(id, "Office PC", 47842, 47843);
        Assert.True(DiscoveryBeacon.TryDecode(packet, IPAddress.Parse("192.168.1.20"), out var peer));
        Assert.Equal(id, peer.Id);
        Assert.Equal("Office PC", peer.Name);
        Assert.Equal(47842, peer.TcpPort);
        Assert.Equal(47843, peer.UdpPort);
        Assert.Equal("192.168.1.20", peer.Address.ToString());
    }

    [Fact]
    public void MessagesRoundTrip()
    {
        NetMessage[] messages =
        [
            new NetMessage.Hello("Desk", -1920, 0, 3840, 1080, 47843),
            new NetMessage.Heartbeat(),
            new NetMessage.KeyStroke(true, 0x41, 30, false),
            new NetMessage.PointerButton(1, true, 100, 200),
            new NetMessage.Wheel(-120, false, 10, 20),
            new NetMessage.ControlState(true),
            new NetMessage.Release(),
            new NetMessage.ClipboardText("Copied line\nsecond"),
            new NetMessage.PointerMove(7, 320, 240)
        ];
        foreach (var message in messages)
            Assert.Equal(message, NetMessage.Decode(message.Encode()));
    }

    [Fact]
    public void ClipboardTextIsCappedAndUnknownMessagesAreIgnored()
    {
        var limited = NetMessage.LimitClipboard(new string('你', 40_000));
        Assert.True(Encoding.UTF8.GetByteCount(limited) <= Protocol.MaxClipboardBytes);
        Assert.Equal(limited, ((NetMessage.ClipboardText)NetMessage.Decode(new NetMessage.ClipboardText(limited).Encode())).Text);
        Assert.IsType<NetMessage.Ignored>(NetMessage.Decode(new byte[] { 99, 1, 2 }));
        Assert.True(Protocol.MaxClipboardBytes + 64 < Protocol.MaxFrame);
    }

    [Fact]
    public void MouseDatagramDropsStalePositions()
    {
        var key = new byte[32];
        RandomNumberGenerator.Fill(key);
        var sender = Guid.NewGuid().ToByteArray();
        using var box = new AeadBox(key, "DSUDP");
        var first = MouseDatagram.Seal(box, sender, 1, 10, 20);
        var second = MouseDatagram.Seal(box, sender, 2, 11, 21);
        Assert.True(MouseDatagram.TryOpen(box, first, sender, 0, out var sequence, out var x, out var y));
        Assert.Equal(1u, sequence);
        Assert.Equal((ushort)10, x);
        Assert.Equal((ushort)20, y);
        Assert.False(MouseDatagram.TryOpen(box, first, sender, 1, out _, out _, out _));
        Assert.True(MouseDatagram.TryOpen(box, second, sender, 1, out sequence, out x, out y));
        Assert.Equal(2u, sequence);
        Assert.Equal((ushort)11, x);
        var stranger = Guid.NewGuid().ToByteArray();
        Assert.False(MouseDatagram.TryOpen(box, second, stranger, 1, out _, out _, out _));
        second[30] ^= 1;
        Assert.False(MouseDatagram.TryOpen(box, second, sender, 1, out _, out _, out _));
    }

    [Fact]
    public async Task LiveLinkDeliversKeysAndClosesWhenThePeerLeaves()
    {
        var key = new byte[32];
        RandomNumberGenerator.Fill(key);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var client = new TcpClient();
        var connect = client.ConnectAsync(IPAddress.Loopback, port);
        using var server = await listener.AcceptTcpClientAsync();
        await connect;
        client.NoDelay = true;
        server.NoDelay = true;
        var hello = new NetMessage.Hello("pc", 0, 0, 1920, 1080, 9);
        var leftTask = LiveLink.StartAsync(client.GetStream(), key, hello, TimeSpan.FromSeconds(5), CancellationToken.None);
        var rightTask = LiveLink.StartAsync(server.GetStream(), key, hello with { Name = "other" }, TimeSpan.FromSeconds(5), CancellationToken.None);
        var links = await Task.WhenAll(leftTask, rightTask);
        await using var left = links[0];
        await using var right = links[1];
        Assert.Equal("other", left.RemoteHello?.Name);
        var received = new TaskCompletionSource<NetMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        right.MessageReceived += message =>
        {
            if (message is NetMessage.KeyStroke)
                received.TrySetResult(message);
        };
        await left.SendAsync(new NetMessage.KeyStroke(true, 0x41, 30, false), CancellationToken.None);
        var message = await received.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var keyStroke = Assert.IsType<NetMessage.KeyStroke>(message);
        Assert.True(keyStroke.Down);
        Assert.Equal((ushort)0x41, keyStroke.VirtualKey);

        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        right.Closed += () => closed.TrySetResult();
        await left.DisposeAsync();
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(3));
        listener.Stop();
    }

    [Theory]
    [InlineData(ShareMode.Idle, false, ShareMode.Idle)]
    [InlineData(ShareMode.Idle, true, ShareMode.Controlling)]
    [InlineData(ShareMode.Controlling, true, ShareMode.Idle)]
    [InlineData(ShareMode.Controlled, true, ShareMode.Idle)]
    public void HotkeyTransitions(ShareMode mode, bool linked, ShareMode next) =>
        Assert.Equal(next, ShareMachine.OnHotkey(mode, linked));

    [Fact]
    public void LinkLossAndPeerControl()
    {
        Assert.Equal(ShareMode.Idle, ShareMachine.OnLinkLost(ShareMode.Controlling));
        Assert.Equal(ShareMode.Controlled, ShareMachine.OnPeerControl(ShareMode.Idle, true));
        Assert.Equal(ShareMode.Idle, ShareMachine.OnPeerControl(ShareMode.Controlled, false));
        Assert.Equal(ShareMode.Controlling, ShareMachine.OnPeerControl(ShareMode.Controlling, false));
        Assert.Equal(ShareMode.Idle, ShareMachine.OnReleaseRequest(ShareMode.Controlling));
        Assert.Equal(ShareMode.Controlled, ShareMachine.OnReleaseRequest(ShareMode.Controlled));
    }

    [Theory]
    [InlineData(false, false, false, HookAction.Pass)]
    [InlineData(true, false, false, HookAction.Swallow)]
    [InlineData(true, true, false, HookAction.Pass)]
    [InlineData(false, false, true, HookAction.SwallowAndToggle)]
    [InlineData(true, false, true, HookAction.SwallowAndToggle)]
    [InlineData(true, true, true, HookAction.Pass)]
    public void HookDoesNotSwallowLocalInputUnlessCapturing(bool capturing, bool ours, bool hotkey, HookAction action) =>
        Assert.Equal(action, HookPolicy.Decide(capturing, ours, hotkey));


}

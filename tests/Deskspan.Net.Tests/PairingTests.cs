using System.Net;
using System.Net.Sockets;
using System.Text;
using Org.BouncyCastle.Math;

namespace Deskspan.Net.Tests;

public class PairingTests
{
    [Theory]
    [InlineData("server", "client",
        "2ee57912099d31560b3a44b1184b9b4866e904c49d12ac5042c97dca461b1a5f",
        "43dd0fd7215bdcb482879fca3220c6a968e66d70b1356cac18bb26c84a78d729",
        "dcb60106f276b02606d8ef0a328c02e4b629f84f89786af5befb0bc75b6e66be",
        "04a56fa807caaa53a4d28dbb9853b9815c61a411118a6fe516a8798434751470f9010153ac33d0d5f2047ffdb1a3e42c9b4e6be662766e1eeb4116988ede5f912c",
        "0406557e482bd03097ad0cbaa5df82115460d951e3451962f1eaf4367a420676d09857ccbc522686c83d1852abfa8ed6e4a1155cf8f1543ceca528afb591a1e0b7",
        "0e0672dc86f8e45565d338b0540abe69",
        "58ad4aa88e0b60d5061eb6b5dd93e80d9c4f00d127c65b3b35b1b5281fee38f0",
        "d3e2e547f1ae04f2dbdbf0fc4b79f8ecff2dff314b5d32fe9fcef2fb26dc459b")]
    [InlineData("", "client",
        "0548d8729f730589e579b0475a582c1608138ddf7054b73b5381c7e883e2efae",
        "403abbe3b1b4b9ba17e3032849759d723939a27a27b9d921c500edde18ed654b",
        "903023b6598908936ea7c929bd761af6039577a9c3f9581064187c3049d87065",
        "04a897b769e681c62ac1c2357319a3d363f610839c4477720d24cbe32f5fd85f44fb92ba966578c1b712be6962498834078262caa5b441ecfa9d4a9485720e918a",
        "04e0f816fd1c35e22065d5556215c097e799390d16661c386e0ecc84593974a61b881a8c82327687d0501862970c64565560cb5671f696048050ca66ca5f8cc7fc",
        "642f05c473c2cd79909f9a841e2f30a7",
        "47d29e6666af1b7dd450d571233085d7a9866e4d49d2645e2df975489521232b",
        "3313c5cefc361d27fb16847a91c2a73b766ffa90a4839122a9b70a2f6bd1d6df")]
    public void Spake2MatchesRfc9382Vectors(string a, string b, string w, string x, string y, string pA, string pB, string ke, string confA, string confB)
    {
        var scalar = new BigInteger(w, 16);
        var left = new Spake2(true, scalar, new BigInteger(x, 16));
        var right = new Spake2(false, scalar, new BigInteger(y, 16));
        Assert.Equal(pA, Convert.ToHexString(left.Share).ToLowerInvariant());
        Assert.Equal(pB, Convert.ToHexString(right.Share).ToLowerInvariant());

        var idA = Encoding.ASCII.GetBytes(a);
        var idB = Encoding.ASCII.GetBytes(b);
        var leftKeys = left.Finish(right.Share, idA, idB);
        var rightKeys = right.Finish(left.Share, idA, idB);
        Assert.Equal(ke, Convert.ToHexString(leftKeys.SharedKey).ToLowerInvariant());
        Assert.Equal(leftKeys.SharedKey, rightKeys.SharedKey);
        Assert.Equal(confA, Convert.ToHexString(leftKeys.OwnConfirmation).ToLowerInvariant());
        Assert.Equal(confB, Convert.ToHexString(leftKeys.PeerConfirmation).ToLowerInvariant());
        Assert.Equal(leftKeys.OwnConfirmation, rightKeys.PeerConfirmation);
    }

    [Fact]
    public void Spake2RejectsPointsOffTheCurve()
    {
        var side = new Spake2(true, "123456789");
        var bad = new byte[65];
        bad[0] = 4;
        Assert.Throws<InvalidDataException>(() => side.Finish(bad, [], []));
        Assert.Throws<InvalidDataException>(() => side.Finish(new byte[10], [], []));
    }

    [Fact]
    public void CodesHaveNineDigitsAndSixRoutingDigits()
    {
        var code = PairingHandshake.NewCode();
        Assert.Equal(9, code.Length);
        Assert.All(code, ch => Assert.True(char.IsAsciiDigit(ch)));
        Assert.Equal("482913705", PairingHandshake.NormalizeCode("482 913 705"));
        Assert.Equal("482913", PairingHandshake.RoutingPart("482-913-705"));
        Assert.Equal("482 913 705", PairingHandshake.FormatCode("482913705"));
        Assert.Throws<ArgumentException>(() => PairingHandshake.NormalizeCode("482913"));
    }

    [Fact]
    public async Task PairingAgreesOnKeysAndIdentities()
    {
        var host = DeviceIdentity.Create("host-pc");
        var join = DeviceIdentity.Create("join-pc");
        var (joined, hosted) = await RunAsync(
            stream => PairingHandshake.JoinAsync(stream, "482 913 705", join, CancellationToken.None),
            stream => PairingHandshake.HostAsync(stream, "482913705", host, CancellationToken.None));
        Assert.Equal(hosted.Keys.TcpKey, joined.Keys.TcpKey);
        Assert.Equal(host.Id, joined.PeerId);
        Assert.Equal("host-pc", joined.PeerName);
        Assert.Equal(join.Id, hosted.PeerId);
        Assert.Equal(join.PublicKey, hosted.PeerPublicKey);
    }

    [Fact]
    public async Task WrongCodeFailsAndTheCodeCannotBeTriedAgain()
    {
        var host = DeviceIdentity.Create("host-pc");
        var join = DeviceIdentity.Create("join-pc");
        var active = "482913705";
        string? Claim() => Interlocked.Exchange(ref active, null!);

        var (wrong, missed) = await RunCatchingAsync(
            stream => PairingHandshake.JoinAsync(stream, "482913000", join, CancellationToken.None),
            stream => PairingHandshake.TryHostAsync(stream, Claim, host, CancellationToken.None));
        Assert.IsType<PairingException>(wrong);
        Assert.Null(await missed);

        var (right, refused) = await RunCatchingAsync(
            stream => PairingHandshake.JoinAsync(stream, "482913705", join, CancellationToken.None),
            stream => PairingHandshake.TryHostAsync(stream, Claim, host, CancellationToken.None));
        Assert.IsType<PairingException>(right);
        Assert.Contains("not active", right!.Message);
        Assert.Null(await refused);
    }

    [Fact]
    public async Task SwappedPublicKeyIsCaughtByBothSides()
    {
        var host = DeviceIdentity.Create("host-pc");
        var join = DeviceIdentity.Create("join-pc");
        var attacker = DeviceIdentity.Create("join-pc");
        using var hostListener = new TcpListener(IPAddress.Loopback, 0);
        using var proxyListener = new TcpListener(IPAddress.Loopback, 0);
        hostListener.Start();
        proxyListener.Start();

        var proxy = Task.Run(async () =>
        {
            using var inbound = await proxyListener.AcceptTcpClientAsync();
            using var outbound = new TcpClient();
            await outbound.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)hostListener.LocalEndpoint).Port);
            var first = new byte[4 + 2 + 16 + 32];
            await PairingHandshake.ReadExactlyAsync(inbound.GetStream(), first, CancellationToken.None);
            attacker.PublicKey.CopyTo(first, 4 + 2 + 16);
            await outbound.GetStream().WriteAsync(first);
            var up = inbound.GetStream().CopyToAsync(outbound.GetStream());
            var down = outbound.GetStream().CopyToAsync(inbound.GetStream());
            await Task.WhenAny(up, down);
        });

        var joinTask = Task.Run(async () =>
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)proxyListener.LocalEndpoint).Port);
            return await PairingHandshake.JoinAsync(client.GetStream(), "482913705", join, CancellationToken.None);
        });
        using var server = await hostListener.AcceptTcpClientAsync();
        var hostTask = PairingHandshake.HostAsync(server.GetStream(), "482913705", host, CancellationToken.None);

        await Assert.ThrowsAsync<PairingException>(() => joinTask);
        await Assert.ThrowsAnyAsync<Exception>(() => hostTask);
        server.Dispose();
        await proxy.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task QuickConnectShowsTheSameNumberOnBothPcs()
    {
        var host = DeviceIdentity.Create("host-pc");
        var join = DeviceIdentity.Create("join-pc");
        string? shownOnJoiner = null;
        string? shownOnHost = null;
        var (joined, hosted) = await RunAsync(
            stream => PairingHandshake.JoinQuickAsync(stream, join, code => shownOnJoiner = code, CancellationToken.None),
            async stream => (await PairingHandshake.TryHostQuickAsync(stream, request =>
            {
                shownOnHost = request.VerifyCode;
                Assert.Equal("join-pc", request.PeerName);
                return Task.FromResult(true);
            }, host, CancellationToken.None))!.Value);
        Assert.NotNull(shownOnJoiner);
        Assert.Matches("^[0-9]{6}$", shownOnJoiner);
        Assert.Equal(shownOnJoiner, shownOnHost);
        Assert.Equal(hosted.Keys.TcpKey, joined.Keys.TcpKey);
    }

    [Fact]
    public async Task QuickConnectRefusalReachesTheJoiner()
    {
        var host = DeviceIdentity.Create("host-pc");
        var join = DeviceIdentity.Create("join-pc");
        var (refused, hosted) = await RunCatchingAsync(
            stream => PairingHandshake.JoinQuickAsync(stream, join, null, CancellationToken.None),
            stream => PairingHandshake.TryHostQuickAsync(stream, _ => Task.FromResult(false), host, CancellationToken.None));
        Assert.IsType<PairingException>(refused);
        Assert.Null(await hosted);
    }

    private static async Task<(TJoin Join, THost Host)> RunAsync<TJoin, THost>(Func<Stream, Task<TJoin>> joiner, Func<Stream, Task<THost>> hoster)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var joinTask = Task.Run(async () =>
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port);
            return await joiner(client.GetStream());
        });
        using var server = await listener.AcceptTcpClientAsync();
        var hosted = await hoster(server.GetStream()).WaitAsync(TimeSpan.FromSeconds(10));
        var joined = await joinTask.WaitAsync(TimeSpan.FromSeconds(10));
        return (joined, hosted);
    }

    private static async Task<(Exception? JoinError, Task<THost> Host)> RunCatchingAsync<TJoin, THost>(Func<Stream, Task<TJoin>> joiner, Func<Stream, Task<THost>> hoster)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var joinTask = Task.Run(async () =>
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port);
            await joiner(client.GetStream());
        });
        using var server = await listener.AcceptTcpClientAsync();
        var hostTask = hoster(server.GetStream());
        await hostTask.WaitAsync(TimeSpan.FromSeconds(10));
        Exception? error = null;
        try
        {
            await joinTask.WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (Exception ex)
        {
            error = ex;
        }

        return (error, hostTask);
    }
}

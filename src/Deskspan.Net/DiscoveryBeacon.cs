using System.Buffers.Binary;
using System.Net;
using System.Text;

namespace Deskspan.Net;

public readonly record struct DiscoveredPeer(Guid Id, string Name, IPAddress Address, int TcpPort, int UdpPort);

public static class DiscoveryBeacon
{
    public static ReadOnlySpan<byte> ProbeMagic => "DSDR"u8;

    public static byte[] EncodeProbe()
    {
        var packet = new byte[5];
        ProbeMagic.CopyTo(packet);
        packet[4] = Protocol.Version;
        return packet;
    }

    public static bool IsProbe(ReadOnlySpan<byte> packet) =>
        packet.Length >= 5 && packet[..4].SequenceEqual(ProbeMagic) && packet[4] == Protocol.Version;

    public static byte[] Encode(Guid id, string name, int tcpPort, int udpPort)
    {
        var nameBytes = Encoding.UTF8.GetBytes(DeviceIdentity.Sanitize(name));
        var packet = new byte[4 + 1 + 16 + 2 + 2 + 1 + nameBytes.Length];
        Protocol.DiscoveryMagic.CopyTo(packet);
        packet[4] = Protocol.Version;
        id.ToByteArray().CopyTo(packet.AsSpan(5));
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(21), (ushort)tcpPort);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(23), (ushort)udpPort);
        packet[25] = (byte)nameBytes.Length;
        nameBytes.CopyTo(packet.AsSpan(26));
        return packet;
    }

    public static bool TryDecode(ReadOnlySpan<byte> packet, IPAddress address, out DiscoveredPeer peer)
    {
        peer = default;
        if (packet.Length < 26 || !packet[..4].SequenceEqual(Protocol.DiscoveryMagic) || packet[4] != Protocol.Version)
            return false;
        int nameLength = packet[25];
        if (nameLength > Protocol.MaxNameBytes || packet.Length < 26 + nameLength)
            return false;
        var id = new Guid(packet.Slice(5, 16));
        int tcp = BinaryPrimitives.ReadUInt16LittleEndian(packet[21..]);
        int udp = BinaryPrimitives.ReadUInt16LittleEndian(packet[23..]);
        var name = Encoding.UTF8.GetString(packet.Slice(26, nameLength));
        peer = new DiscoveredPeer(id, name, address, tcp, udp);
        return true;
    }
}

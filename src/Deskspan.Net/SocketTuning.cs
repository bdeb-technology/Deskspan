using System.Net.Sockets;

namespace Deskspan.Net;

// Keeps session TCP sockets alive across NAT timeouts and brief outages, and
// detects a truly dead peer quickly so we can reconnect instead of hanging.
public static class SocketTuning
{
    public static void Apply(Socket? socket)
    {
        if (socket == null)
            return;
        try { socket.NoDelay = true; } catch { }
        try { socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true); } catch { }
        try { socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 10); } catch { }
        try { socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 2); } catch { }
        try { socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, 4); } catch { }
    }

    public static void Apply(TcpClient? client)
    {
        if (client != null)
            Apply(client.Client);
    }
}

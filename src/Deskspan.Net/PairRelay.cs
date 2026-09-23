using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Deskspan.Net;

public static class PairRelay
{
    public const int Port = 47840;
    public static ReadOnlySpan<byte> Magic => "DSRL"u8;

    public static bool TryCode(string? code, out string digits)
    {
        digits = new string((code ?? "").Where(char.IsDigit).ToArray());
        return digits.Length == 6;
    }

    public static async Task<TcpClient> ConnectAsync(string host, int port, bool hosting, string code, CancellationToken cancellationToken, TimeSpan? wait = null)
    {
        if (!TryCode(code, out var digits))
            throw new ArgumentException("Enter the 6-digit pairing code.");
        var client = new TcpClient { NoDelay = true };
        try
        {
            using var connectLimit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connectLimit.CancelAfter(TimeSpan.FromSeconds(8));
            await client.ConnectAsync(host, port, connectLimit.Token).ConfigureAwait(false);
            var stream = client.GetStream();
            var header = new byte[12];
            Magic.CopyTo(header);
            header[4] = Protocol.Version;
            header[5] = hosting ? (byte)1 : (byte)2;
            Encoding.ASCII.GetBytes(digits).CopyTo(header.AsSpan(6));
            await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
            using var waitLimit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            waitLimit.CancelAfter(wait ?? (hosting ? TimeSpan.FromMinutes(3) : TimeSpan.FromSeconds(20)));
            var status = new byte[1];
            await PairingHandshake.ReadExactlyAsync(stream, status, waitLimit.Token).ConfigureAwait(false);
            if (status[0] != 0)
                throw new InvalidOperationException("That code was not found.");
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }
}

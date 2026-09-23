using System.Security.Cryptography;

namespace Deskspan.Net;

public sealed class AeadBox : IDisposable
{
    private readonly ChaCha20Poly1305 _aead;
    private readonly byte[] _associated;
    private readonly object _gate = new();

    public AeadBox(byte[] key, string associated)
    {
        _aead = new ChaCha20Poly1305(key);
        _associated = System.Text.Encoding.ASCII.GetBytes(associated);
    }

    public byte[] Seal(ReadOnlySpan<byte> plaintext)
    {
        var packet = new byte[12 + plaintext.Length + 16];
        var nonce = packet.AsSpan(0, 12);
        var cipher = packet.AsSpan(12, plaintext.Length);
        var tag = packet.AsSpan(12 + plaintext.Length, 16);
        RandomNumberGenerator.Fill(nonce);
        lock (_gate)
            _aead.Encrypt(nonce, plaintext, cipher, tag, _associated);
        return packet;
    }

    public byte[] Open(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < 12 + 16)
            throw new CryptographicException("Ciphertext is too short.");
        var plain = new byte[packet.Length - 28];
        lock (_gate)
            _aead.Decrypt(packet[..12], packet[12..^16], packet[^16..], plain, _associated);
        return plain;
    }

    public void Dispose() => _aead.Dispose();
}

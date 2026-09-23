using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Crypto.Agreement;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Security;

namespace Deskspan.Net;

public sealed class DeviceIdentity
{
    public DeviceIdentity(Guid id, string name, byte[] publicKey, byte[] privateKey)
    {
        if (publicKey.Length != 32 || privateKey.Length != 32)
            throw new ArgumentException("X25519 keys must be 32 bytes.");
        Id = id;
        Name = Sanitize(name);
        PublicKey = publicKey;
        PrivateKey = privateKey;
    }

    public Guid Id { get; }
    public string Name { get; set; }
    public byte[] PublicKey { get; }
    public byte[] PrivateKey { get; }
    public byte[] IdBytes => Id.ToByteArray();

    public static DeviceIdentity Create(string name)
    {
        var generator = new X25519KeyPairGenerator();
        generator.Init(new X25519KeyGenerationParameters(new SecureRandom()));
        var pair = generator.GenerateKeyPair();
        var publicKey = ((X25519PublicKeyParameters)pair.Public).GetEncoded();
        var privateKey = ((X25519PrivateKeyParameters)pair.Private).GetEncoded();
        return new DeviceIdentity(Guid.NewGuid(), name, publicKey, privateKey);
    }

    public SessionKeys KeysWith(byte[] peerPublic, byte[] peerId)
    {
        var agreement = new X25519Agreement();
        agreement.Init(new X25519PrivateKeyParameters(PrivateKey, 0));
        var shared = new byte[agreement.AgreementSize];
        agreement.CalculateAgreement(new X25519PublicKeyParameters(peerPublic, 0), shared, 0);
        return SessionKeys.Derive(shared, IdBytes, peerId);
    }

    public static string Sanitize(string name)
    {
        var trimmed = (name ?? "").Trim().Replace("\r", "").Replace("\n", "");
        if (trimmed.Length == 0)
            trimmed = "Windows PC";
        var bytes = Encoding.UTF8.GetBytes(trimmed);
        if (bytes.Length <= Protocol.MaxNameBytes)
            return trimmed;
        var cut = Protocol.MaxNameBytes;
        while (cut > 0 && (bytes[cut] & 0xC0) == 0x80)
            cut--;
        return Encoding.UTF8.GetString(bytes, 0, cut);
    }

    public static int CompareIds(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right) =>
        left.SequenceCompareTo(right);
}

public readonly record struct SessionKeys(byte[] TcpKey, byte[] UdpKey)
{
    public static SessionKeys Derive(byte[] sharedSecret, byte[] localId, byte[] peerId)
    {
        var tcp = HKDF.DeriveKey(HashAlgorithmName.SHA256, sharedSecret, 32, "Deskspan.v1"u8.ToArray(), Info(localId, peerId, "tcp"u8));
        var udp = HKDF.DeriveKey(HashAlgorithmName.SHA256, sharedSecret, 32, "Deskspan.v1"u8.ToArray(), Info(localId, peerId, "udp"u8));
        return new SessionKeys(tcp, udp);
    }

    private static byte[] Info(byte[] localId, byte[] peerId, ReadOnlySpan<byte> label)
    {
        var first = localId;
        var second = peerId;
        if (localId.AsSpan().SequenceCompareTo(peerId) > 0)
        {
            first = peerId;
            second = localId;
        }

        var info = new byte[first.Length + second.Length + label.Length];
        first.CopyTo(info, 0);
        second.CopyTo(info.AsSpan(first.Length));
        label.CopyTo(info.AsSpan(first.Length + second.Length));
        return info;
    }
}

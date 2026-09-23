using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Asn1.X9;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Math.EC;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.Utilities;
using ECPoint = Org.BouncyCastle.Math.EC.ECPoint;

namespace Deskspan.Net;

public readonly record struct Spake2Keys(byte[] SharedKey, byte[] OwnConfirmation, byte[] PeerConfirmation);

// SPAKE2-P256-SHA256-HKDF-HMAC from RFC 9382. A is the joining PC and B is the PC showing the code.
public sealed class Spake2
{
    private const int ScalarLength = 32;
    private static readonly X9ECParameters Group = ECNamedCurveTable.GetByName("P-256");
    private static readonly ECPoint M = Group.Curve.DecodePoint(Convert.FromHexString("02886e2f97ace46e55ba9dd7242579f2993b64e16ef3dcab95afd497333d8fa12f"));
    private static readonly ECPoint N = Group.Curve.DecodePoint(Convert.FromHexString("03d8bbd6c639c62937b04d997f38c3770719c629d7014d49a24b4f98baa1292b49"));

    private readonly bool _isA;
    private readonly BigInteger _w;
    private readonly BigInteger _secret;

    public Spake2(bool isA, string password)
        : this(isA, PasswordScalar(password), null)
    {
    }

    internal Spake2(bool isA, BigInteger w, BigInteger? secret)
    {
        _isA = isA;
        _w = w.Mod(Group.N);
        _secret = secret ?? BigIntegers.CreateRandomInRange(BigInteger.One, Group.N.Subtract(BigInteger.One), new SecureRandom());
        var blind = (_isA ? M : N).Multiply(_w);
        Share = Group.G.Multiply(_secret).Add(blind).Normalize().GetEncoded(false);
    }

    public byte[] Share { get; }

    public static int ShareLength => 65;

    public static BigInteger PasswordScalar(string password)
    {
        var input = Encoding.UTF8.GetBytes("Deskspan SPAKE2 w\0" + password);
        return new BigInteger(1, SHA512.HashData(input)).Mod(Group.N);
    }

    public Spake2Keys Finish(byte[] peerShare, byte[] identityA, byte[] identityB)
    {
        if (peerShare.Length != ShareLength)
            throw new InvalidDataException("The pairing share is invalid.");
        ECPoint peer;
        try
        {
            peer = Group.Curve.DecodePoint(peerShare);
        }
        catch (ArgumentException)
        {
            throw new InvalidDataException("The pairing share is invalid.");
        }

        if (peer.IsInfinity || !peer.IsValid())
            throw new InvalidDataException("The pairing share is invalid.");
        var unblind = (_isA ? N : M).Multiply(_w);
        var k = peer.Subtract(unblind).Multiply(_secret).Normalize();
        if (k.IsInfinity)
            throw new InvalidDataException("The pairing share is invalid.");

        var pA = _isA ? Share : peerShare;
        var pB = _isA ? peerShare : Share;
        var transcript = Transcript(identityA, identityB, pA, pB, k.GetEncoded(false), BigIntegers.AsUnsignedByteArray(ScalarLength, _w));
        var hash = SHA256.HashData(transcript);
        var ke = hash[..16];
        var ka = hash[16..];
        var confirmation = HKDF.DeriveKey(HashAlgorithmName.SHA256, ka, 32, null, "ConfirmationKeys"u8.ToArray());
        var confirmA = HMACSHA256.HashData(confirmation[..16], transcript);
        var confirmB = HMACSHA256.HashData(confirmation[16..], transcript);
        return new Spake2Keys(ke, _isA ? confirmA : confirmB, _isA ? confirmB : confirmA);
    }

    internal static byte[] Transcript(params byte[][] parts)
    {
        var length = parts.Sum(part => 8 + part.Length);
        var output = new byte[length];
        var offset = 0;
        foreach (var part in parts)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(output.AsSpan(offset), (ulong)part.Length);
            offset += 8;
            part.CopyTo(output, offset);
            offset += part.Length;
        }

        return output;
    }
}

using System.Security.Cryptography;

namespace Kiler.Crypto.Tests;

internal static class TestKeys
{
    /// <summary>A copy of the bytes of a key, to compare in a test.</summary>
    public static byte[] Copy(this SecretKey key) => key.Use(bytes => bytes.ToArray());

    /// <summary>
    /// Texts that are not the public key of P-256 in its one form, each with a signature of <paramref name="text"/> by that
    /// key, or by the key of P-256 that the others are made of: the update signature (M3.4) and the admin seal (M3.9).
    /// </summary>
    public static TheoryData<string, string> NotPublicKeys(byte[] text)
    {
        using var p256 = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var spki = p256.ExportSubjectPublicKeyInfo();
        var signature = Sign(p256, text);
        var good = Convert.ToBase64String(spki);
        Assert.True(EcdsaP256.Verify(good, text, signature), "The key the others are made of does not verify.");

        // A curve of 256 bits that is not P-256: its signature has 64 bytes too.
        using var brainpool = ECDsa.Create(ECCurve.NamedCurves.brainpoolP256r1);
        using var p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        using var rsa = RSA.Create(2048);
        var point = p256.ExportParameters(false).Q;

        // The OID of P-256 in the key, 1.2.840.10045.3.1.7, with its last arc made one that no curve has.
        byte[] p256Oid = [0x2A, 0x86, 0x48, 0xCE, 0x3D, 0x03, 0x01, 0x07];
        var oidAt = spki.AsSpan().IndexOf(p256Oid);
        Assert.True(oidAt > 0, "The key holds no OID of P-256.");
        var unknownCurve = (byte[])spki.Clone();
        unknownCurve[oidAt + p256Oid.Length - 1] = 0x7F;

        // The same point of P-256, with the parameters of the curve written out instead of its name.
        using var explicitCurve = ECDsa.Create(p256.ExportExplicitParameters(includePrivateParameters: false));

        return new TheoryData<string, string>
        {
            { "", signature },
            { "not base64!", signature },
            // Base64 that is not in its one form.
            { good[..8] + " " + good[8..], signature },
            // The key with a byte too few, or with a byte after it.
            { Convert.ToBase64String(spki.AsSpan(0, spki.Length - 1)), signature },
            { Convert.ToBase64String([.. spki, 0]), signature },
            // The point alone, without its SubjectPublicKeyInfo.
            { Convert.ToBase64String([0x04, .. point.X!, .. point.Y!]), signature },
            // A point that is not on the curve: the last byte of its y changed.
            { Convert.ToBase64String([.. spki.AsSpan(0, spki.Length - 1), (byte)(spki[^1] ^ 0x01)]), signature },
            { Convert.ToBase64String(unknownCurve), signature },
            { Convert.ToBase64String(explicitCurve.ExportSubjectPublicKeyInfo()), signature },
            { Convert.ToBase64String(brainpool.ExportSubjectPublicKeyInfo()), Sign(brainpool, text) },
            { Convert.ToBase64String(p384.ExportSubjectPublicKeyInfo()), Sign(p384, text) },
            { Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo()), Convert.ToBase64String(rsa.SignData(text, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)) },
        };
    }

    /// <summary>A public key of P-256 with texts that are not a signature of <paramref name="text"/> of 64 bytes in its one form.</summary>
    public static TheoryData<string, string> NotSignatures(byte[] text)
    {
        using var p256 = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKey = Convert.ToBase64String(p256.ExportSubjectPublicKeyInfo());
        var good = Sign(p256, text);
        var bytes = Convert.FromBase64String(good);
        Assert.True(EcdsaP256.Verify(publicKey, text, good), "The signature the others are made of does not verify.");

        return new TheoryData<string, string>
        {
            { publicKey, "" },
            { publicKey, "not base64!" },
            { publicKey, good[..8] + " " + good[8..] },
            { publicKey, Convert.ToBase64String(bytes.AsSpan(0, 63)) },
            { publicKey, Convert.ToBase64String([.. bytes, 0]) },
            // The same signature in the DER form of RFC 3279.
            { publicKey, Convert.ToBase64String(p256.SignData(text, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence)) },
        };
    }

    public static string Sign(ECDsa key, byte[] text) =>
        Convert.ToBase64String(key.SignData(text, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
}

/// <summary>The keys that an unlock gives, disposed at the end of a test's <c>using</c>.</summary>
internal sealed class KeySet(IReadOnlyDictionary<int, SecretKey> keys) : IDisposable
{
    public IReadOnlyDictionary<int, SecretKey> Keys => keys;

    /// <summary>The levels in order, to compare: they are not secret.</summary>
    public int[] Levels => [.. keys.Keys.Order()];

    public SecretKey this[int level] => keys[level];

    public void Dispose()
    {
        foreach (var key in keys.Values)
        {
            key.Dispose();
        }
    }
}

/// <summary>What an unlock of the admin key gives, disposed at the end of a test's <c>using</c>.</summary>
internal sealed class Unlocked(UnlockedKeys keys) : IDisposable
{
    public UnlockedKeys Keys => keys;

    /// <summary>The levels in order, to compare: they are not secret.</summary>
    public int[] Levels => [.. keys.Levels.Keys.Order()];

    public void Dispose()
    {
        foreach (var key in keys.Levels.Values)
        {
            key.Dispose();
        }

        keys.AdminKey?.Dispose();
    }
}

using System.Security.Cryptography;
using System.Text;

namespace Kiler.Crypto.Tests;

public class UpdateSignatureTests
{
    private static readonly ReleaseVersion Version = new(1, 0, 0);

    private static readonly byte[] Hash = SHA256.HashData("demo Kiler.exe"u8);

    [Fact]
    public void Verify_ASignatureOfTheVersionAndTheHash_IsTrue()
    {
        using var key = SigningKey.New();

        var signature = key.Sign(Version, Hash);

        Assert.True(UpdateSignature.Verify(key.PublicKey, Version, Hash, signature));
    }

    // An older signed exe must not pass under a higher number: the version is signed with the hash (decided on 2026-11-11).
    [Theory]
    [InlineData(1, 0, 1)]
    [InlineData(1, 1, 0)]
    [InlineData(2, 0, 0)]
    [InlineData(0, 9, 9)]
    public void Verify_AnotherVersion_IsFalse(int major, int minor, int patch)
    {
        using var key = SigningKey.New();

        var signature = key.Sign(Version, Hash);

        Assert.False(UpdateSignature.Verify(key.PublicKey, new ReleaseVersion(major, minor, patch), Hash, signature));
    }

    [Fact]
    public void Verify_AnotherHash_IsFalse()
    {
        using var key = SigningKey.New();
        var signature = key.Sign(Version, Hash);

        for (var i = 0; i < Hash.Length; i++)
        {
            var changed = (byte[])Hash.Clone();
            changed[i] ^= 0x01;

            Assert.False(UpdateSignature.Verify(key.PublicKey, Version, changed, signature), $"A change of byte {i} of the hash verifies.");
        }
    }

    [Fact]
    public void Verify_AChangedByteOfTheSignature_IsFalse()
    {
        using var key = SigningKey.New();
        var signature = Convert.FromBase64String(key.Sign(Version, Hash));

        for (var i = 0; i < signature.Length; i++)
        {
            var changed = (byte[])signature.Clone();
            changed[i] ^= 0x01;

            Assert.False(UpdateSignature.Verify(key.PublicKey, Version, Hash, Convert.ToBase64String(changed)), $"A change of byte {i} of the signature verifies.");
        }
    }

    [Fact]
    public void Verify_WithAnotherPublicKey_IsFalse()
    {
        using var key = SigningKey.New();
        using var another = SigningKey.New();

        var signature = key.Sign(Version, Hash);

        Assert.False(UpdateSignature.Verify(another.PublicKey, Version, Hash, signature));
    }

    [Fact]
    public void Sign_TwiceTheSameText_GivesTwoSignaturesThatBothVerify()
    {
        using var key = SigningKey.New();

        var first = key.Sign(Version, Hash);
        var second = key.Sign(Version, Hash);

        Assert.Equal(64, Convert.FromBase64String(first).Length);
        Assert.True(UpdateSignature.Verify(key.PublicKey, Version, Hash, first));
        Assert.True(UpdateSignature.Verify(key.PublicKey, Version, Hash, second));
    }

    // Each key is made from a key of the test, and its signature of the text is made with it, so each is refused for its
    // own reason: without the check, a signature that fits it would verify.
    [Theory]
    [MemberData(nameof(NotPublicKeys))]
    public void Verify_NotAPublicKeyOfP256InItsOneForm_IsFalse(string publicKey, string signature)
    {
        Assert.False(UpdateSignature.Verify(publicKey, Version, Hash, signature));
    }

    public static TheoryData<string, string> NotPublicKeys()
    {
        var text = UpdateSignature.SignedText(Version, Hash);
        using var p256 = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var spki = p256.ExportSubjectPublicKeyInfo();
        var signature = Sign(p256, text);
        var good = Convert.ToBase64String(spki);
        Assert.True(UpdateSignature.Verify(good, Version, Hash, signature), "The key the others are made of does not verify.");

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

    [Theory]
    [MemberData(nameof(NotSignatures))]
    public void Verify_NotASignatureOf64BytesInItsOneForm_IsFalse(string publicKey, string signature)
    {
        Assert.False(UpdateSignature.Verify(publicKey, Version, Hash, signature));
    }

    public static TheoryData<string, string> NotSignatures()
    {
        var text = UpdateSignature.SignedText(Version, Hash);
        using var p256 = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKey = Convert.ToBase64String(p256.ExportSubjectPublicKeyInfo());
        var good = Sign(p256, text);
        var bytes = Convert.FromBase64String(good);
        Assert.True(UpdateSignature.Verify(publicKey, Version, Hash, good), "The signature the others are made of does not verify.");

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

    [Theory]
    [InlineData(0)]
    [InlineData(31)]
    [InlineData(33)]
    [InlineData(64)]
    public void Verify_AHashOfAnotherLength_IsRefused(int length)
    {
        using var key = SigningKey.New();
        var signature = key.Sign(Version, Hash);

        Assert.Throws<ArgumentException>(() => UpdateSignature.Verify(key.PublicKey, Version, new byte[length], signature));
        Assert.Throws<ArgumentException>(() => key.Sign(Version, new byte[length]));
    }

    [Fact]
    public void Verify_NullKeyOrSignature_IsRefused()
    {
        using var key = SigningKey.New();
        var signature = key.Sign(Version, Hash);

        Assert.Throws<ArgumentNullException>(() => UpdateSignature.Verify(null!, Version, Hash, signature));
        Assert.Throws<ArgumentNullException>(() => UpdateSignature.Verify(key.PublicKey, Version, Hash, null!));
    }

    // The format of the signed text: the format byte, then the label, the version and the hash as lowercase hex, each as
    // UTF-8 with its length before it in 4 bytes, big-endian, as the associated data of an encrypted value.
    [Fact]
    public void SignedText_IsTheFormatTheLabelTheVersionAndTheHashEachWithItsLength()
    {
        var hash = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        byte[] expected =
        [
            1,
            0, 0, 0, 6, .. "update"u8,
            0, 0, 0, 6, .. "1.10.0"u8,
            0, 0, 0, 64, .. "000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f"u8,
        ];

        Assert.Equal(expected, UpdateSignature.SignedText(new ReleaseVersion(1, 10, 0), hash));
    }

    // RFC 6979, appendix A.2.5: ECDSA over P-256 with SHA-256, checked against the text of the RFC
    // (https://www.rfc-editor.org/rfc/rfc6979.txt). The signature of a message is r and s, 32 bytes each.
    private const string Ux = "60FED4BA255A9D31C961EB74C6356D68C049B8923B61FA6CE669622E60F29FB6";
    private const string Uy = "7903FE1008B8BC99A41AE9E95628BC64F2F1B20C2D7E9F5177A3C294D4462299";

    [Theory]
    [InlineData("sample", "EFD48B2AACB6A8FD1140DD9CD45E81D69D2C877B56AAF991C34D0EA84EAF3716", "F7CB1C942D657C41D436C7A1B6E29F65F3E900DBB9AFF4064DC4AB2F843ACDA8")]
    [InlineData("test", "F1ABB023518351CD71D881567B1EA663ED3EFCF6C5132B354F28D3B0B7D38367", "019F4113742A2B14BD25926B49C649155F267E60D3814B4C0CC84250E46F0083")]
    public void VerifyData_TheVectorsOfRfc6979_AreTrue(string message, string r, string s)
    {
        var publicKey = Rfc6979PublicKey();
        var signature = Convert.ToBase64String(Convert.FromHexString(r + s));

        Assert.True(UpdateSignature.VerifyData(publicKey, Encoding.ASCII.GetBytes(message), signature));
        Assert.False(UpdateSignature.VerifyData(publicKey, Encoding.ASCII.GetBytes(message + "."), signature));
    }

    // Written by the code of M3.4 and kept as it is: a version signed before stays signed when the code changes.
    [Fact]
    public void Verify_ASignatureWrittenEarlier_StillVerifies()
    {
        const string PublicKey = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEe1i5Nq9CQ898C4HDsiHtZT+meaVA12hjHrr2urCdo4xYx1xMulkBKjNaC+Fc0yp7Dg4VwW8Hs7x5f8nXwlVLYg==";
        const string Signature = "X/z4+BWvkwufwbhdyc86UeuVguSku9XSo9jpymDgJEUR4bVk/8kbF32vCDM2tyqWGLMauy4gJsPnNI1cdREuqw==";

        Assert.True(UpdateSignature.Verify(PublicKey, Version, Hash, Signature));
        Assert.False(UpdateSignature.Verify(PublicKey, new ReleaseVersion(1, 0, 1), Hash, Signature));
    }

    private static string Rfc6979PublicKey()
    {
        using var key = ECDsa.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = Convert.FromHexString(Ux), Y = Convert.FromHexString(Uy) },
        });
        return Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
    }

    private static string Sign(ECDsa key, byte[] text) =>
        Convert.ToBase64String(key.SignData(text, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
}

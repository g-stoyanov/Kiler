using System.Security.Cryptography;

namespace Kiler.Crypto;

/// <summary>
/// The signature of a release (M3 step 11, M3.4): ECDSA over P-256 with SHA-256, of the version and the SHA-256 of
/// <c>Kiler.exe</c> together, never of the exe alone (decided on 2026-11-11). The signed text is the format byte, then the
/// label "update", the text of the version and the hash as lowercase hex, each as UTF-8 with its length before it, as the
/// associated data of an encrypted value. The public key is the base64 of its SubjectPublicKeyInfo, and the signature the
/// base64 of its 64 bytes in the form of IEEE P1363 (r, then s). The hash is of the local file that will run, never of the
/// file on the share, which can change between the read and the start.
/// </summary>
public static class UpdateSignature
{
    /// <summary>The bytes of a SHA-256.</summary>
    public const int HashSize = SHA256.HashSizeInBytes;

    /// <summary>The first byte of the signed text: ECDSA over P-256 with SHA-256. A new algorithm gets a new format.</summary>
    internal const byte Format1 = 1;

    /// <summary>The bytes of a signature of P-256 in the form of IEEE P1363.</summary>
    internal const int SignatureSize = 64;

    private const DSASignatureFormat SignatureFormat = DSASignatureFormat.IeeeP1363FixedFieldConcatenation;

    /// <summary>
    /// Whether <paramref name="signature"/> is a signature of <paramref name="version"/> and <paramref name="sha256"/> by the
    /// key of <paramref name="publicKey"/>. A key that is not a public key of P-256, or a signature that is not 64 bytes,
    /// each in its one base64 form, gives false. A hash of another length is a bug of the caller and throws.
    /// </summary>
    public static bool Verify(string publicKey, ReleaseVersion version, ReadOnlySpan<byte> sha256, string signature)
    {
        ArgumentNullException.ThrowIfNull(publicKey);
        ArgumentNullException.ThrowIfNull(signature);
        return VerifyData(publicKey, SignedText(version, sha256), signature);
    }

    /// <summary>The text that a release signs.</summary>
    internal static byte[] SignedText(ReleaseVersion version, ReadOnlySpan<byte> sha256)
    {
        if (sha256.Length != HashSize)
        {
            throw new ArgumentException($"A SHA-256 has {HashSize} bytes.", nameof(sha256));
        }

        return EncryptedValue.AssociatedData(Format1, Purpose.Update, [version.ToString(), Convert.ToHexStringLower(sha256)]);
    }

    /// <summary>The signature of <paramref name="text"/>, which <see cref="SigningKey"/> makes.</summary>
    internal static string Sign(ECDsa key, byte[] text) => Convert.ToBase64String(key.SignData(text, HashAlgorithmName.SHA256, SignatureFormat));

    /// <summary>The check of a signature of any data: the test vectors sign other texts.</summary>
    internal static bool VerifyData(string publicKey, ReadOnlySpan<byte> data, string signature)
    {
        ArgumentNullException.ThrowIfNull(publicKey);
        ArgumentNullException.ThrowIfNull(signature);

        // VerifyData of .NET 10 gives false for a signature of another length too (M3.4, with the length check taken out);
        // the check keeps the format in the code, not in the platform.
        if (!CanonicalBase64.TryDecode(signature, out var signatureBytes) || signatureBytes.Length != SignatureSize)
        {
            return false;
        }

        using var key = ImportPublicKey(publicKey);
        return key is not null && key.VerifyData(data, signatureBytes, HashAlgorithmName.SHA256, SignatureFormat);
    }

    /// <summary>
    /// The key of the base64 of a SubjectPublicKeyInfo of P-256, in its one form and with no byte after it; anything else is
    /// null. The caller disposes it.
    /// </summary>
    internal static ECDsa? ImportPublicKey(string publicKey)
    {
        if (!CanonicalBase64.TryDecode(publicKey, out var bytes))
        {
            return null;
        }

        var key = ECDsa.Create();
        try
        {
            key.ImportSubjectPublicKeyInfo(bytes, out var read);
            if (read == bytes.Length && IsP256(key))
            {
                return key;
            }
        }
        catch (Exception exception) when (IsNotAKey(exception))
        {
        }

        key.Dispose();
        return null;
    }

    /// <summary>
    /// Whether an import failed because the bytes are not a key it takes. On Windows, an OID of no known curve and a point
    /// that is not on its curve throw <see cref="PlatformNotSupportedException"/>, not <see cref="CryptographicException"/>
    /// (M3.4, security review).
    /// </summary>
    internal static bool IsNotAKey(Exception exception) => exception is CryptographicException or PlatformNotSupportedException;

    /// <summary>Whether the key is of P-256: a curve of 256 bits, such as brainpoolP256r1, makes signatures of 64 bytes too.</summary>
    internal static bool IsP256(ECDsa key)
    {
        var curve = key.ExportParameters(includePrivateParameters: false).Curve;
        return curve.IsNamed && string.Equals(curve.Oid.Value, ECCurve.NamedCurves.nistP256.Oid.Value, StringComparison.Ordinal);
    }
}

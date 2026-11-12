using System.Security.Cryptography;

namespace Kiler.Crypto;

/// <summary>
/// ECDSA over P-256 with SHA-256, shared by the update signature (M3.4) and the admin seal (M3.9). A public key is the base64
/// of its SubjectPublicKeyInfo, and a signature the base64 of its 64 bytes in the form of IEEE P1363 (r, then s), each read
/// only in its one form. A private key is locked as its PKCS#8 in an encrypted value whose context is its public key, in a
/// pinned buffer that is zeroed after use.
/// </summary>
internal static class EcdsaP256
{
    /// <summary>The bytes of a signature of P-256 in the form of IEEE P1363.</summary>
    public const int SignatureSize = 64;

    private const DSASignatureFormat SignatureFormat = DSASignatureFormat.IeeeP1363FixedFieldConcatenation;

    /// <summary>A PKCS#8 of P-256 with its public key has about 140 bytes.</summary>
    private const int Pkcs8BufferSize = 512;

    /// <summary>The signature of <paramref name="text"/>.</summary>
    public static string Sign(ECDsa key, byte[] text) => Convert.ToBase64String(key.SignData(text, HashAlgorithmName.SHA256, SignatureFormat));

    /// <summary>
    /// Whether <paramref name="signature"/> is a signature of <paramref name="data"/> by the key of
    /// <paramref name="publicKey"/>. A key that is not a public key of P-256, or a signature that is not 64 bytes, each in its
    /// one base64 form, gives false.
    /// </summary>
    public static bool Verify(string publicKey, ReadOnlySpan<byte> data, string signature)
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
    public static ECDsa? ImportPublicKey(string publicKey)
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
    /// The PKCS#8 of <paramref name="key"/> as an encrypted value with <paramref name="secretKey"/>, the purpose and
    /// <paramref name="publicKey"/>, the key's own, as its context.
    /// </summary>
    public static string LockPrivateKey(ECDsa key, SecretKey secretKey, Purpose purpose, string publicKey)
    {
        var pkcs8 = GC.AllocateArray<byte>(Pkcs8BufferSize, pinned: true);
        try
        {
            if (!key.TryExportPkcs8PrivateKey(pkcs8, out var length))
            {
                throw new CryptographicException("The key cannot be exported.");
            }

            return EncryptedValue.Encrypt(secretKey, pkcs8.AsSpan(0, length), purpose, publicKey);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pkcs8);
        }
    }

    /// <summary>
    /// The key of a PKCS#8 locked by <see cref="LockPrivateKey"/> whose public key is <paramref name="publicKey"/>, which the
    /// caller checked is of P-256, and which the caller disposes. A value that does not decrypt with the key, the purpose and
    /// that public key as the context, or that holds anything but a whole PKCS#8 of that public key, is null.
    /// </summary>
    public static ECDsa? UnlockPrivateKey(SecretKey secretKey, string value, Purpose purpose, string publicKey)
    {
        if (!CanonicalBase64.TryDecode(value, out var locked) || locked.Length < EncryptedValue.MinSize)
        {
            return null;
        }

        var pkcs8 = GC.AllocateArray<byte>(locked.Length - EncryptedValue.MinSize, pinned: true);
        try
        {
            EncryptedValue.Decrypt(secretKey, value, pkcs8, purpose, publicKey);
            return ImportPrivateKey(pkcs8, publicKey);
        }
        catch (CryptographicException)
        {
            return null;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pkcs8);
        }
    }

    /// <summary>
    /// Whether an import failed because the bytes are not a key it takes. On Windows, an OID of no known curve and a point
    /// that is not on its curve throw <see cref="PlatformNotSupportedException"/>, not <see cref="CryptographicException"/>
    /// (M3.4, security review).
    /// </summary>
    public static bool IsNotAKey(Exception exception) => exception is CryptographicException or PlatformNotSupportedException;

    /// <summary>Whether the key is of P-256: a curve of 256 bits, such as brainpoolP256r1, makes signatures of 64 bytes too.</summary>
    public static bool IsP256(ECDsa key)
    {
        var curve = key.ExportParameters(includePrivateParameters: false).Curve;
        return curve.IsNamed && string.Equals(curve.Oid.Value, ECCurve.NamedCurves.nistP256.Oid.Value, StringComparison.Ordinal);
    }

    /// <summary>The key of a whole PKCS#8 whose public key is <paramref name="publicKey"/>; anything else is null.</summary>
    private static ECDsa? ImportPrivateKey(ReadOnlySpan<byte> pkcs8, string publicKey)
    {
        var key = ECDsa.Create();
        try
        {
            key.ImportPkcs8PrivateKey(pkcs8, out var read);
            if (read == pkcs8.Length && string.Equals(Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()), publicKey, StringComparison.Ordinal))
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
}

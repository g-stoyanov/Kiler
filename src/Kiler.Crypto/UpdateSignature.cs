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

    /// <summary>
    /// Whether <paramref name="signature"/> is a signature of <paramref name="version"/> and <paramref name="sha256"/> by the
    /// key of <paramref name="publicKey"/>. A key that is not a public key of P-256, or a signature that is not 64 bytes,
    /// each in its one base64 form, gives false. A hash of another length is a bug of the caller and throws.
    /// </summary>
    public static bool Verify(string publicKey, ReleaseVersion version, ReadOnlySpan<byte> sha256, string signature)
    {
        ArgumentNullException.ThrowIfNull(publicKey);
        ArgumentNullException.ThrowIfNull(signature);
        return EcdsaP256.Verify(publicKey, SignedText(version, sha256), signature);
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
}

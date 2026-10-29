using System.Security.Cryptography;

namespace Kiler.Crypto;

/// <summary>AES-256-GCM with a 12-byte nonce and a 16-byte tag: the algorithm of version 1 of an encrypted value (M3.1).</summary>
internal static class Aes256Gcm
{
    public const int NonceSize = 12;

    public const int TagSize = 16;

    /// <summary>
    /// An <see cref="AesGcm"/> of the key, which keeps a copy of it until it is disposed. Its decryption throws an
    /// <see cref="AuthenticationTagMismatchException"/> when the tag does not match, and then clears the plaintext.
    /// </summary>
    public static AesGcm Create(ReadOnlySpan<byte> key)
    {
        // AesGcm takes a key of 16 or 24 bytes too; only AES-256 is version 1.
        if (key.Length != SecretKey.Size)
        {
            throw new ArgumentException($"An AES-256 key has {SecretKey.Size} bytes.", nameof(key));
        }

        return new AesGcm(key, TagSize);
    }

    public static void Encrypt(ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> associatedData, Span<byte> ciphertext, Span<byte> tag)
    {
        using var aes = Create(key);
        aes.Encrypt(nonce, plaintext, ciphertext, tag, associatedData);
    }

    public static void Decrypt(ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> tag, ReadOnlySpan<byte> associatedData, Span<byte> plaintext)
    {
        using var aes = Create(key);
        aes.Decrypt(nonce, ciphertext, tag, plaintext, associatedData);
    }
}

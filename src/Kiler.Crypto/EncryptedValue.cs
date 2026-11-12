using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Kiler.Crypto;

/// <summary>
/// What an encrypted value or a signed text is for. Its label goes into the associated data or the signed text, so a value
/// is never read as another. The labels are part of the format, and one enum keeps them apart.
/// </summary>
internal enum Purpose
{
    /// <summary>A group of fields of a shared record; the context is the record id and the level.</summary>
    Group,

    /// <summary>The check value of a level; the context is the level.</summary>
    Check,

    /// <summary>A level key locked with a password or the recovery key; the context is the level.</summary>
    LevelKey,

    /// <summary>
    /// The signed text of a release (M3.4); the context is the version and the hash. Only <see cref="UpdateSignature"/> takes
    /// it: Kiler encrypts no value with it, and a tag of AES-GCM would be no signature.
    /// </summary>
    Update,

    /// <summary>The signing key of the releases locked with a password (M3.4); the context is its public key.</summary>
    SigningKey,

    /// <summary>
    /// The sealed text of a file (M3.9); the context is the label of its <see cref="SealPurpose"/>, then the text. Only
    /// <see cref="AdminSeal"/> takes it: Kiler encrypts no value with it.
    /// </summary>
    Seal,

    /// <summary>The public key of the admin key locked with a password or the recovery key (M3.9); there is no context.</summary>
    AdminPublicKey,

    /// <summary>The private key of the admin key locked with a password or the recovery key (M3.9); the context is its public key.</summary>
    AdminPrivateKey,
}

/// <summary>
/// The format of an encrypted value (M3.1, decided at the start of M3): base64 of a version byte, the nonce, the ciphertext
/// and the tag. Version 1 is AES-256-GCM. The associated data is the version byte, then the label of the purpose and each
/// part of the context, each as UTF-8 with its length before it in 4 bytes, big-endian, so the same characters split
/// otherwise are another context. A value that is damaged, cut short, of an unknown version, or read with another key,
/// purpose or context is a <see cref="CryptographicException"/> with one message, and gives no partial result.
/// </summary>
internal static class EncryptedValue
{
    public const byte Version1 = 1;

    /// <summary>The bytes of a value of version 1 that encrypts nothing: the version, the nonce and the tag.</summary>
    public const int MinSize = HeaderSize + Aes256Gcm.TagSize;

    private const int HeaderSize = 1 + Aes256Gcm.NonceSize;

    // A lone surrogate in a part of the context throws, instead of becoming U+FFFD, which two parts could share.
    private static readonly UTF8Encoding Strict = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static string Encrypt(SecretKey key, ReadOnlySpan<byte> plaintext, Purpose purpose, params ReadOnlySpan<string> context)
    {
        ArgumentNullException.ThrowIfNull(key);
        using var aes = key.Use(Aes256Gcm.Create);
        var associatedData = AssociatedData(Version1, purpose, context);

        var value = new byte[MinSize + plaintext.Length];
        value[0] = Version1;
        var nonce = value.AsSpan(1, Aes256Gcm.NonceSize);
        RandomNumberGenerator.Fill(nonce);
        aes.Encrypt(nonce, plaintext, value.AsSpan(HeaderSize, plaintext.Length), value.AsSpan(HeaderSize + plaintext.Length), associatedData);
        return Convert.ToBase64String(value);
    }

    public static byte[] Decrypt(SecretKey key, string value, Purpose purpose, params ReadOnlySpan<string> context)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);
        using var aes = key.Use(Aes256Gcm.Create);

        var bytes = Read(value);
        var plaintext = new byte[bytes.Length - MinSize];
        Decrypt(aes, bytes, plaintext, purpose, context);
        return plaintext;
    }

    /// <summary>
    /// Decrypts a value whose plaintext has the length of <paramref name="plaintext"/> straight into it, so a secret such as a
    /// level key goes into a pinned buffer of the caller, never into an array of its own (M3.3). A value of another length
    /// fails as a damaged one does, and a failure leaves <paramref name="plaintext"/> zeroed.
    /// </summary>
    public static void Decrypt(SecretKey key, string value, Span<byte> plaintext, Purpose purpose, params ReadOnlySpan<string> context)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);
        using var aes = key.Use(Aes256Gcm.Create);

        var bytes = Read(value);
        if (bytes.Length - MinSize != plaintext.Length)
        {
            CryptographicOperations.ZeroMemory(plaintext);
            throw Unreadable();
        }

        Decrypt(aes, bytes, plaintext, purpose, context);
    }

    private static void Decrypt(AesGcm aes, byte[] bytes, Span<byte> plaintext, Purpose purpose, ReadOnlySpan<string> context)
    {
        var associatedData = AssociatedData(bytes[0], purpose, context);
        try
        {
            aes.Decrypt(bytes.AsSpan(1, Aes256Gcm.NonceSize), bytes.AsSpan(HeaderSize, plaintext.Length), bytes.AsSpan(HeaderSize + plaintext.Length), plaintext, associatedData);
        }
        catch (CryptographicException)
        {
            // AesGcm clears it on a tag that does not match; this covers any other failure of the decryption.
            CryptographicOperations.ZeroMemory(plaintext);
            throw Unreadable();
        }
    }

    /// <summary>The bytes of a value of a version that can be read, at least as long as one that encrypts nothing.</summary>
    private static byte[] Read(string value)
    {
        var bytes = FromBase64(value);
        return bytes.Length >= MinSize && bytes[0] == Version1 ? bytes : throw Unreadable();
    }

    private static byte[] FromBase64(string value) => CanonicalBase64.TryDecode(value, out var bytes) ? bytes : throw Unreadable();

    /// <summary>
    /// The version byte, then the label of <paramref name="purpose"/> and each part of <paramref name="context"/> as strict
    /// UTF-8 with its length before it. It is also the signed text of a release, with the format of the signature as the
    /// version (<see cref="UpdateSignature"/>, M3.4).
    /// </summary>
    internal static byte[] AssociatedData(byte version, Purpose purpose, ReadOnlySpan<string> context)
    {
        var label = Label(purpose);
        var size = 1 + sizeof(int) + Strict.GetByteCount(label);
        foreach (var part in context)
        {
            ArgumentNullException.ThrowIfNull(part, nameof(context));
            size += sizeof(int) + Strict.GetByteCount(part);
        }

        var data = new byte[size];
        data[0] = version;
        var at = Append(data, 1, label);
        foreach (var part in context)
        {
            at = Append(data, at, part);
        }

        return data;
    }

    private static int Append(byte[] data, int at, string part)
    {
        var length = Strict.GetBytes(part, data.AsSpan(at + sizeof(int)));
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(at), length);
        return at + sizeof(int) + length;
    }

    private static string Label(Purpose purpose) => purpose switch
    {
        Purpose.Group => "group",
        Purpose.Check => "check",
        Purpose.LevelKey => "level-key",
        Purpose.Update => "update",
        Purpose.SigningKey => "signing-key",
        Purpose.Seal => "seal",
        Purpose.AdminPublicKey => "admin-public-key",
        Purpose.AdminPrivateKey => "admin-private-key",
        _ => throw new ArgumentOutOfRangeException(nameof(purpose)),
    };

    private static CryptographicException Unreadable() => new("The encrypted value cannot be decrypted.");
}

using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Kiler.Crypto;

/// <summary>What an encrypted value is for. Its label goes into the associated data, so a value is never read as another.</summary>
internal enum Purpose
{
    /// <summary>A group of fields of a shared record; the context is the record id and the level.</summary>
    Group,

    /// <summary>The check value of a level; the context is the level.</summary>
    Check,

    /// <summary>A level key locked with a password or the recovery key; the context is the level.</summary>
    LevelKey,
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

    private static byte[] FromBase64(string value)
    {
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(value);
        }
        catch (FormatException)
        {
            throw Unreadable();
        }

        // Convert takes white space and bits set after the last byte, so the same bytes have more than one text: only the
        // one that Encrypt writes is read.
        return string.Equals(Convert.ToBase64String(bytes), value, StringComparison.Ordinal) ? bytes : throw Unreadable();
    }

    private static byte[] AssociatedData(byte version, Purpose purpose, ReadOnlySpan<string> context)
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
        _ => throw new ArgumentOutOfRangeException(nameof(purpose)),
    };

    private static CryptographicException Unreadable() => new("The encrypted value cannot be decrypted.");
}

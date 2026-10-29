using System.Security.Cryptography;

namespace Kiler.Crypto;

/// <summary>
/// The check value of a level (M3.2, design 7.1): a known text encrypted with the level key, with the purpose "check" and the
/// level as context. It is kept in levels.json, and a key unlocked from a user file counts only when it verifies against
/// the check of its level (design 7.5). A new level key is <see cref="SecretKey.New"/>. The known text is part of the format.
/// </summary>
public static class LevelCheck
{
    private static ReadOnlySpan<byte> KnownText => "Kiler level check"u8;

    public static string Create(SecretKey key, int level) => EncryptedValue.Encrypt(key, KnownText, Purpose.Check, Level.Context(level));

    /// <summary>
    /// Whether <paramref name="key"/> is the key of <paramref name="level"/>. A check that is damaged, or is not a check of
    /// that level, gives false. A disposed key throws <see cref="ObjectDisposedException"/>: the level is locked, which
    /// is not a key that failed.
    /// </summary>
    public static bool Verify(SecretKey key, string check, int level)
    {
        var context = Level.Context(level);
        byte[] text;
        try
        {
            text = EncryptedValue.Decrypt(key, check, Purpose.Check, context);
        }
        catch (CryptographicException)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(text, KnownText);
    }
}

using System.Security.Cryptography;

namespace Kiler.Crypto;

/// <summary>
/// A set of level keys locked with a secret (M3.3): the <c>salt</c> and the <c>keys</c> of a user file (design 7.3). The salt
/// is the base64 of 16 bytes. Each key is an encrypted value of its 32 bytes, with the key of the secret, the purpose
/// "level-key" and its level as context, by its level. Kiler.Core reads and writes it as JSON, where a level is a name.
/// </summary>
public sealed class LockedKeys
{
    public LockedKeys(string salt, IReadOnlyDictionary<int, string> keys)
    {
        ArgumentNullException.ThrowIfNull(salt);
        ArgumentNullException.ThrowIfNull(keys);
        Salt = salt;
        Keys = keys;
    }

    public string Salt { get; }

    public IReadOnlyDictionary<int, string> Keys { get; }
}

/// <summary>
/// Locking and unlocking a set of level keys (M3.3) with a password (design 7.3 and 7.6) or with the recovery key (design
/// 7.7). The key of the secret is PBKDF2 of it with a new salt at every lock, so the same keys locked again, with a new
/// password or the same, keep their bytes and get a new salt and new values. The secret is stored nowhere: a wrong one
/// unlocks no key.
/// </summary>
public static class LevelKeys
{
    /// <summary>The keys locked with <paramref name="password"/>; a level below 1 is refused, as level 0 has no key.</summary>
    public static LockedKeys Lock(IReadOnlyDictionary<int, SecretKey> keys, ReadOnlySpan<char> password)
    {
        ArgumentNullException.ThrowIfNull(keys);

        // Before PBKDF2, which takes a noticeable time.
        foreach (var (level, key) in keys)
        {
            _ = Level.Context(level, nameof(keys));
            ArgumentNullException.ThrowIfNull(key, nameof(keys));
        }

        var salt = PasswordKey.NewSalt();
        using var passwordKey = PasswordKey.Derive(password, salt);
        var locked = new Dictionary<int, string>(keys.Count);
        foreach (var (level, key) in keys)
        {
            var context = Level.Context(level);
            locked.Add(level, key.Use(bytes => EncryptedValue.Encrypt(passwordKey, bytes, Purpose.LevelKey, context)));
        }

        return new LockedKeys(Convert.ToBase64String(salt), locked);
    }

    /// <summary>The keys locked with the 32 characters of <paramref name="recoveryKey"/>, as with a password.</summary>
    public static LockedKeys Lock(IReadOnlyDictionary<int, SecretKey> keys, RecoveryKey recoveryKey)
    {
        ArgumentNullException.ThrowIfNull(recoveryKey);
        return Lock(keys, recoveryKey.Secret);
    }

    /// <summary>
    /// The keys of <paramref name="locked"/> that <paramref name="password"/> unlocks, which the caller disposes. The set
    /// comes from the share, so what is not a key locked with this password is left out, not an error: a wrong password, or
    /// a salt that is not 16 bytes of base64, gives no key, and a damaged value, a key moved to another level, a value that
    /// does not hold 32 bytes and an entry of a level below 1 or with no value leave out that entry. The caller keeps a key
    /// only when the check of its level verifies it (<see cref="LevelCheck.Verify"/>, design 7.5).
    /// </summary>
    public static IReadOnlyDictionary<int, SecretKey> Unlock(LockedKeys locked, ReadOnlySpan<char> password)
    {
        ArgumentNullException.ThrowIfNull(locked);
        var unlocked = new Dictionary<int, SecretKey>();

        Span<byte> salt = stackalloc byte[PasswordKey.SaltSize];
        if (!Convert.TryFromBase64String(locked.Salt, salt, out var saltLength) || saltLength != PasswordKey.SaltSize)
        {
            return unlocked;
        }

        using var passwordKey = PasswordKey.Derive(password, salt);
        var bytes = GC.AllocateArray<byte>(SecretKey.Size, pinned: true);
        try
        {
            foreach (var (level, value) in locked.Keys)
            {
                if (level >= Level.Lowest && value is not null && TryDecrypt(passwordKey, value, level, bytes))
                {
                    unlocked.Add(level, SecretKey.FromBytes(bytes));
                }
            }
        }
        catch
        {
            foreach (var key in unlocked.Values)
            {
                key.Dispose();
            }

            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }

        return unlocked;
    }

    /// <summary>The keys of <paramref name="locked"/> that the 32 characters of <paramref name="recoveryKey"/> unlock, as with a password.</summary>
    public static IReadOnlyDictionary<int, SecretKey> Unlock(LockedKeys locked, RecoveryKey recoveryKey)
    {
        ArgumentNullException.ThrowIfNull(recoveryKey);
        return Unlock(locked, recoveryKey.Secret);
    }

    private static bool TryDecrypt(SecretKey passwordKey, string value, int level, Span<byte> key)
    {
        try
        {
            EncryptedValue.Decrypt(passwordKey, value, key, Purpose.LevelKey, Level.Context(level));
            return true;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }
}

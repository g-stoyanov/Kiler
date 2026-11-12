using System.Security.Cryptography;

namespace Kiler.Crypto;

/// <summary>
/// A set of level keys locked with a secret (M3.3): the <c>salt</c> and the <c>keys</c> of a user file (design 7.3). The salt
/// is the base64 of 16 bytes. Each key is an encrypted value of its 32 bytes, with the key of the secret, the purpose
/// "level-key" and its level as context, keyed by the digits of the level. Kiler.Core reads and writes it as JSON.
/// <para>
/// Under the same salt, it can hold the admin key (M3.9): <see cref="AdminPublicKey"/>, the SubjectPublicKeyInfo of its
/// public key as an encrypted value with the purpose "admin-public-key" and no context, which every user has; and
/// <see cref="AdminPrivateKey"/>, the PKCS#8 of its private key as an encrypted value with the purpose "admin-private-key"
/// and the public key as the context, which an admin's set and the recovery set have. So one PBKDF2 unlocks them all.
/// </para>
/// </summary>
public sealed class LockedKeys
{
    public LockedKeys(string salt, IReadOnlyDictionary<int, string> keys, string? adminPublicKey = null, string? adminPrivateKey = null)
    {
        ArgumentNullException.ThrowIfNull(salt);
        ArgumentNullException.ThrowIfNull(keys);
        Salt = salt;
        Keys = keys;
        AdminPublicKey = adminPublicKey;
        AdminPrivateKey = adminPrivateKey;
    }

    public string Salt { get; }

    public IReadOnlyDictionary<int, string> Keys { get; }

    /// <summary>The locked public key of the admin key, or <c>null</c> in a set locked without it.</summary>
    public string? AdminPublicKey { get; }

    /// <summary>The locked private key of the admin key, or <c>null</c> in a set that is not an admin's or the recovery set.</summary>
    public string? AdminPrivateKey { get; }
}

/// <summary>
/// What a secret unlocks of a <see cref="LockedKeys"/> (M3.9): the level keys, the public key of the admin key and the
/// admin key itself. The caller disposes the level keys and the admin key, or hands them on, as with the keys of
/// <see cref="LevelKeys.Unlock(LockedKeys, ReadOnlySpan{char})"/>.
/// </summary>
public sealed class UnlockedKeys
{
    internal UnlockedKeys(IReadOnlyDictionary<int, SecretKey> levels, string? adminPublicKey, AdminKey? adminKey)
    {
        Levels = levels;
        AdminPublicKey = adminPublicKey;
        AdminKey = adminKey;
    }

    /// <summary>
    /// The level keys that unlocked, not yet verified: the caller keeps one only when <see cref="LevelCheck.Verify"/> of its
    /// level is true, and disposes the rest (design 7.5).
    /// </summary>
    public IReadOnlyDictionary<int, SecretKey> Levels { get; }

    /// <summary>The public key of the admin key, a public key of P-256, or <c>null</c> when it did not unlock.</summary>
    public string? AdminPublicKey { get; }

    /// <summary>The admin key, whose public key is <see cref="AdminPublicKey"/>, or <c>null</c> when it did not unlock.</summary>
    public AdminKey? AdminKey { get; }
}

/// <summary>
/// Locking and unlocking a set of level keys (M3.3) with a password (design 7.3 and 7.6) or with the recovery key (design
/// 7.7), with the admin key or without it (M3.9). The key of the secret is PBKDF2 of it with a new salt at every lock, so
/// the same keys locked again, with a new password or the same, keep their bytes and get a new salt and new values. The
/// secret is stored nowhere: a wrong one unlocks no key.
/// </summary>
public static class LevelKeys
{
    /// <summary>The keys locked with <paramref name="password"/>; a level below 1 is refused, as level 0 has no key.</summary>
    public static LockedKeys Lock(IReadOnlyDictionary<int, SecretKey> keys, ReadOnlySpan<char> password) =>
        Lock(keys, adminPublicKey: null, adminKey: null, password, withAdminKey: false);

    /// <summary>The keys locked with the 32 characters of <paramref name="recoveryKey"/>, as with a password.</summary>
    public static LockedKeys Lock(IReadOnlyDictionary<int, SecretKey> keys, RecoveryKey recoveryKey)
    {
        ArgumentNullException.ThrowIfNull(recoveryKey);
        return recoveryKey.Use(secret => Lock(keys, secret));
    }

    /// <summary>
    /// The keys locked with <paramref name="password"/>, with <paramref name="adminPublicKey"/>, the public key of the admin
    /// key, and with the private key of <paramref name="adminKey"/> when it is given, for an admin. A public key that is not
    /// one of P-256 in its one base64 form, and an admin key of another public key, are refused.
    /// </summary>
    public static LockedKeys Lock(IReadOnlyDictionary<int, SecretKey> keys, string adminPublicKey, AdminKey? adminKey, ReadOnlySpan<char> password)
    {
        ArgumentNullException.ThrowIfNull(adminPublicKey);
        return Lock(keys, adminPublicKey, adminKey, password, withAdminKey: true);
    }

    /// <summary>The keys and the admin key locked with the 32 characters of <paramref name="recoveryKey"/>, as with a password.</summary>
    public static LockedKeys Lock(IReadOnlyDictionary<int, SecretKey> keys, string adminPublicKey, AdminKey? adminKey, RecoveryKey recoveryKey)
    {
        ArgumentNullException.ThrowIfNull(recoveryKey);
        return recoveryKey.Use(secret => Lock(keys, adminPublicKey, adminKey, secret));
    }

    /// <summary>
    /// The keys of <paramref name="locked"/> that <paramref name="password"/> unlocks, which the caller disposes. The set
    /// comes from the share, so what is not a key locked with this password is left out, not an error: a wrong password, or
    /// a salt that is not 16 bytes of base64, gives no key, and a damaged value, a key moved to another level, a value that
    /// does not hold 32 bytes and an entry of a level below 1 or with no value leave out that entry. The caller keeps a key
    /// only when the check of its level verifies it (<see cref="LevelCheck.Verify"/>, design 7.5). The admin key is not
    /// unlocked.
    /// </summary>
    public static IReadOnlyDictionary<int, SecretKey> Unlock(LockedKeys locked, ReadOnlySpan<char> password)
    {
        ArgumentNullException.ThrowIfNull(locked);
        using var secretKey = KeyOf(locked, password);
        return secretKey is null ? new Dictionary<int, SecretKey>() : UnlockLevels(locked, secretKey);
    }

    /// <summary>The keys of <paramref name="locked"/> that the 32 characters of <paramref name="recoveryKey"/> unlock, as with a password.</summary>
    public static IReadOnlyDictionary<int, SecretKey> Unlock(LockedKeys locked, RecoveryKey recoveryKey)
    {
        ArgumentNullException.ThrowIfNull(recoveryKey);
        return recoveryKey.Use(secret => Unlock(locked, secret));
    }

    /// <summary>
    /// The keys of <paramref name="locked"/> that <paramref name="password"/> unlocks, as <see cref="Unlock(LockedKeys, ReadOnlySpan{char})"/>,
    /// with the admin key, through one PBKDF2. What is not the admin key locked with this password is left out too: a public
    /// key that does not decrypt or is not a SubjectPublicKeyInfo of P-256 gives neither the public key nor the private key,
    /// and a private key that does not decrypt with its public key as the context, or is not a whole PKCS#8 of that public
    /// key, is not given (M3.9).
    /// </summary>
    public static UnlockedKeys UnlockAll(LockedKeys locked, ReadOnlySpan<char> password)
    {
        ArgumentNullException.ThrowIfNull(locked);
        using var secretKey = KeyOf(locked, password);
        if (secretKey is null)
        {
            return new UnlockedKeys(new Dictionary<int, SecretKey>(), adminPublicKey: null, adminKey: null);
        }

        var levels = UnlockLevels(locked, secretKey);
        try
        {
            var adminPublicKey = UnlockPublicKey(secretKey, locked.AdminPublicKey);
            var adminKey = adminPublicKey is null || locked.AdminPrivateKey is null
                ? null
                : AdminKey.Unlock(secretKey, locked.AdminPrivateKey, adminPublicKey);
            return new UnlockedKeys(levels, adminPublicKey, adminKey);
        }
        catch
        {
            Dispose(levels);
            throw;
        }
    }

    /// <summary>What the 32 characters of <paramref name="recoveryKey"/> unlock of <paramref name="locked"/>, as with a password.</summary>
    public static UnlockedKeys UnlockAll(LockedKeys locked, RecoveryKey recoveryKey)
    {
        ArgumentNullException.ThrowIfNull(recoveryKey);
        return recoveryKey.Use(secret => UnlockAll(locked, secret));
    }

    private static LockedKeys Lock(IReadOnlyDictionary<int, SecretKey> keys, string? adminPublicKey, AdminKey? adminKey, ReadOnlySpan<char> password, bool withAdminKey)
    {
        ArgumentNullException.ThrowIfNull(keys);

        // Before PBKDF2, which takes a noticeable time.
        foreach (var (level, key) in keys)
        {
            _ = Level.Context(level, nameof(keys));
            ArgumentNullException.ThrowIfNull(key, nameof(keys));
        }

        byte[]? spki = null;
        if (withAdminKey)
        {
            // The SubjectPublicKeyInfo that the key exports again, so an unlock gives the same text that seals are checked with.
            using (var check = EcdsaP256.ImportPublicKey(adminPublicKey!))
            {
                spki = check?.ExportSubjectPublicKeyInfo();
                if (spki is null || !string.Equals(Convert.ToBase64String(spki), adminPublicKey, StringComparison.Ordinal))
                {
                    throw new ArgumentException("The public key of the admin key is not one of P-256 in its one form.", nameof(adminPublicKey));
                }
            }

            if (adminKey is not null && !string.Equals(adminKey.PublicKey, adminPublicKey, StringComparison.Ordinal))
            {
                throw new ArgumentException("The admin key is not the one of the public key.", nameof(adminKey));
            }
        }

        var salt = PasswordKey.NewSalt();
        using var passwordKey = PasswordKey.Derive(password, salt);
        var locked = new Dictionary<int, string>(keys.Count);
        foreach (var (level, key) in keys)
        {
            var context = Level.Context(level);
            locked.Add(level, key.Use(bytes => EncryptedValue.Encrypt(passwordKey, bytes, Purpose.LevelKey, context)));
        }

        var lockedPublicKey = spki is null ? null : EncryptedValue.Encrypt(passwordKey, spki, Purpose.AdminPublicKey);
        return new LockedKeys(Convert.ToBase64String(salt), locked, lockedPublicKey, adminKey?.Lock(passwordKey));
    }

    /// <summary>The key of the secret and the salt of <paramref name="locked"/>, or <c>null</c> for a salt that is not 16 bytes of base64.</summary>
    private static SecretKey? KeyOf(LockedKeys locked, ReadOnlySpan<char> secret)
    {
        Span<byte> salt = stackalloc byte[PasswordKey.SaltSize];
        if (!Convert.TryFromBase64String(locked.Salt, salt, out var saltLength) || saltLength != PasswordKey.SaltSize)
        {
            return null;
        }

        return PasswordKey.Derive(secret, salt);
    }

    private static Dictionary<int, SecretKey> UnlockLevels(LockedKeys locked, SecretKey secretKey)
    {
        var unlocked = new Dictionary<int, SecretKey>();
        var bytes = GC.AllocateArray<byte>(SecretKey.Size, pinned: true);
        try
        {
            foreach (var (level, value) in locked.Keys)
            {
                if (level >= Level.Lowest && value is not null && TryDecrypt(secretKey, value, level, bytes))
                {
                    unlocked.Add(level, SecretKey.FromBytes(bytes));
                }
            }
        }
        catch
        {
            Dispose(unlocked);
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }

        return unlocked;
    }

    /// <summary>The public key that <paramref name="value"/> holds, when it is a SubjectPublicKeyInfo of P-256; anything else is null.</summary>
    private static string? UnlockPublicKey(SecretKey secretKey, string? value)
    {
        if (value is null)
        {
            return null;
        }

        byte[] spki;
        try
        {
            spki = EncryptedValue.Decrypt(secretKey, value, Purpose.AdminPublicKey);
        }
        catch (CryptographicException)
        {
            return null;
        }

        var publicKey = Convert.ToBase64String(spki);
        using var key = EcdsaP256.ImportPublicKey(publicKey);
        return key is null ? null : publicKey;
    }

    private static bool TryDecrypt(SecretKey secretKey, string value, int level, Span<byte> key)
    {
        try
        {
            EncryptedValue.Decrypt(secretKey, value, key, Purpose.LevelKey, Level.Context(level));
            return true;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    private static void Dispose(IReadOnlyDictionary<int, SecretKey> keys)
    {
        foreach (var key in keys.Values)
        {
            key.Dispose();
        }
    }
}

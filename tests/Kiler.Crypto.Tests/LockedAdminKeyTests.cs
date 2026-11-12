using System.Security.Cryptography;

namespace Kiler.Crypto.Tests;

// The admin key locked with the level keys (M3.9). Each lock and unlock runs PBKDF2 with 600 000 iterations once, so the
// tests take few.
public class LockedAdminKeyTests
{
    private const string Password = "demo password";

    private static readonly byte[] Text = """{"account":"demo.user","level":1}"""u8.ToArray();

    [Fact]
    public void UnlockAll_WithThePasswordOfTheLock_GivesTheLevelKeysThePublicKeyAndThePrivateKey()
    {
        using var one = SecretKey.New();
        using var two = SecretKey.New();
        using var admin = AdminKey.New();
        var locked = LevelKeys.Lock(Levels(one, two), admin.PublicKey, admin, Password);

        using var unlocked = new Unlocked(LevelKeys.UnlockAll(locked, Password));

        Assert.Equal([1, 2], unlocked.Levels);
        Assert.True(unlocked.Keys.Levels[1].Copy().SequenceEqual(one.Copy()), "The key of level 1 differs.");
        Assert.True(unlocked.Keys.Levels[2].Copy().SequenceEqual(two.Copy()), "The key of level 2 differs.");
        Assert.Equal(admin.PublicKey, unlocked.Keys.AdminPublicKey);
        Assert.NotNull(unlocked.Keys.AdminKey);
        Assert.Equal(admin.PublicKey, unlocked.Keys.AdminKey.PublicKey);
        var seal = unlocked.Keys.AdminKey.Seal(SealPurpose.UserFile, Text);
        Assert.True(AdminSeal.Verify(admin.PublicKey, SealPurpose.UserFile, Text, seal), "The unlocked key seals for another public key.");
    }

    [Fact]
    public void UnlockAll_ASetWithThePublicKeyOnly_GivesNoPrivateKey()
    {
        using var one = SecretKey.New();
        using var admin = AdminKey.New();
        var locked = LevelKeys.Lock(Levels(one), admin.PublicKey, null, Password);

        using var unlocked = new Unlocked(LevelKeys.UnlockAll(locked, Password));

        Assert.Null(locked.AdminPrivateKey);
        Assert.Equal([1], unlocked.Levels);
        Assert.Equal(admin.PublicKey, unlocked.Keys.AdminPublicKey);
        Assert.Null(unlocked.Keys.AdminKey);
    }

    [Fact]
    public void UnlockAll_WithAWrongPassword_GivesNoKey()
    {
        using var one = SecretKey.New();
        using var admin = AdminKey.New();
        var locked = LevelKeys.Lock(Levels(one), admin.PublicKey, admin, Password);

        using var unlocked = new Unlocked(LevelKeys.UnlockAll(locked, "demo passwore"));

        Assert.Empty(unlocked.Levels);
        Assert.Null(unlocked.Keys.AdminPublicKey);
        Assert.Null(unlocked.Keys.AdminKey);
    }

    // The recovery set of levels.json holds both keys, so restoring admin access gives back the same admin key.
    [Fact]
    public void UnlockAll_WithTheRecoveryKeyOfTheLock_GivesTheAdminKey_AndWithAnotherNoKey()
    {
        using var one = SecretKey.New();
        using var admin = AdminKey.New();
        using var recoveryKey = RecoveryKey.New();
        using var another = RecoveryKey.New();
        var locked = LevelKeys.Lock(Levels(one), admin.PublicKey, admin, recoveryKey);

        using (var unlocked = new Unlocked(LevelKeys.UnlockAll(locked, recoveryKey)))
        {
            Assert.Equal([1], unlocked.Levels);
            Assert.Equal(admin.PublicKey, unlocked.Keys.AdminPublicKey);
            Assert.Equal(admin.PublicKey, unlocked.Keys.AdminKey?.PublicKey);
        }

        using (var unlocked = new Unlocked(LevelKeys.UnlockAll(locked, another)))
        {
            Assert.Empty(unlocked.Levels);
            Assert.Null(unlocked.Keys.AdminPublicKey);
            Assert.Null(unlocked.Keys.AdminKey);
        }
    }

    [Fact]
    public void UnlockAll_ASetLockedWithoutTheAdminKey_GivesTheLevelKeysOnly()
    {
        using var one = SecretKey.New();
        var locked = LevelKeys.Lock(Levels(one), Password);

        using var unlocked = new Unlocked(LevelKeys.UnlockAll(locked, Password));

        Assert.Equal([1], unlocked.Levels);
        Assert.Null(unlocked.Keys.AdminPublicKey);
        Assert.Null(unlocked.Keys.AdminKey);
    }

    // A change of password locks again an admin key that came from an unlock, so an imported key must export again.
    [Fact]
    public void Lock_AgainWithANewPassword_KeepsTheSameAdminKeyWithANewSalt()
    {
        using var one = SecretKey.New();
        using var admin = AdminKey.New();
        var locked = LevelKeys.Lock(Levels(one), admin.PublicKey, admin, Password);

        using var unlocked = new Unlocked(LevelKeys.UnlockAll(locked, Password));
        var relocked = LevelKeys.Lock(unlocked.Keys.Levels, unlocked.Keys.AdminPublicKey!, unlocked.Keys.AdminKey, "new demo password");
        using var again = new Unlocked(LevelKeys.UnlockAll(relocked, "new demo password"));

        Assert.NotEqual(locked.Salt, relocked.Salt);
        Assert.Equal([1], again.Levels);
        Assert.Equal(admin.PublicKey, again.Keys.AdminPublicKey);
        Assert.NotNull(again.Keys.AdminKey);
        var seal = again.Keys.AdminKey.Seal(SealPurpose.Settings, Text);
        Assert.True(AdminSeal.Verify(admin.PublicKey, SealPurpose.Settings, Text, seal), "The key locked again seals for another public key.");
    }

    // The format: under the salt of the level keys, the SubjectPublicKeyInfo of the public key with the purpose
    // "admin-public-key" and no context, and the PKCS#8 of the private key with the purpose "admin-private-key" and the
    // public key as the context.
    [Fact]
    public void Lock_LocksThePublicKeyAndThePrivateKeyUnderTheSaltOfTheLevelKeys()
    {
        using var one = SecretKey.New();
        using var admin = AdminKey.New();
        var locked = LevelKeys.Lock(Levels(one), admin.PublicKey, admin, Password);
        using var passwordKey = PasswordKey.Derive(Password, Convert.FromBase64String(locked.Salt));

        var level = EncryptedValue.Decrypt(passwordKey, locked.Keys[1], Purpose.LevelKey, "1");
        var spki = EncryptedValue.Decrypt(passwordKey, locked.AdminPublicKey!, Purpose.AdminPublicKey);
        var pkcs8 = EncryptedValue.Decrypt(passwordKey, locked.AdminPrivateKey!, Purpose.AdminPrivateKey, admin.PublicKey);
        try
        {
            using var imported = ECDsa.Create();
            imported.ImportPkcs8PrivateKey(pkcs8, out var read);

            Assert.True(level.SequenceEqual(one.Copy()), "The key of level 1 differs.");
            Assert.Equal(admin.PublicKey, Convert.ToBase64String(spki));
            Assert.Equal(pkcs8.Length, read);
            Assert.Equal(admin.PublicKey, Convert.ToBase64String(imported.ExportSubjectPublicKeyInfo()));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(level);
            CryptographicOperations.ZeroMemory(pkcs8);
        }
    }

    // Written by the code of M3.9 with the password of this class and kept as it is: a user file locked before stays
    // readable, and its labels "admin-public-key" and "admin-private-key" with it.
    [Fact]
    public void UnlockAll_ASetWrittenEarlier_StillUnlocks()
    {
        const string PublicKey = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE4kwPshobfjeb0oSkqowOhdDOv4M01octg/aoVi7iakbyDRVdDRDToOY0z+Fkl0lFcws68w6OMyWhMlAz+KTKFg==";
        var locked = new LockedKeys(
            "41A48oGDoSIBF/WQ0F0ncA==",
            new Dictionary<int, string> { [1] = "AaU5ec+3qxT4DO1qgcB3vjyfzrC5X2CRNuokE8dm/r+MulcboW9w0VQAUEKdJFyH/TOAQF2px2el8kRgcQ==" },
            "ARx0kN2xXk6v1mE6hYTydpFZi6PPtG4eHiJaDfF+PQNJFw8Agickh8x+JfglKXHkumfK1WyycTTbfRzbmcNQv2RLuK8m8U7t98nQ9/7POLykZ399a6QiYQsz0utJIbmHatv5esC/ZyhzwXxKUCZnyV7WReoUc1lO",
            "AYDzi0fT0XUSPgB5nJnVPFCD2Qc4Zrkpe7IPufKt/lRbWdVE83jMjv+xqzfKsZoyAlgA+v2FqLIdV1n2oLSfRUF8dFEO9HqvFyiwNpEHN2cjaKRHm74MNbaNI3j4mvyPM2yCZpmrkkMB6qQtJFJtruz6R69zMeJq2WsyKqNxa856H73+ygzbXTuhEY7mUB9ZpyOonwQwEDUFHsVFHKX92d69zPmwqUM=");

        using var unlocked = new Unlocked(LevelKeys.UnlockAll(locked, Password));

        Assert.Equal([1], unlocked.Levels);
        Assert.Equal(PublicKey, unlocked.Keys.AdminPublicKey);
        Assert.Equal(PublicKey, unlocked.Keys.AdminKey?.PublicKey);
    }

    [Fact]
    public void Lock_AnAdminKeyOfAnotherPublicKey_IsRefused()
    {
        using var one = SecretKey.New();
        using var admin = AdminKey.New();
        using var another = AdminKey.New();

        Assert.Throws<ArgumentException>(() => LevelKeys.Lock(Levels(one), another.PublicKey, admin, Password));
    }

    [Theory]
    [MemberData(nameof(NotP256PublicKeys))]
    public void Lock_APublicKeyThatIsNotOfP256InItsOneForm_IsRefused(string publicKey)
    {
        using var one = SecretKey.New();

        Assert.Throws<ArgumentException>(() => LevelKeys.Lock(Levels(one), publicKey, null, Password));
    }

    public static TheoryData<string> NotP256PublicKeys()
    {
        using var p256 = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var brainpool = ECDsa.Create(ECCurve.NamedCurves.brainpoolP256r1);
        var good = Convert.ToBase64String(p256.ExportSubjectPublicKeyInfo());

        return new TheoryData<string>
        {
            "",
            "not a key",
            good[..8] + " " + good[8..],
            Convert.ToBase64String([.. Convert.FromBase64String(good), 0]),
            Convert.ToBase64String(brainpool.ExportSubjectPublicKeyInfo()),
        };
    }

    [Fact]
    public void LockAndUnlockAll_Null_AreRefused()
    {
        using var one = SecretKey.New();
        using var admin = AdminKey.New();
        using var recoveryKey = RecoveryKey.New();
        var locked = LevelKeys.Lock(Levels(one), admin.PublicKey, admin, Password);

        Assert.Throws<ArgumentNullException>(() => LevelKeys.Lock(null!, admin.PublicKey, admin, Password));
        Assert.Throws<ArgumentNullException>(() => LevelKeys.Lock(Levels(one), null!, admin, Password));
        Assert.Throws<ArgumentNullException>(() => LevelKeys.Lock(Levels(one), admin.PublicKey, admin, (RecoveryKey)null!));
        Assert.Throws<ArgumentNullException>(() => LevelKeys.UnlockAll(null!, Password));
        Assert.Throws<ArgumentNullException>(() => LevelKeys.UnlockAll(locked, (RecoveryKey)null!));
    }

    // Each is made with the password, the salt and the context of the test, so each is left out for its own reason: without
    // the check, it would unlock. The level key of the set unlocks in each.
    [Theory]
    [MemberData(nameof(NotThePublicKey))]
    public void UnlockAll_NotALockedPublicKeyOfP256_GivesNeitherAdminKey(string salt, string level1, string? publicKey, string? privateKey)
    {
        var locked = new LockedKeys(salt, new Dictionary<int, string> { [1] = level1 }, publicKey, privateKey);

        using var unlocked = new Unlocked(LevelKeys.UnlockAll(locked, Password));

        Assert.Equal([1], unlocked.Levels);
        Assert.Null(unlocked.Keys.AdminPublicKey);
        Assert.Null(unlocked.Keys.AdminKey);
    }

    public static TheoryData<string, string, string?, string?> NotThePublicKey()
    {
        using var one = SecretKey.New();
        using var admin = AdminKey.New();
        using var p256 = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var brainpool = ECDsa.Create(ECCurve.NamedCurves.brainpoolP256r1);
        var locked = LevelKeys.Lock(Levels(one), admin.PublicKey, admin, Password);
        using var passwordKey = PasswordKey.Derive(Password, Convert.FromBase64String(locked.Salt));
        var spki = Convert.FromBase64String(admin.PublicKey);
        var brainpoolPublicKey = Convert.ToBase64String(brainpool.ExportSubjectPublicKeyInfo());
        var point = p256.ExportParameters(false).Q;
        using var explicitCurve = ECDsa.Create(p256.ExportExplicitParameters(includePrivateParameters: false));

        string Row(byte[] publicKey, Purpose purpose = Purpose.AdminPublicKey, params string[] context) =>
            EncryptedValue.Encrypt(passwordKey, publicKey, purpose, context);

        using (var good = new Unlocked(LevelKeys.UnlockAll(locked, Password)))
        {
            Assert.True(good.Keys.AdminKey is not null, "The set the others are made of does not unlock.");
        }

        var (salt, level1, privateKey) = (locked.Salt, locked.Keys[1], locked.AdminPrivateKey);

        return new TheoryData<string, string, string?, string?>
        {
            // The locked private key read as the public key.
            { salt, level1, privateKey, privateKey },
            // The public key with the purpose of the private key, or with a context.
            { salt, level1, Row(spki, Purpose.AdminPrivateKey), privateKey },
            { salt, level1, Row(spki, Purpose.AdminPublicKey, admin.PublicKey), privateKey },
            // The level key read as the public key.
            { salt, level1, level1, privateKey },
            // A private key without the public key.
            { salt, level1, null, privateKey },
            // Not a SubjectPublicKeyInfo of P-256 in its one form, each with the private key locked for it.
            { salt, level1, Row(brainpool.ExportSubjectPublicKeyInfo()), EncryptedValue.Encrypt(passwordKey, brainpool.ExportPkcs8PrivateKey(), Purpose.AdminPrivateKey, brainpoolPublicKey) },
            { salt, level1, Row(explicitCurve.ExportSubjectPublicKeyInfo()), privateKey },
            { salt, level1, Row([.. spki, 0]), privateKey },
            { salt, level1, Row([0x04, .. point.X!, .. point.Y!]), privateKey },
            { salt, level1, Row([]), privateKey },
            // Not an encrypted value in its one form.
            { salt, level1, "not base64!", privateKey },
            { salt, level1, locked.AdminPublicKey![..4] + " " + locked.AdminPublicKey[4..], privateKey },
        };
    }

    [Theory]
    [MemberData(nameof(NotThePrivateKey))]
    public void UnlockAll_NotThePrivateKeyOfThePublicKey_GivesThePublicKeyOnly(string salt, string publicKey, string privateKey, string expected)
    {
        var locked = new LockedKeys(salt, new Dictionary<int, string>(), publicKey, privateKey);

        using var unlocked = new Unlocked(LevelKeys.UnlockAll(locked, Password));

        Assert.Equal(expected, unlocked.Keys.AdminPublicKey);
        Assert.Null(unlocked.Keys.AdminKey);
    }

    public static TheoryData<string, string, string, string> NotThePrivateKey()
    {
        using var one = SecretKey.New();
        using var admin = AdminKey.New();
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        var locked = LevelKeys.Lock(Levels(one), admin.PublicKey, admin, Password);
        using var passwordKey = PasswordKey.Derive(Password, Convert.FromBase64String(locked.Salt));
        var otherPublicKey = Convert.ToBase64String(other.ExportSubjectPublicKeyInfo());
        var pkcs8 = EncryptedValue.Decrypt(passwordKey, locked.AdminPrivateKey!, Purpose.AdminPrivateKey, admin.PublicKey);
        using (var good = new Unlocked(LevelKeys.UnlockAll(locked, Password)))
        {
            Assert.True(good.Keys.AdminKey is not null, "The set the others are made of does not unlock.");
        }

        string Row(byte[] privateKey, Purpose purpose = Purpose.AdminPrivateKey, params string[] context) =>
            EncryptedValue.Encrypt(passwordKey, privateKey, purpose, context);

        var publicKey = locked.AdminPublicKey!;
        var expected = admin.PublicKey;
        var rows = new TheoryData<string, string, string, string>
        {
            // The locked public key read as the private key.
            { locked.Salt, publicKey, publicKey, expected },
            // The private key with the purpose of the public key, with no context, or with the context of another key.
            { locked.Salt, publicKey, Row(pkcs8, Purpose.AdminPublicKey, admin.PublicKey), expected },
            { locked.Salt, publicKey, Row(pkcs8), expected },
            { locked.Salt, publicKey, Row(pkcs8, Purpose.AdminPrivateKey, otherPublicKey), expected },
            // The level key read as the private key.
            { locked.Salt, publicKey, locked.Keys[1], expected },
            // The private key of another key pair, of P-256 or of P-384, locked for this public key.
            { locked.Salt, publicKey, Row(other.ExportPkcs8PrivateKey(), Purpose.AdminPrivateKey, admin.PublicKey), expected },
            { locked.Salt, publicKey, Row(p384.ExportPkcs8PrivateKey(), Purpose.AdminPrivateKey, admin.PublicKey), expected },
            // The private key with a byte after it, and bytes that are not a PKCS#8.
            { locked.Salt, publicKey, Row([.. pkcs8, 0], Purpose.AdminPrivateKey, admin.PublicKey), expected },
            { locked.Salt, publicKey, Row(new byte[SecretKey.Size], Purpose.AdminPrivateKey, admin.PublicKey), expected },
            { locked.Salt, publicKey, Row([], Purpose.AdminPrivateKey, admin.PublicKey), expected },
            // Not an encrypted value in its one form, or shorter than one of nothing.
            { locked.Salt, publicKey, "not base64!", expected },
            { locked.Salt, publicKey, Convert.ToBase64String(new byte[EncryptedValue.MinSize - 1]), expected },
        };
        CryptographicOperations.ZeroMemory(pkcs8);
        return rows;
    }

    private static Dictionary<int, SecretKey> Levels(params SecretKey[] keys) =>
        keys.Select((key, at) => (key, level: at + 1)).ToDictionary(pair => pair.level, pair => pair.key);
}

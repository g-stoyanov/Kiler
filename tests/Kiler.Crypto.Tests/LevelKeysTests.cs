namespace Kiler.Crypto.Tests;

// Each lock and unlock runs PBKDF2 with 600 000 iterations once, so the tests take few.
public class LevelKeysTests
{
    private const string Password = "demo password";

    [Fact]
    public void Unlock_WithThePasswordOfTheLock_GivesTheSameKeys()
    {
        using var one = SecretKey.New();
        using var two = SecretKey.New();
        var locked = LevelKeys.Lock(new Dictionary<int, SecretKey> { [1] = one, [2] = two }, Password);

        using var unlocked = new KeySet(LevelKeys.Unlock(locked, Password));

        Assert.Equal([1, 2], unlocked.Levels);
        Assert.True(unlocked[1].Copy().SequenceEqual(one.Copy()), "The key of level 1 differs.");
        Assert.True(unlocked[2].Copy().SequenceEqual(two.Copy()), "The key of level 2 differs.");
    }

    [Fact]
    public void Unlock_WithAWrongPassword_GivesNoKey()
    {
        using var one = SecretKey.New();
        using var two = SecretKey.New();
        var locked = LevelKeys.Lock(new Dictionary<int, SecretKey> { [1] = one, [2] = two }, Password);

        using var unlocked = new KeySet(LevelKeys.Unlock(locked, "demo passwore"));

        Assert.Empty(unlocked.Levels);
    }

    // The level is the context of a locked key, so a key cannot be given as the key of another level.
    [Fact]
    public void Unlock_AKeyMovedToAnotherLevel_DoesNotUnlockIt()
    {
        using var one = SecretKey.New();
        using var two = SecretKey.New();
        var locked = LevelKeys.Lock(new Dictionary<int, SecretKey> { [1] = one, [2] = two }, Password);
        var moved = new LockedKeys(locked.Salt, new Dictionary<int, string> { [1] = locked.Keys[2], [2] = locked.Keys[1], [3] = locked.Keys[2] });

        using var unlocked = new KeySet(LevelKeys.Unlock(moved, Password));

        Assert.Empty(unlocked.Levels);
    }

    [Fact]
    public void Lock_AgainWithANewPassword_KeepsTheSameKeysWithANewSalt()
    {
        using var one = SecretKey.New();
        using var two = SecretKey.New();
        var locked = LevelKeys.Lock(new Dictionary<int, SecretKey> { [1] = one, [2] = two }, Password);

        using var unlocked = new KeySet(LevelKeys.Unlock(locked, Password));
        var relocked = LevelKeys.Lock(unlocked.Keys, "new demo password");
        using var again = new KeySet(LevelKeys.Unlock(relocked, "new demo password"));

        Assert.NotEqual(locked.Salt, relocked.Salt);
        Assert.Equal([1, 2], again.Levels);
        Assert.True(again[1].Copy().SequenceEqual(one.Copy()), "The key of level 1 changed with the password.");
        Assert.True(again[2].Copy().SequenceEqual(two.Copy()), "The key of level 2 changed with the password.");
    }

    [Fact]
    public void Lock_TheSameKeysTwice_TakesANewSaltAndNewValues()
    {
        using var key = SecretKey.New();
        var keys = new Dictionary<int, SecretKey> { [2] = key };

        var first = LevelKeys.Lock(keys, Password);
        var second = LevelKeys.Lock(keys, Password);

        Assert.NotEqual(first.Salt, second.Salt);
        Assert.NotEqual(first.Keys[2], second.Keys[2]);
    }

    // The format of the user file (design 7.3): the salt as base64 of 16 bytes, and each key as an encrypted value of its
    // 32 bytes with the key of the password, the purpose "level-key" and the level as context.
    [Fact]
    public void Lock_GivesTheSaltAndEachKeyAsALevelKeyValue()
    {
        using var key = SecretKey.New();

        var locked = LevelKeys.Lock(new Dictionary<int, SecretKey> { [2] = key }, Password);

        var salt = Convert.FromBase64String(locked.Salt);
        Assert.Equal(PasswordKey.SaltSize, salt.Length);
        Assert.Equal([2], locked.Keys.Keys);
        using var passwordKey = PasswordKey.Derive(Password, salt);
        Assert.True(EncryptedValue.Decrypt(passwordKey, locked.Keys[2], Purpose.LevelKey, "2").SequenceEqual(key.Copy()), "The locked value is not the key.");
    }

    [Fact]
    public void Lock_NoKeys_GivesASaltAndNoKey()
    {
        var locked = LevelKeys.Lock(new Dictionary<int, SecretKey>(), Password);

        Assert.Equal(PasswordKey.SaltSize, Convert.FromBase64String(locked.Salt).Length);
        Assert.Empty(locked.Keys);
    }

    // The salt comes from the user file on the share: one that PBKDF2 does not take is a password that does not unlock.
    [Theory]
    [InlineData("")]
    [InlineData("not base64!")]
    [InlineData("AAAAAAAAAAAAAAAAAAAA")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAA=")]
    public void Unlock_ASaltThatIsNotSixteenBytes_GivesNoKey(string salt)
    {
        using var key = SecretKey.New();
        var value = EncryptedValue.Encrypt(key, new byte[SecretKey.Size], Purpose.LevelKey, "1");

        using var unlocked = new KeySet(LevelKeys.Unlock(new LockedKeys(salt, new Dictionary<int, string> { [1] = value }), Password));

        Assert.Empty(unlocked.Levels);
    }

    // A salt of 15 bytes is not the salt of 16 that ends with a zero byte, though a buffer of 16 would read it so.
    [Fact]
    public void Unlock_ASaltOneByteShort_GivesNoKey()
    {
        byte[] salt = [.. PasswordKey.NewSalt()[..^1], 0];
        using var passwordKey = PasswordKey.Derive(Password, salt);
        var value = EncryptedValue.Encrypt(passwordKey, new byte[SecretKey.Size], Purpose.LevelKey, "1");

        using var unlocked = new KeySet(LevelKeys.Unlock(new LockedKeys(Convert.ToBase64String(salt[..^1]), new Dictionary<int, string> { [1] = value }), Password));

        Assert.Empty(unlocked.Levels);
    }

    [Fact]
    public void Unlock_ADamagedKey_LeavesOutOnlyThatKey()
    {
        using var one = SecretKey.New();
        using var two = SecretKey.New();
        var locked = LevelKeys.Lock(new Dictionary<int, SecretKey> { [1] = one, [2] = two }, Password);
        var bytes = Convert.FromBase64String(locked.Keys[2]);
        bytes[^1] ^= 0x01;
        var damaged = new LockedKeys(locked.Salt, new Dictionary<int, string> { [1] = locked.Keys[1], [2] = Convert.ToBase64String(bytes) });

        using var unlocked = new KeySet(LevelKeys.Unlock(damaged, Password));

        Assert.Equal([1], unlocked.Levels);
        Assert.True(unlocked[1].Copy().SequenceEqual(one.Copy()), "The key of level 1 differs.");
    }

    // Values of the password, the purpose and the level that hold no key of 32 bytes, or are not of a level that has a key.
    [Fact]
    public void Unlock_AnEntryThatIsNotALockedLevelKey_IsLeftOut()
    {
        var salt = PasswordKey.NewSalt();
        using var passwordKey = PasswordKey.Derive(Password, salt);
        var keys = new Dictionary<int, string>
        {
            [1] = EncryptedValue.Encrypt(passwordKey, new byte[SecretKey.Size - 1], Purpose.LevelKey, "1"),
            [2] = EncryptedValue.Encrypt(passwordKey, new byte[SecretKey.Size + 1], Purpose.LevelKey, "2"),
            [3] = EncryptedValue.Encrypt(passwordKey, [], Purpose.LevelKey, "3"),
            [4] = null!,
            [0] = EncryptedValue.Encrypt(passwordKey, new byte[SecretKey.Size], Purpose.LevelKey, "0"),
            [-1] = EncryptedValue.Encrypt(passwordKey, new byte[SecretKey.Size], Purpose.LevelKey, "-1"),
            [5] = EncryptedValue.Encrypt(passwordKey, new byte[SecretKey.Size], Purpose.LevelKey, "5"),
        };

        using var unlocked = new KeySet(LevelKeys.Unlock(new LockedKeys(Convert.ToBase64String(salt), keys), Password));

        Assert.Equal([5], unlocked.Levels);
    }

    // Written by the code of M3.3 and kept as it is: the label "level-key", the level as its digits, PBKDF2 of the password
    // and the base64 of the salt are part of the format, and the user files stay readable.
    [Fact]
    public void Unlock_KeysLockedEarlier_StillUnlock()
    {
        var locked = new LockedKeys(
            "VzNRnoUHuzoFQQEdqco0zg==",
            new Dictionary<int, string>
            {
                [1] = "AU3oMLcGfQxHdox8bJDMx5Es/CvV6iF111r1sTdz+yVsQdgafWFF7h78QaOcFmPmawSgV4H6C7g4nHYsCg==",
                [2] = "ATGlINbvIuyTFKeMZOfrgIDMePrjsHTdu4a1a+LepKmnQV4l0AvWwDrN4pqw5pnc4Ozprb+o/wiKhLH4ug==",
            });

        using var unlocked = new KeySet(LevelKeys.Unlock(locked, Password));

        Assert.Equal([1, 2], unlocked.Levels);
        Assert.True(unlocked[1].Copy().SequenceEqual(Enumerable.Range(0, SecretKey.Size).Select(i => (byte)i)), "The key of level 1 differs.");
        Assert.True(unlocked[2].Copy().SequenceEqual(Enumerable.Range(SecretKey.Size, SecretKey.Size).Select(i => (byte)i)), "The key of level 2 differs.");
    }

    // Level 0 is not encrypted, so it has no key. The levels are checked before the slow PBKDF2.
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void Lock_ALevelBelowOne_IsRefused(int level)
    {
        using var key = SecretKey.New();

        Assert.Throws<ArgumentOutOfRangeException>(() => LevelKeys.Lock(new Dictionary<int, SecretKey> { [1] = key, [level] = key }, Password));
    }

    [Fact]
    public void Lock_ADisposedKey_Throws()
    {
        var key = SecretKey.New();
        key.Dispose();

        Assert.Throws<ObjectDisposedException>(() => LevelKeys.Lock(new Dictionary<int, SecretKey> { [1] = key }, Password));
    }

    [Fact]
    public void Lock_ANullKey_IsRefused()
    {
        Assert.Throws<ArgumentNullException>(() => LevelKeys.Lock(null!, Password));
        Assert.Throws<ArgumentNullException>(() => LevelKeys.Lock(new Dictionary<int, SecretKey> { [1] = null! }, Password));
    }

    [Fact]
    public void ANullLockedSet_IsRefused()
    {
        Assert.Throws<ArgumentNullException>(() => new LockedKeys(null!, new Dictionary<int, string>()));
        Assert.Throws<ArgumentNullException>(() => new LockedKeys("AAAAAAAAAAAAAAAAAAAAAA==", null!));
        Assert.Throws<ArgumentNullException>(() => LevelKeys.Unlock(null!, Password));
    }

    [Fact]
    public void APasswordOverTheLimit_IsRefused()
    {
        var locked = new LockedKeys(Convert.ToBase64String(PasswordKey.NewSalt()), new Dictionary<int, string>());

        Assert.Throws<ArgumentException>(() => LevelKeys.Lock(new Dictionary<int, SecretKey>(), new string('a', PasswordKey.MaxLength + 1)));
        Assert.Throws<ArgumentException>(() => LevelKeys.Unlock(locked, new string('a', PasswordKey.MaxLength + 1)));
    }

    // The automatic locking (M4) disposes the keys that were unlocked; the keys of the caller stay as they are.
    [Fact]
    public void Unlock_GivesKeysOfTheirOwn()
    {
        using var key = SecretKey.New();
        var locked = LevelKeys.Lock(new Dictionary<int, SecretKey> { [1] = key }, Password);

        var unlocked = LevelKeys.Unlock(locked, Password);
        unlocked[1].Dispose();

        Assert.False(key.Buffer.All(b => b == 0), "The key that was locked was zeroed with the one unlocked.");
    }
}

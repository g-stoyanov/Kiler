namespace Kiler.Crypto.Tests;

public class LevelCheckTests
{
    [Fact]
    public void Verify_WithTheKeyOfItsLevel_IsTrue()
    {
        using var key = SecretKey.New();

        var check = LevelCheck.Create(key, 2);

        Assert.True(LevelCheck.Verify(key, check, 2));
    }

    [Fact]
    public void Verify_WithAnotherKey_IsFalse()
    {
        using var key = SecretKey.New();
        using var another = SecretKey.New();

        var check = LevelCheck.Create(key, 2);

        Assert.False(LevelCheck.Verify(another, check, 2));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(12)]
    public void Verify_ForAnotherLevel_IsFalse(int level)
    {
        using var key = SecretKey.New();

        var check = LevelCheck.Create(key, 2);

        Assert.False(LevelCheck.Verify(key, check, level));
    }

    [Fact]
    public void Verify_AChangedByteAnywhere_IsFalse()
    {
        using var key = SecretKey.New();
        var bytes = Convert.FromBase64String(LevelCheck.Create(key, 2));

        for (var i = 0; i < bytes.Length; i++)
        {
            var changed = (byte[])bytes.Clone();
            changed[i] ^= 0x01;

            Assert.False(LevelCheck.Verify(key, Convert.ToBase64String(changed), 2), $"A change of byte {i} was not detected.");
        }
    }

    // levels.json is on the share, so a damaged check is a key that is not proven, not an error.
    [Theory]
    [InlineData("")]
    [InlineData("not base64!")]
    [InlineData("AAAA")]
    public void Verify_NotAnEncryptedValue_IsFalse(string check)
    {
        using var key = SecretKey.New();

        Assert.False(LevelCheck.Verify(key, check, 2));
    }

    // A value of the key, the purpose and the level that holds another text: only the known text proves the key.
    [Theory]
    [InlineData("")]
    [InlineData("another text")]
    [InlineData("known text, less its last byte")]
    [InlineData("known text and one byte more")]
    public void Verify_AnotherTextOfTheKeyAndLevel_IsFalse(string kind)
    {
        using var key = SecretKey.New();
        var known = EncryptedValue.Decrypt(key, LevelCheck.Create(key, 2), Purpose.Check, "2");
        byte[] text = kind switch
        {
            "" => [],
            "another text" => "another text"u8.ToArray(),
            "known text, less its last byte" => known[..^1],
            _ => [.. known, 0x20],
        };

        var check = EncryptedValue.Encrypt(key, text, Purpose.Check, "2");

        Assert.False(LevelCheck.Verify(key, check, 2));
    }

    [Fact]
    public void Verify_TheKnownTextForAnotherPurpose_IsFalse()
    {
        using var key = SecretKey.New();
        var known = EncryptedValue.Decrypt(key, LevelCheck.Create(key, 2), Purpose.Check, "2");

        var levelKey = EncryptedValue.Encrypt(key, known, Purpose.LevelKey, "2");

        Assert.False(LevelCheck.Verify(key, levelKey, 2));
    }

    // Written by the code of M3.2 and kept as it is: the label "check", the known text and the level as its digits are
    // part of the format, and the checks in levels.json stay valid.
    [Fact]
    public void Verify_ACheckWrittenEarlier_IsTrue()
    {
        using var key = SecretKey.FromBytes(Enumerable.Range(0, SecretKey.Size).Select(i => (byte)i).ToArray());

        Assert.True(LevelCheck.Verify(key, "AVu+IUSVV96sBg4bZoXr3VhmUmMhlyw5njdcHmNiRBHst4JWOvSpuupt+6y6Aw==", 2));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void ALevelBelowOne_IsRefused(int level)
    {
        using var key = SecretKey.New();
        var check = LevelCheck.Create(key, 1);

        Assert.Throws<ArgumentOutOfRangeException>(() => LevelCheck.Create(key, level));
        Assert.Throws<ArgumentOutOfRangeException>(() => LevelCheck.Verify(key, check, level));
    }

    [Fact]
    public void Verify_ANullCheck_IsRefused()
    {
        using var key = SecretKey.New();

        Assert.Throws<ArgumentNullException>(() => LevelCheck.Verify(key, null!, 2));
    }

    // The automatic locking (M4) disposes a key: that is a locked level, not a key that failed its check.
    [Fact]
    public void Verify_WithADisposedKey_Throws()
    {
        var key = SecretKey.New();
        var check = LevelCheck.Create(key, 2);

        key.Dispose();

        Assert.Throws<ObjectDisposedException>(() => LevelCheck.Verify(key, check, 2));
    }
}

using System.Globalization;
using System.Text.RegularExpressions;

namespace Kiler.Crypto.Tests;

public class RecoveryKeyTests
{
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    // The 20 bytes whose groups of 5 bits are 0 to 31 in order: their RFC 4648 base32 is "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567"
    // (checked with Python's base64.b32encode), so their Crockford Base32 is its alphabet in order.
    private static readonly byte[] AlphabetBytes = Convert.FromHexString("00443214C74254B635CF84653A56D7C675BE77DF");

    private const string AlphabetKey = "0123-4567-89AB-CDEF-GHJK-MNPQ-RSTV-WXYZ";

    [Fact]
    public void New_GivesEightGroupsOfFourCharactersOfTheAlphabet()
    {
        using var key = RecoveryKey.New();

        Assert.Matches(new Regex("^[0-9A-HJKMNP-TV-Z]{4}(-[0-9A-HJKMNP-TV-Z]{4}){7}$"), key.ToDisplayString());
    }

    [Fact]
    public void New_TwiceGivesTwoKeys()
    {
        using var first = RecoveryKey.New();
        using var second = RecoveryKey.New();

        Assert.NotEqual(first.ToDisplayString(), second.ToDisplayString());
    }

    [Fact]
    public void FromBytes_TheBytesWhoseGroupsAre0To31_GiveTheAlphabetInOrder()
    {
        using var key = RecoveryKey.FromBytes(AlphabetBytes);

        Assert.Equal(AlphabetKey, key.ToDisplayString());
    }

    [Theory]
    [InlineData("0000-0000-0000-0000-0000-0000-0000-0000", "0000000000000000000000000000000000000000")]
    [InlineData("ZZZZ-ZZZZ-ZZZZ-ZZZZ-ZZZZ-ZZZZ-ZZZZ-ZZZZ", "FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF")]
    public void FromBytes_AllZerosOrAllOnes_GiveTheFirstOrTheLastCharacter(string expected, string hex)
    {
        using var key = RecoveryKey.FromBytes(Convert.FromHexString(hex));

        Assert.Equal(expected, key.ToDisplayString());
    }

    [Theory]
    [InlineData(19)]
    [InlineData(21)]
    public void FromBytes_OfAnotherLength_IsRefused(int length) =>
        Assert.Throws<ArgumentException>(() => RecoveryKey.FromBytes(new byte[length]));

    // A typed key is read without regard to case, hyphens and spaces, with O read as 0 and I and L as 1.
    [Theory]
    [InlineData(AlphabetKey)]
    [InlineData(Alphabet)]
    [InlineData("0123-4567-89ab-cdef-ghjk-mnpq-rstv-wxyz")]
    [InlineData("0123 4567 89AB CDEF GHJK MNPQ RSTV WXYZ")]
    [InlineData("  0123 - 4567 89aB-CdEf GHJK--MNPQ RSTV-WXYZ - ")]
    [InlineData("O123-4567-89AB-CDEF-GHJK-MNPQ-RSTV-WXYZ")]
    [InlineData("o123-4567-89AB-CDEF-GHJK-MNPQ-RSTV-WXYZ")]
    [InlineData("0I23-4567-89AB-CDEF-GHJK-MNPQ-RSTV-WXYZ")]
    [InlineData("0i23-4567-89AB-CDEF-GHJK-MNPQ-RSTV-WXYZ")]
    [InlineData("0L23-4567-89AB-CDEF-GHJK-MNPQ-RSTV-WXYZ")]
    [InlineData("0l23-4567-89AB-CDEF-GHJK-MNPQ-RSTV-WXYZ")]
    public void TryParse_TheKeyAsItIsTyped_ReadsIt(string typed)
    {
        Assert.True(RecoveryKey.TryParse(typed, out var key));

        using (key)
        {
            Assert.Equal(AlphabetKey, key.ToDisplayString());
        }
    }

    // The letters are read as ASCII, not by the culture of the machine: in Turkish, "i" in upper case is "İ" (U+0130).
    [Fact]
    public void TryParse_UnderATurkishCulture_ReadsASmallI()
    {
        var culture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
        try
        {
            Assert.True(RecoveryKey.TryParse("0i23-4567-89ab-cdef-ghjk-mnpq-rstv-wxyz", out var key));
            key.Dispose();
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }
    }

    [Theory]
    [MemberData(nameof(NotKeys))]
    public void TryParse_AnotherCharacterOrLength_IsRefused(string typed)
    {
        Assert.False(RecoveryKey.TryParse(typed, out var key));
        Assert.Null(key);
    }

    public static TheoryData<string> NotKeys() => new()
    {
        "",
        "----    ----",
        // One character less and one more.
        AlphabetKey[..^1],
        AlphabetKey + "Z",
        AlphabetKey + "-0000",
        // U is not in the alphabet; the other characters are not hyphens or spaces.
        "U123-4567-89AB-CDEF-GHJK-MNPQ-RSTV-WXYZ",
        "0123_4567_89AB_CDEF_GHJK_MNPQ_RSTV_WXYZ",
        "0123.4567.89AB.CDEF.GHJK.MNPQ.RSTV.WXYZ",
        "0123\t4567\t89AB\tCDEF\tGHJK\tMNPQ\tRSTV\tWXYZ",
        AlphabetKey + "\n",
        // Letters that look like those of the alphabet: the Cyrillic A, the fullwidth A, and the dotless i and the Kelvin
        // sign, which Unicode maps to I and k.
        AlphabetKey.Replace('A', (char)0x0410),
        AlphabetKey.Replace('A', (char)0xFF21),
        AlphabetKey.Replace('1', (char)0x0131),
        AlphabetKey.Replace('K', (char)0x212A),
    };

    [Fact]
    public void Unlock_WithTheRecoveryKeyAsTyped_GivesTheKeys()
    {
        using var levelKey = SecretKey.New();
        using var recoveryKey = RecoveryKey.New();
        var locked = LevelKeys.Lock(new Dictionary<int, SecretKey> { [3] = levelKey }, recoveryKey);
        var typed = recoveryKey.ToDisplayString().ToLowerInvariant().Replace('-', ' ');

        Assert.True(RecoveryKey.TryParse(typed, out var typedKey));
        using (typedKey)
        {
            using var unlocked = new KeySet(LevelKeys.Unlock(locked, typedKey));

            Assert.Equal([3], unlocked.Levels);
            Assert.True(unlocked[3].Copy().SequenceEqual(levelKey.Copy()), "The key of level 3 differs.");
        }
    }

    // The 32 characters in upper case are the secret, as a password is: the format of the lock does not change with it.
    [Fact]
    public void Lock_WithTheRecoveryKey_TakesItsThirtyTwoCharactersAsThePassword()
    {
        using var levelKey = SecretKey.New();
        using var recoveryKey = RecoveryKey.FromBytes(AlphabetBytes);

        var locked = LevelKeys.Lock(new Dictionary<int, SecretKey> { [1] = levelKey }, recoveryKey);

        using var unlocked = new KeySet(LevelKeys.Unlock(locked, Alphabet));
        Assert.Equal([1], unlocked.Levels);
    }

    [Fact]
    public void ToString_DoesNotShowTheKey()
    {
        using var key = RecoveryKey.FromBytes(AlphabetBytes);

        Assert.DoesNotContain("0123", key.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ADisposedKey_CannotBeUsed()
    {
        var key = RecoveryKey.New();

        key.Dispose();
        key.Dispose();

        Assert.Throws<ObjectDisposedException>(() => key.ToDisplayString());
        Assert.Throws<ObjectDisposedException>(() => LevelKeys.Lock(new Dictionary<int, SecretKey>(), key));
        Assert.Throws<ObjectDisposedException>(() => LevelKeys.Unlock(new LockedKeys(Convert.ToBase64String(PasswordKey.NewSalt()), new Dictionary<int, string>()), key));
    }

    [Fact]
    public void Dispose_ZeroesTheKey()
    {
        var key = RecoveryKey.FromBytes(AlphabetBytes);

        key.Dispose();

        Assert.True(key.Buffer.All(c => c == '\0'), "The key was not zeroed.");
    }

    [Fact]
    public void ANullRecoveryKey_IsRefused()
    {
        Assert.Throws<ArgumentNullException>(() => LevelKeys.Lock(new Dictionary<int, SecretKey>(), (RecoveryKey)null!));
        Assert.Throws<ArgumentNullException>(() => LevelKeys.Unlock(new LockedKeys(Convert.ToBase64String(PasswordKey.NewSalt()), new Dictionary<int, string>()), (RecoveryKey)null!));
    }
}

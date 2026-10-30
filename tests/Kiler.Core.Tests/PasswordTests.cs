using Kiler.Crypto;

namespace Kiler.Core.Tests;

/// <summary>The length of a password (decided at the start of M4).</summary>
public class PasswordTests
{
    [Theory]
    [InlineData(0, false)]
    [InlineData(Password.MinLength - 1, false)]
    [InlineData(Password.MinLength, true)]
    [InlineData(Password.MaxLength, true)]
    [InlineData(Password.MaxLength + 1, false)]
    public void APassword_HasFrom8To1024Characters(int length, bool fits)
    {
        Assert.Equal(fits, Password.Invalid(new string('p', length)) is null);
    }

    [Fact]
    public void TheLongestPassword_IsTheLimitOfKilerCrypto()
    {
        using var key = SecretKey.New();
        var keys = new Dictionary<int, SecretKey> { [1] = key };

        Assert.Single(LevelKeys.Lock(keys, new string('p', Password.MaxLength)).Keys);
        Assert.Throws<ArgumentException>(() => LevelKeys.Lock(keys, new string('p', Password.MaxLength + 1)));
    }

    [Fact]
    public void ATemporaryPassword_Is16CharactersOfLowercaseCrockfordBase32_NewEachTime()
    {
        var first = Password.NewTemporary();
        var second = Password.NewTemporary();

        // Compared with Assert.True, as it is a password (SKILL.md, "Tests").
        Assert.True(first.Length == 16 && first.All("0123456789abcdefghjkmnpqrstvwxyz".Contains), "The temporary password is not 16 characters of the alphabet.");
        Assert.True(Password.Invalid(first) is null, "The temporary password is not a password.");
        Assert.True(first != second, "Two temporary passwords are the same.");
    }
}

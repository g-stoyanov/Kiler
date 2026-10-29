using System.Security.Cryptography;
using System.Text;

namespace Kiler.Crypto.Tests;

public class PasswordKeyTests
{
    // RFC 7914, section 11: PBKDF2-HMAC-SHA-256 with dkLen 64. The first 32 bytes are the key of the same inputs.
    [Theory]
    [InlineData("passwd", "salt", 1, "55ac046e56e3089fec1691c22544b605f94185216dde0465e68b9d57c20dacbc49ca9cccf179b645991664b39d77ef317c71b845b1e30bd509112041d3a19783")]
    [InlineData("Password", "NaCl", 80000, "4ddcd8f60b98be21830cee5ef22701f9641a4418d04c0414aeff08876b34ab56a1d425a1225833549adb841b51c9b3176a272bdebba1d078478f62b397f33c8d")]
    public void Derive_TheTestVectorsOfRfc7914_GiveTheirKeys(string password, string salt, int iterations, string expected)
    {
        var destination = new byte[64];

        PasswordKey.Derive(password, Encoding.ASCII.GetBytes(salt), iterations, destination);

        Assert.Equal(expected, Convert.ToHexStringLower(destination));
    }

    [Fact]
    public void Derive_TakesPbkdf2WithSha256AndTheIterationsOfTheFormat()
    {
        var salt = PasswordKey.NewSalt();

        using var key = PasswordKey.Derive("demo password", salt);

        var expected = Rfc2898DeriveBytes.Pbkdf2("demo password"u8, salt, PasswordKey.Iterations, HashAlgorithmName.SHA256, SecretKey.Size);
        Assert.Equal(600_000, PasswordKey.Iterations);
        Assert.True(key.Copy().SequenceEqual(expected), "The key is not PBKDF2-SHA256 of the password.");
    }

    [Fact]
    public void Derive_APasswordThatIsNotAscii_TakesItsUtf8()
    {
        var salt = PasswordKey.NewSalt();
        var destination = new byte[SecretKey.Size];

        PasswordKey.Derive("парола ü 🔑", salt, 1, destination);

        var expected = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes("парола ü 🔑"), salt, 1, HashAlgorithmName.SHA256, SecretKey.Size);
        Assert.True(destination.SequenceEqual(expected), "The key is not of the UTF-8 of the password.");
    }

    [Fact]
    public void Derive_AnotherPasswordOrSalt_GivesAnotherKey()
    {
        var salt = PasswordKey.NewSalt();

        using var key = PasswordKey.Derive("demo password", salt);
        using var otherPassword = PasswordKey.Derive("demo passwore", salt);
        using var otherSalt = PasswordKey.Derive("demo password", PasswordKey.NewSalt());

        Assert.False(key.Copy().SequenceEqual(otherPassword.Copy()), "Two passwords give the same key.");
        Assert.False(key.Copy().SequenceEqual(otherSalt.Copy()), "Two salts give the same key.");
    }

    [Fact]
    public void NewSalt_GivesSixteenRandomBytes()
    {
        var first = PasswordKey.NewSalt();
        var second = PasswordKey.NewSalt();

        Assert.Equal(16, first.Length);
        Assert.False(first.SequenceEqual(second), "Two new salts are the same.");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(15)]
    [InlineData(17)]
    public void Derive_ASaltOfAnotherLength_IsRefused(int length) =>
        Assert.Throws<ArgumentException>(() => PasswordKey.Derive("demo password", new byte[length]));

    [Fact]
    public void Derive_APasswordOverTheLimit_IsRefused()
    {
        using var key = PasswordKey.Derive(new string('a', PasswordKey.MaxLength), PasswordKey.NewSalt());

        Assert.Throws<ArgumentException>(() => PasswordKey.Derive(new string('a', PasswordKey.MaxLength + 1), PasswordKey.NewSalt()));
    }
}

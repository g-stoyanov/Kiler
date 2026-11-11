using System.Security.Cryptography;

namespace Kiler.Crypto.Tests;

// Each lock and unlock runs PBKDF2 with 600 000 iterations once, so the tests take few.
public class SigningKeyTests
{
    private const string Password = "demo signing password";

    private const string Label = "kiler-signing-key";

    private static readonly ReleaseVersion Version = new(1, 0, 0);

    private static readonly byte[] Hash = SHA256.HashData("demo Kiler.exe"u8);

    [Fact]
    public void New_GivesAKeyOfP256WhosePublicKeyIsItsSubjectPublicKeyInfo()
    {
        using var key = SigningKey.New();

        var spki = Convert.FromBase64String(key.PublicKey);
        using var imported = ECDsa.Create();
        imported.ImportSubjectPublicKeyInfo(spki, out var read);

        Assert.Equal(key.PublicKey, Convert.ToBase64String(spki));
        Assert.Equal(spki.Length, read);
        Assert.Equal(256, imported.KeySize);
        Assert.Equal(ECCurve.NamedCurves.nistP256.Oid.Value, imported.ExportParameters(false).Curve.Oid.Value);
    }

    [Fact]
    public void New_Twice_GivesTwoKeys()
    {
        using var one = SigningKey.New();
        using var two = SigningKey.New();

        Assert.NotEqual(one.PublicKey, two.PublicKey);
    }

    [Fact]
    public void TryUnlock_WithThePasswordOfTheLock_GivesTheSameKey()
    {
        using var key = SigningKey.New();
        var file = key.Lock(Password);

        Assert.True(SigningKey.TryUnlock(file, Password, out var unlocked));
        using (unlocked)
        {
            Assert.Equal(key.PublicKey, unlocked.PublicKey);
            Assert.True(UpdateSignature.Verify(key.PublicKey, Version, Hash, unlocked.Sign(Version, Hash)), "The unlocked key signs for another public key.");
        }
    }

    [Fact]
    public void TryUnlock_WithAnotherPassword_IsFalse()
    {
        using var key = SigningKey.New();
        var file = key.Lock(Password);

        Assert.False(SigningKey.TryUnlock(file, "demo signing passwore", out var unlocked));
        Assert.Null(unlocked);
    }

    // The format of the key file (decided in M3.4): one line of the label, the format, the salt, the public key and the
    // PKCS#8 of the key as an encrypted value of the purpose "signing-key", with the public key as its context.
    [Fact]
    public void Lock_GivesTheLabelTheFormatTheSaltThePublicKeyAndTheLockedKey()
    {
        using var key = SigningKey.New();

        var parts = key.Lock(Password).Split('.');

        Assert.Equal(5, parts.Length);
        Assert.Equal(Label, parts[0]);
        Assert.Equal("1", parts[1]);
        var salt = Convert.FromBase64String(parts[2]);
        Assert.Equal(PasswordKey.SaltSize, salt.Length);
        Assert.Equal(key.PublicKey, parts[3]);
        using var passwordKey = PasswordKey.Derive(Password, salt);
        var pkcs8 = EncryptedValue.Decrypt(passwordKey, parts[4], Purpose.SigningKey, key.PublicKey);
        using var imported = ECDsa.Create();
        imported.ImportPkcs8PrivateKey(pkcs8, out var read);
        CryptographicOperations.ZeroMemory(pkcs8);
        Assert.Equal(pkcs8.Length, read);
        Assert.Equal(key.PublicKey, Convert.ToBase64String(imported.ExportSubjectPublicKeyInfo()));
    }

    [Fact]
    public void Lock_TheSameKeyTwice_TakesANewSaltAndANewValue()
    {
        using var key = SigningKey.New();

        var first = key.Lock(Password).Split('.');
        var second = key.Lock(Password).Split('.');

        Assert.NotEqual(first[2], second[2]);
        Assert.NotEqual(first[4], second[4]);
    }

    // The file can be attacked offline once copied (decided in M3.4).
    [Fact]
    public void Lock_APasswordShorterThan16_IsRefused()
    {
        using var key = SigningKey.New();

        Assert.Equal(16, SigningKey.MinPasswordLength);
        Assert.Throws<ArgumentException>(() => key.Lock(new string('a', SigningKey.MinPasswordLength - 1)));
        Assert.Throws<ArgumentException>(() => key.Lock(""));
    }

    [Fact]
    public void Lock_APasswordOf16_IsTaken()
    {
        using var key = SigningKey.New();
        var password = new string('a', SigningKey.MinPasswordLength);

        Assert.True(SigningKey.TryUnlock(key.Lock(password), password, out var unlocked));
        unlocked.Dispose();
    }

    // The salt, the x of the public key, the version of the value, its nonce, its ciphertext and its tag.
    [Theory]
    [InlineData(2, 0)]
    [InlineData(2, 15)]
    [InlineData(3, 30)]
    [InlineData(4, 0)]
    [InlineData(4, 5)]
    [InlineData(4, 40)]
    [InlineData(4, -1)]
    public void TryUnlock_AChangedByte_IsFalse(int part, int at)
    {
        using var key = SigningKey.New();
        var parts = key.Lock(Password).Split('.');
        var bytes = Convert.FromBase64String(parts[part]);
        bytes[at < 0 ? bytes.Length + at : at] ^= 0x01;
        parts[part] = Convert.ToBase64String(bytes);

        Assert.False(SigningKey.TryUnlock(string.Join('.', parts), Password, out _));
    }

    // Lock refuses such a password, so it locked no file: one answer, as for any other wrong password.
    [Fact]
    public void TryUnlock_APasswordLongerThanTheLongest_IsFalse()
    {
        using var key = SigningKey.New();
        var file = key.Lock(Password);

        Assert.False(SigningKey.TryUnlock(file, new string('a', PasswordKey.MaxLength + 1), out var unlocked));
        Assert.Null(unlocked);
    }

    [Fact]
    public void TryUnlock_WithThePublicKeyOfAnotherKey_IsFalse()
    {
        using var key = SigningKey.New();
        using var another = SigningKey.New();
        var parts = key.Lock(Password).Split('.');
        parts[3] = another.PublicKey;

        Assert.False(SigningKey.TryUnlock(string.Join('.', parts), Password, out _));
    }

    // A line break that an editor or a password manager adds at the end.
    [Theory]
    [InlineData("\r\n")]
    [InlineData("\n")]
    [InlineData(" ")]
    public void TryUnlock_WithWhiteSpaceAtTheEnds_GivesTheKey(string space)
    {
        using var key = SigningKey.New();
        var file = key.Lock(Password);

        Assert.True(SigningKey.TryUnlock(space + file + space, Password, out var unlocked));
        using (unlocked)
        {
            Assert.Equal(key.PublicKey, unlocked.PublicKey);
        }
    }

    // Each is made with the password, the salt and the context of the test, so each is refused for its own reason: without
    // the check, it would unlock.
    [Theory]
    [MemberData(nameof(NotKeyFiles))]
    public void TryUnlock_NotAKeyFileOfItsFormat_IsFalse(string file)
    {
        Assert.False(SigningKey.TryUnlock(file, Password, out var unlocked));
        Assert.Null(unlocked);
    }

    public static TheoryData<string> NotKeyFiles()
    {
        using var p256 = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var brainpool = ECDsa.Create(ECCurve.NamedCurves.brainpoolP256r1);
        var publicKey = Convert.ToBase64String(p256.ExportSubjectPublicKeyInfo());
        var otherPublicKey = Convert.ToBase64String(other.ExportSubjectPublicKeyInfo());
        var brainpoolPublicKey = Convert.ToBase64String(brainpool.ExportSubjectPublicKeyInfo());
        var pkcs8 = p256.ExportPkcs8PrivateKey();
        var salt = PasswordKey.NewSalt();
        var shortSalt = salt[..15];
        using var passwordKey = KeyOf(Password, salt);
        using var shortSaltKey = KeyOf(Password, shortSalt);

        var value = EncryptedValue.Encrypt(passwordKey, pkcs8, Purpose.SigningKey, publicKey);
        var saltText = Convert.ToBase64String(salt);
        var good = string.Join('.', Label, "1", saltText, publicKey, value);
        Assert.True(SigningKey.TryUnlock(good, Password, out var unlocked), "The file the others are made of does not unlock.");
        unlocked.Dispose();

        return new TheoryData<string>
        {
            "",
            "not a key file",
            string.Join('.', "kiler-signing-kez", "1", saltText, publicKey, value),
            string.Join('.', Label, "2", saltText, publicKey, value),
            string.Join('.', Label, "01", saltText, publicKey, value),
            string.Join('.', Label, saltText, publicKey, value),
            string.Join('.', Label, "1", saltText, publicKey, value, ""),
            string.Join('.', Label, "1", saltText, publicKey, value, "x"),
            // Base64 that is not in its one form.
            string.Join('.', Label, "1", saltText[..4] + " " + saltText[4..], publicKey, value),
            string.Join('.', Label, "1", saltText, publicKey[..8] + " " + publicKey[8..], value),
            // A salt of 15 bytes.
            string.Join('.', Label, "1", Convert.ToBase64String(shortSalt), publicKey, EncryptedValue.Encrypt(shortSaltKey, pkcs8, Purpose.SigningKey, publicKey)),
            // The key of another purpose.
            string.Join('.', Label, "1", saltText, publicKey, EncryptedValue.Encrypt(passwordKey, pkcs8, Purpose.LevelKey, publicKey)),
            // A key whose public key is not the one of the file, locked for that one.
            string.Join('.', Label, "1", saltText, otherPublicKey, EncryptedValue.Encrypt(passwordKey, pkcs8, Purpose.SigningKey, otherPublicKey)),
            // A key of a curve of 256 bits that is not P-256.
            string.Join('.', Label, "1", saltText, brainpoolPublicKey, EncryptedValue.Encrypt(passwordKey, brainpool.ExportPkcs8PrivateKey(), Purpose.SigningKey, brainpoolPublicKey)),
            // A locked key shorter than an encrypted value of nothing.
            string.Join('.', Label, "1", saltText, publicKey, Convert.ToBase64String(new byte[EncryptedValue.MinSize - 1])),
            // Not a PKCS#8.
            string.Join('.', Label, "1", saltText, publicKey, EncryptedValue.Encrypt(passwordKey, new byte[SecretKey.Size], Purpose.SigningKey, publicKey)),
            // A PKCS#8 with a byte after it.
            string.Join('.', Label, "1", saltText, publicKey, EncryptedValue.Encrypt(passwordKey, [.. pkcs8, 0], Purpose.SigningKey, publicKey)),
        };
    }

    // Written by the code of M3.4 with the password of this class and kept as it is: a key file made before stays readable.
    [Fact]
    public void TryUnlock_AFileWrittenEarlier_StillUnlocks()
    {
        const string File = "kiler-signing-key.1.ht5OZ17F2wNvQS7tVXlYJg==.MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEKos/QtaqldBnRN009NliUySK5+DIxOI5yFR2LSrUXU1b2Xie1kt6+HE8jFjPfobYAU6pxXzs3lNEe+rGgnCcrQ==.AYt1I5jM/ufKmNIiWJphKYeBAvd98kZB8cNG6nfhMhZ9dJVTQ4A4S69TFVRrnktxk4m2Jc7Nf3i0Y7YN58cO0B2VOu0IuPr9fAs8kSxQkEAvsOjb62SO2dTerPLbYf6EFmAXSG9HMqaQ7wUJsLdr3xpQJf88O4vAXpqQ1PGmpedyzqAsQJIfP5/pBwV0z9loLAovR+CEKuh4LUCayvhkkVbE86yWK6w=";
        const string PublicKey = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEKos/QtaqldBnRN009NliUySK5+DIxOI5yFR2LSrUXU1b2Xie1kt6+HE8jFjPfobYAU6pxXzs3lNEe+rGgnCcrQ==";

        Assert.True(SigningKey.TryUnlock(File, Password, out var unlocked));
        using (unlocked)
        {
            Assert.Equal(PublicKey, unlocked.PublicKey);
        }
    }

    [Fact]
    public void TryUnlock_Null_IsRefused()
    {
        Assert.Throws<ArgumentNullException>(() => SigningKey.TryUnlock(null!, Password, out _));
    }

    [Fact]
    public void SignAndLock_AfterDispose_Throw()
    {
        var key = SigningKey.New();
        key.Dispose();

        Assert.Throws<ObjectDisposedException>(() => key.Sign(Version, Hash));
        Assert.Throws<ObjectDisposedException>(() => key.Lock(Password));
    }

    [Fact]
    public void ToString_IsTheNameOfTheType()
    {
        using var key = SigningKey.New();

        Assert.Equal(typeof(SigningKey).FullName, key.ToString());
    }

    /// <summary>The key of a password and any salt, as <see cref="PasswordKey.Derive(ReadOnlySpan{char}, ReadOnlySpan{byte})"/> takes only one of 16 bytes.</summary>
    private static SecretKey KeyOf(string password, byte[] salt)
    {
        var bytes = new byte[SecretKey.Size];
        PasswordKey.Derive(password, salt, PasswordKey.Iterations, bytes);
        return SecretKey.FromBytes(bytes);
    }
}

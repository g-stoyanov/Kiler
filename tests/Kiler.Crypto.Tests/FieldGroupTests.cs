using System.Security.Cryptography;
using System.Text;

namespace Kiler.Crypto.Tests;

public class FieldGroupTests
{
    private static readonly byte[] Fields = """{"host":"db.example.test","port":1521}"""u8.ToArray();

    [Fact]
    public void Decrypt_WithItsKeyRecordIdAndLevel_GivesTheFields()
    {
        using var key = SecretKey.New();

        var group = FieldGroup.Encrypt(key, Fields, "8f3a1c", 2);

        Assert.True(FieldGroup.Decrypt(key, group, "8f3a1c", 2).SequenceEqual(Fields), "Decrypted fields differ.");
    }

    [Theory]
    [InlineData("8f3a1d", 2)]
    [InlineData("8F3A1C", 2)]
    [InlineData("8f3a1", 2)]
    [InlineData("8f3a1c", 1)]
    [InlineData("8f3a1c", 3)]
    [InlineData("8f3a1c", 12)]
    public void Decrypt_UnderAnotherRecordIdOrLevel_Fails(string recordId, int level)
    {
        using var key = SecretKey.New();

        var group = FieldGroup.Encrypt(key, Fields, "8f3a1c", 2);

        Assert.Throws<CryptographicException>(() => FieldGroup.Decrypt(key, group, recordId, level));
    }

    [Fact]
    public void Decrypt_WithAnotherKey_Fails()
    {
        using var key = SecretKey.New();
        using var another = SecretKey.New();

        var group = FieldGroup.Encrypt(key, Fields, "8f3a1c", 2);

        Assert.Throws<CryptographicException>(() => FieldGroup.Decrypt(another, group, "8f3a1c", 2));
    }

    [Fact]
    public void Decrypt_AChangedByteAnywhere_Fails()
    {
        using var key = SecretKey.New();
        var bytes = Convert.FromBase64String(FieldGroup.Encrypt(key, Fields, "8f3a1c", 2));

        for (var i = 0; i < bytes.Length; i++)
        {
            var changed = (byte[])bytes.Clone();
            changed[i] ^= 0x01;

            Assert.Throws<CryptographicException>(() => FieldGroup.Decrypt(key, Convert.ToBase64String(changed), "8f3a1c", 2));
        }
    }

    // The value of format 1 that EncryptedValueTests keeps: the record id, then the level as its digits.
    [Fact]
    public void Decrypt_AGroupWrittenEarlier_StillDecrypts()
    {
        using var key = SecretKey.FromBytes(Enumerable.Range(0, SecretKey.Size).Select(i => (byte)i).ToArray());

        var fields = FieldGroup.Decrypt(key, "AXjwinuq6iHd6VkgqznsXcPCWXqrPJaw9582a+NZ4HbXJMfWB9lNUCpn0DX++kvL4CBe", "demo01", 1);

        Assert.Equal("demo value of format 1", Encoding.UTF8.GetString(fields));
    }

    // Level 0 is not encrypted.
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void ALevelBelowOne_IsRefused(int level)
    {
        using var key = SecretKey.New();
        var group = FieldGroup.Encrypt(key, Fields, "8f3a1c", 1);

        Assert.Throws<ArgumentOutOfRangeException>(() => FieldGroup.Encrypt(key, Fields, "8f3a1c", level));
        Assert.Throws<ArgumentOutOfRangeException>(() => FieldGroup.Decrypt(key, group, "8f3a1c", level));
    }

    [Fact]
    public void ARecordIdThatIsNullOrEmpty_IsRefused()
    {
        using var key = SecretKey.New();
        var group = FieldGroup.Encrypt(key, Fields, "8f3a1c", 2);

        Assert.Throws<ArgumentNullException>(() => FieldGroup.Encrypt(key, Fields, null!, 2));
        Assert.Throws<ArgumentException>(() => FieldGroup.Encrypt(key, Fields, "", 2));
        Assert.Throws<ArgumentNullException>(() => FieldGroup.Decrypt(key, group, null!, 2));
        Assert.Throws<ArgumentException>(() => FieldGroup.Decrypt(key, group, "", 2));
    }
}

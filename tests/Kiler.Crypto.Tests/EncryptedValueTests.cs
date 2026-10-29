using System.Security.Cryptography;
using System.Text;

namespace Kiler.Crypto.Tests;

public class EncryptedValueTests
{
    private static readonly byte[] Text = "host=db.example.test;port=1521"u8.ToArray();

    // The GCM specification of McGrew and Viega, appendix B, Test Case 16: AES-256, a 96-bit IV, 60 bytes of text and
    // 20 bytes of associated data.
    private static readonly byte[] GcmKey = Convert.FromHexString("feffe9928665731c6d6a8f9467308308feffe9928665731c6d6a8f9467308308");
    private static readonly byte[] GcmText = Convert.FromHexString("d9313225f88406e5a55909c5aff5269a86a7a9531534f7da2e4c303d8a318a721c3c0c95956809532fcf0e2449a6b525b16aedf5aa0de657ba637b39");
    private static readonly byte[] GcmData = Convert.FromHexString("feedfacedeadbeeffeedfacedeadbeefabaddad2");
    private static readonly byte[] GcmNonce = Convert.FromHexString("cafebabefacedbaddecaf888");
    private const string GcmCiphertext = "522dc1f099567d07f47f37a32a84427d643a8cdcbfe5c0c97598a2bd2555d1aa8cb08e48590dbb3da7b08b1056828838c5f61e6393ba7a0abcc9f662";
    private const string GcmTag = "76fc6ece0f4e1768cddf8853bb2d551b";

    [Fact]
    public void Aes256Gcm_TestCase16OfTheGcmSpecification_GivesItsCiphertextAndTag()
    {
        var ciphertext = new byte[GcmText.Length];
        var tag = new byte[Aes256Gcm.TagSize];

        Aes256Gcm.Encrypt(GcmKey, GcmNonce, GcmText, GcmData, ciphertext, tag);

        Assert.Equal(GcmCiphertext, Convert.ToHexStringLower(ciphertext));
        Assert.Equal(GcmTag, Convert.ToHexStringLower(tag));
    }

    [Fact]
    public void Aes256Gcm_TestCase16OfTheGcmSpecification_Decrypts()
    {
        var plaintext = new byte[GcmText.Length];

        Aes256Gcm.Decrypt(GcmKey, GcmNonce, Convert.FromHexString(GcmCiphertext), Convert.FromHexString(GcmTag), GcmData, plaintext);

        Assert.Equal(Convert.ToHexStringLower(GcmText), Convert.ToHexStringLower(plaintext));
    }

    [Fact]
    public void Decrypt_WhatWasEncrypted_GivesTheSameBytes()
    {
        using var key = SecretKey.New();

        var value = EncryptedValue.Encrypt(key, Text, Purpose.Group, "8f3a1c", "2");

        Assert.True(EncryptedValue.Decrypt(key, value, Purpose.Group, "8f3a1c", "2").SequenceEqual(Text), "Decrypted bytes differ.");
    }

    [Fact]
    public void Decrypt_AnEncryptedEmptyValue_GivesNoBytes()
    {
        using var key = SecretKey.New();

        var value = EncryptedValue.Encrypt(key, [], Purpose.Check, "1");

        Assert.Equal(EncryptedValue.MinSize, Convert.FromBase64String(value).Length);
        Assert.True(EncryptedValue.Decrypt(key, value, Purpose.Check, "1").Length == 0, "An empty value decrypts to some bytes.");
    }

    [Fact]
    public void Encrypt_GivesTheVersionTheNonceTheCiphertextAndTheTag()
    {
        using var key = SecretKey.New();

        var bytes = Convert.FromBase64String(EncryptedValue.Encrypt(key, Text, Purpose.Group, "8f3a1c", "2"));

        Assert.Equal(EncryptedValue.Version1, bytes[0]);
        Assert.Equal(1 + Aes256Gcm.NonceSize + Text.Length + Aes256Gcm.TagSize, bytes.Length);
        Assert.False(bytes.AsSpan(1 + Aes256Gcm.NonceSize, Text.Length).SequenceEqual(Text), "The text is not encrypted.");
    }

    [Fact]
    public void Encrypt_TheSameBytesTwice_TakesANewNonce()
    {
        using var key = SecretKey.New();

        var first = Convert.FromBase64String(EncryptedValue.Encrypt(key, Text, Purpose.Group, "8f3a1c", "2"));
        var second = Convert.FromBase64String(EncryptedValue.Encrypt(key, Text, Purpose.Group, "8f3a1c", "2"));

        Assert.False(first.AsSpan(1, Aes256Gcm.NonceSize).SequenceEqual(second.AsSpan(1, Aes256Gcm.NonceSize)), "The nonce was used twice.");
        Assert.False(first.SequenceEqual(second), "Two encryptions gave the same value.");
    }

    [Fact]
    public void Decrypt_AChangedByteAnywhere_Fails()
    {
        using var key = SecretKey.New();
        var bytes = Convert.FromBase64String(EncryptedValue.Encrypt(key, Text, Purpose.Group, "8f3a1c", "2"));

        // The version, every byte of the nonce, the ciphertext and the tag.
        for (var i = 0; i < bytes.Length; i++)
        {
            var changed = (byte[])bytes.Clone();
            changed[i] ^= 0x01;

            Assert.Throws<CryptographicException>(() => EncryptedValue.Decrypt(key, Convert.ToBase64String(changed), Purpose.Group, "8f3a1c", "2"));
        }
    }

    [Fact]
    public void Decrypt_WithAnotherPurpose_Fails()
    {
        using var key = SecretKey.New();

        var value = EncryptedValue.Encrypt(key, Text, Purpose.Check, "2");

        Assert.Throws<CryptographicException>(() => EncryptedValue.Decrypt(key, value, Purpose.LevelKey, "2"));
    }

    [Theory]
    [InlineData("8f3a1c", "3")]
    [InlineData("8f3a1d", "2")]
    [InlineData("8f3a1c")]
    [InlineData("8f3a1c", "2", "")]
    // The parts carry their lengths, so the same characters split otherwise are another context.
    [InlineData("8f3a1c2")]
    [InlineData("8f3a1", "c2")]
    public void Decrypt_WithAnotherContext_Fails(params string[] context)
    {
        using var key = SecretKey.New();

        var value = EncryptedValue.Encrypt(key, Text, Purpose.Group, "8f3a1c", "2");

        Assert.Throws<CryptographicException>(() => EncryptedValue.Decrypt(key, value, Purpose.Group, context));
    }

    // Written by the code of M3.1 and kept as it is: the values on the share stay readable when a later format comes.
    [Fact]
    public void Decrypt_AValueOfFormat1WrittenEarlier_StillDecrypts()
    {
        using var key = SecretKey.FromBytes(Enumerable.Range(0, SecretKey.Size).Select(i => (byte)i).ToArray());

        var plaintext = EncryptedValue.Decrypt(key, "AXjwinuq6iHd6VkgqznsXcPCWXqrPJaw9582a+NZ4HbXJMfWB9lNUCpn0DX++kvL4CBe", Purpose.Group, "demo01", "1");

        Assert.Equal("demo value of format 1", Encoding.UTF8.GetString(plaintext));
    }

    [Fact]
    public void Encrypt_AContextWithALoneSurrogate_IsRefused()
    {
        using var key = SecretKey.New();

        // As U+FFFD, the two halves would be one context.
        Assert.ThrowsAny<ArgumentException>(() => EncryptedValue.Encrypt(key, Text, Purpose.Group, "a" + (char)0xD800, "2"));
        Assert.ThrowsAny<ArgumentException>(() => EncryptedValue.Encrypt(key, Text, Purpose.Group, "a" + (char)0xDC00, "2"));
    }

    [Fact]
    public void Decrypt_WithAnotherKey_Fails()
    {
        using var key = SecretKey.New();
        using var another = SecretKey.New();

        var value = EncryptedValue.Encrypt(key, Text, Purpose.Group, "8f3a1c", "2");

        Assert.Throws<CryptographicException>(() => EncryptedValue.Decrypt(another, value, Purpose.Group, "8f3a1c", "2"));
    }

    [Theory]
    [MemberData(nameof(NotValues))]
    public void Decrypt_NotAValueOfAKnownFormat_Fails(string value)
    {
        // The key of the values, so each fails for its format, not for the key.
        using var key = SecretKey.FromBytes(new byte[SecretKey.Size]);

        var exception = Assert.Throws<CryptographicException>(() => EncryptedValue.Decrypt(key, value, Purpose.Group, "8f3a1c", "2"));

        Assert.True(value.Length == 0 || !exception.Message.Contains(value, StringComparison.Ordinal), "The message repeats the value.");
    }

    public static TheoryData<string> NotValues()
    {
        const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
        using var key = SecretKey.FromBytes(new byte[SecretKey.Size]);
        var good = EncryptedValue.Encrypt(key, Text, Purpose.Group, "8f3a1c", "2");
        var bytes = Convert.FromBase64String(good);
        Assert.True(EncryptedValue.Decrypt(key, good, Purpose.Group, "8f3a1c", "2").SequenceEqual(Text), "The value the others are made of does not decrypt.");

        // The value of the 30 bytes of the text has 59 bytes, so it ends with one "=", and the character before it carries
        // 4 bits of the last byte and 2 that must be zero.
        var lastBits = good[..^2] + Alphabet[Alphabet.IndexOf(good[^2], StringComparison.Ordinal) + 1] + "=";

        return new TheoryData<string>
        {
            "",
            "not base64!",
            // Base64 that is not in its one form: a space in it, and a bit set after the last byte.
            good[..8] + " " + good[8..],
            lastBits,
            // One byte too short: the version, the nonce and the tag of an empty value, less one.
            Convert.ToBase64String(bytes.AsSpan(0, EncryptedValue.MinSize - 1)),
            // An unknown version with the rest as it is.
            Convert.ToBase64String([0, .. bytes.AsSpan(1)]),
            Convert.ToBase64String([2, .. bytes.AsSpan(1)]),
        };
    }
}

using System.Security.Cryptography;
using System.Text;

namespace Kiler.Crypto.Tests;

public class AdminSealTests
{
    private static readonly byte[] Text = """{"account":"demo.user","displayName":"Demo User","level":2,"canEdit":true,"isAdmin":false}"""u8.ToArray();

    [Fact]
    public void Verify_ASealOfTheTextAndItsPurpose_IsTrue()
    {
        using var key = AdminKey.New();

        var seal = key.Seal(SealPurpose.UserFile, Text);

        Assert.True(AdminSeal.Verify(key.PublicKey, SealPurpose.UserFile, Text, seal));
    }

    // Every pair, so two purposes with the same label would fail here.
    [Theory]
    [MemberData(nameof(PairsOfPurposes))]
    public void Verify_WithAnotherPurpose_IsFalse(SealPurpose sealedAs, SealPurpose checkedAs)
    {
        using var key = AdminKey.New();

        var seal = key.Seal(sealedAs, Text);

        Assert.False(AdminSeal.Verify(key.PublicKey, checkedAs, Text, seal));
    }

    public static TheoryData<SealPurpose, SealPurpose> PairsOfPurposes()
    {
        var pairs = new TheoryData<SealPurpose, SealPurpose>();
        foreach (var one in Enum.GetValues<SealPurpose>())
        {
            foreach (var other in Enum.GetValues<SealPurpose>().Where(other => other != one))
            {
                pairs.Add(one, other);
            }
        }

        return pairs;
    }

    [Fact]
    public void Verify_AChangedByteOfTheText_IsFalse()
    {
        using var key = AdminKey.New();
        var seal = key.Seal(SealPurpose.Levels, Text);

        for (var at = 0; at < Text.Length; at++)
        {
            var changed = (byte[])Text.Clone();
            changed[at] ^= 0x01;
            Assert.False(AdminSeal.Verify(key.PublicKey, SealPurpose.Levels, changed, seal), $"A change of byte {at} of the text verifies.");
        }
    }

    [Fact]
    public void Verify_TheTextWithAByteLessOrMore_IsFalse()
    {
        using var key = AdminKey.New();
        var seal = key.Seal(SealPurpose.Settings, Text);

        Assert.False(AdminSeal.Verify(key.PublicKey, SealPurpose.Settings, Text.AsSpan(0, Text.Length - 1), seal));
        Assert.False(AdminSeal.Verify(key.PublicKey, SealPurpose.Settings, [.. Text, 0], seal));
        Assert.False(AdminSeal.Verify(key.PublicKey, SealPurpose.Settings, [], seal));
    }

    [Fact]
    public void Verify_AChangedByteOfTheSeal_IsFalse()
    {
        using var key = AdminKey.New();
        var seal = Convert.FromBase64String(key.Seal(SealPurpose.Template, Text));

        for (var at = 0; at < seal.Length; at++)
        {
            var changed = (byte[])seal.Clone();
            changed[at] ^= 0x01;
            Assert.False(AdminSeal.Verify(key.PublicKey, SealPurpose.Template, Text, Convert.ToBase64String(changed)), $"A change of byte {at} of the seal verifies.");
        }
    }

    [Fact]
    public void Verify_WithTheKeyOfAnotherKiler_IsFalse()
    {
        using var key = AdminKey.New();
        using var another = AdminKey.New();

        var seal = key.Seal(SealPurpose.UserFile, Text);

        Assert.False(AdminSeal.Verify(another.PublicKey, SealPurpose.UserFile, Text, seal));
    }

    [Theory]
    [MemberData(nameof(NotPublicKeys))]
    public void Verify_NotAPublicKeyOfP256InItsOneForm_IsFalse(string publicKey, string seal)
    {
        Assert.False(AdminSeal.Verify(publicKey, SealPurpose.Template, Text, seal));
    }

    public static TheoryData<string, string> NotPublicKeys() => TestKeys.NotPublicKeys(AdminSeal.SignedText(SealPurpose.Template, Text));

    [Theory]
    [MemberData(nameof(NotSeals))]
    public void Verify_NotASealOf64BytesInItsOneForm_IsFalse(string publicKey, string seal)
    {
        Assert.False(AdminSeal.Verify(publicKey, SealPurpose.Template, Text, seal));
    }

    public static TheoryData<string, string> NotSeals() => TestKeys.NotSignatures(AdminSeal.SignedText(SealPurpose.Template, Text));

    // A signature of a release is never a seal, though both are ECDSA over P-256: the label is the first part of each text.
    [Fact]
    public void Verify_ASignatureOfAReleaseWithTheSameKey_IsFalse()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKey = Convert.ToBase64String(ecdsa.ExportSubjectPublicKeyInfo());
        var hash = SHA256.HashData(Text);
        var signature = TestKeys.Sign(ecdsa, UpdateSignature.SignedText(new ReleaseVersion(1, 0, 0), hash));

        Assert.True(UpdateSignature.Verify(publicKey, new ReleaseVersion(1, 0, 0), hash, signature), "The signature of the release does not verify.");
        Assert.False(AdminSeal.Verify(publicKey, SealPurpose.Template, hash, signature));
        Assert.False(AdminSeal.Verify(publicKey, SealPurpose.Template, Text, signature));
    }

    // Written by the code of M3.9 and kept as it is: a file sealed before stays sealed when the code changes.
    [Fact]
    public void Verify_ASealWrittenEarlier_StillVerifies()
    {
        const string PublicKey = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE4kwPshobfjeb0oSkqowOhdDOv4M01octg/aoVi7iakbyDRVdDRDToOY0z+Fkl0lFcws68w6OMyWhMlAz+KTKFg==";
        const string Seal = "lEsPgvE2OqmF3/Asrml8vIzkb9+DgqdPbbjWuNHdXWrsKgAHMBAjyQk8ABTBu4fz/BKtTPwsVIyS/jSbaW00HA==";

        Assert.True(AdminSeal.Verify(PublicKey, SealPurpose.UserFile, Text, Seal));
        Assert.False(AdminSeal.Verify(PublicKey, SealPurpose.Levels, Text, Seal));
    }

    [Fact]
    public void Verify_NullKeyOrSeal_IsRefused()
    {
        using var key = AdminKey.New();
        var seal = key.Seal(SealPurpose.UserFile, Text);

        Assert.Throws<ArgumentNullException>(() => AdminSeal.Verify(null!, SealPurpose.UserFile, Text, seal));
        Assert.Throws<ArgumentNullException>(() => AdminSeal.Verify(key.PublicKey, SealPurpose.UserFile, Text, null!));
    }

    [Fact]
    public void SealAndVerify_AnUnknownPurpose_AreRefused()
    {
        using var key = AdminKey.New();
        var seal = key.Seal(SealPurpose.UserFile, Text);

        Assert.Throws<ArgumentOutOfRangeException>(() => key.Seal((SealPurpose)99, Text));
        Assert.Throws<ArgumentOutOfRangeException>(() => AdminSeal.Verify(key.PublicKey, (SealPurpose)99, Text, seal));
    }

    // M4.11: a model of Kiler.Core keeps the digest of the sealed text, not the text, and checks the seal with it.
    [Theory]
    [InlineData(SealPurpose.UserFile)]
    [InlineData(SealPurpose.Levels)]
    [InlineData(SealPurpose.Settings)]
    [InlineData(SealPurpose.Template)]
    public void VerifyDigest_OfASealOfTheTextAndItsPurpose_IsTrue(SealPurpose purpose)
    {
        using var key = AdminKey.New();

        var seal = key.Seal(purpose, Text);

        Assert.True(AdminSeal.VerifyDigest(key.PublicKey, AdminSeal.Digest(purpose, Text), seal));
    }

    [Theory]
    [MemberData(nameof(PairsOfPurposes))]
    public void VerifyDigest_OfTheTextWithAnotherPurpose_IsFalse(SealPurpose sealedAs, SealPurpose checkedAs)
    {
        using var key = AdminKey.New();

        var seal = key.Seal(sealedAs, Text);

        Assert.True(AdminSeal.VerifyDigest(key.PublicKey, AdminSeal.Digest(sealedAs, Text), seal), "The seal does not verify with its own purpose.");
        Assert.False(AdminSeal.VerifyDigest(key.PublicKey, AdminSeal.Digest(checkedAs, Text), seal));
    }

    [Fact]
    public void VerifyDigest_OfAChangedText_IsFalse()
    {
        using var key = AdminKey.New();
        var seal = key.Seal(SealPurpose.Levels, Text);
        Assert.True(AdminSeal.VerifyDigest(key.PublicKey, AdminSeal.Digest(SealPurpose.Levels, Text), seal), "The seal does not verify.");

        for (var at = 0; at < Text.Length; at++)
        {
            var changed = (byte[])Text.Clone();
            changed[at] ^= 0x01;
            Assert.False(AdminSeal.VerifyDigest(key.PublicKey, AdminSeal.Digest(SealPurpose.Levels, changed), seal), $"A change of byte {at} of the text verifies.");
        }

        Assert.False(AdminSeal.VerifyDigest(key.PublicKey, AdminSeal.Digest(SealPurpose.Levels, Text.AsSpan(0, Text.Length - 1)), seal));
        Assert.False(AdminSeal.VerifyDigest(key.PublicKey, AdminSeal.Digest(SealPurpose.Levels, [.. Text, 0]), seal));
    }

    [Fact]
    public void VerifyDigest_AChangedByteOfTheSeal_OrTheKeyOfAnotherKiler_IsFalse()
    {
        using var key = AdminKey.New();
        using var another = AdminKey.New();
        var digest = AdminSeal.Digest(SealPurpose.Template, Text);
        var seal = Convert.FromBase64String(key.Seal(SealPurpose.Template, Text));
        Assert.True(AdminSeal.VerifyDigest(key.PublicKey, digest, Convert.ToBase64String(seal)), "The seal does not verify.");

        Assert.False(AdminSeal.VerifyDigest(another.PublicKey, digest, Convert.ToBase64String(seal)));
        for (var at = 0; at < seal.Length; at++)
        {
            var changed = (byte[])seal.Clone();
            changed[at] ^= 0x01;
            Assert.False(AdminSeal.VerifyDigest(key.PublicKey, digest, Convert.ToBase64String(changed)), $"A change of byte {at} of the seal verifies.");
        }
    }

    [Theory]
    [MemberData(nameof(NotPublicKeys))]
    public void VerifyDigest_NotAPublicKeyOfP256InItsOneForm_IsFalse(string publicKey, string seal)
    {
        Assert.False(AdminSeal.VerifyDigest(publicKey, AdminSeal.Digest(SealPurpose.Template, Text), seal));
    }

    [Theory]
    [MemberData(nameof(NotSeals))]
    public void VerifyDigest_NotASealOf64BytesInItsOneForm_IsFalse(string publicKey, string seal)
    {
        Assert.False(AdminSeal.VerifyDigest(publicKey, AdminSeal.Digest(SealPurpose.Template, Text), seal));
    }

    [Fact]
    public void VerifyDigest_ASealWrittenEarlier_StillVerifies()
    {
        const string PublicKey = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE4kwPshobfjeb0oSkqowOhdDOv4M01octg/aoVi7iakbyDRVdDRDToOY0z+Fkl0lFcws68w6OMyWhMlAz+KTKFg==";
        const string Seal = "lEsPgvE2OqmF3/Asrml8vIzkb9+DgqdPbbjWuNHdXWrsKgAHMBAjyQk8ABTBu4fz/BKtTPwsVIyS/jSbaW00HA==";

        Assert.True(AdminSeal.VerifyDigest(PublicKey, AdminSeal.Digest(SealPurpose.UserFile, Text), Seal));
        Assert.False(AdminSeal.VerifyDigest(PublicKey, AdminSeal.Digest(SealPurpose.Levels, Text), Seal));
    }

    [Theory]
    [InlineData(SealPurpose.UserFile)]
    [InlineData(SealPurpose.Template)]
    public void Digest_IsTheSha256OfTheSignedText(SealPurpose purpose)
    {
        Assert.Equal(SHA256.HashData(AdminSeal.SignedText(purpose, Text)), AdminSeal.Digest(purpose, Text));
    }

    [Fact]
    public void DigestAndVerifyDigest_WhatIsNotTheirs_IsRefused()
    {
        using var key = AdminKey.New();
        var seal = key.Seal(SealPurpose.UserFile, Text);
        var digest = AdminSeal.Digest(SealPurpose.UserFile, Text);

        Assert.Throws<ArgumentOutOfRangeException>(() => AdminSeal.Digest((SealPurpose)99, Text));
        Assert.Throws<ArgumentNullException>(() => AdminSeal.VerifyDigest(null!, digest, seal));
        Assert.Throws<ArgumentNullException>(() => AdminSeal.VerifyDigest(key.PublicKey, digest, null!));
        Assert.Throws<ArgumentException>(() => AdminSeal.VerifyDigest(key.PublicKey, digest.AsSpan(0, 31), seal));
        Assert.Throws<ArgumentException>(() => AdminSeal.VerifyDigest(key.PublicKey, [.. digest, 0], seal));
        Assert.Throws<ArgumentException>(() => AdminSeal.VerifyDigest(key.PublicKey, [], seal));
    }

    // The format of the sealed text: the format byte, then the label "seal", the label of the purpose and the text, each with
    // its length before it in 4 bytes, big-endian, as the associated data of an encrypted value.
    [Fact]
    public void SignedText_IsTheFormatTheLabelThePurposeAndTheTextEachWithItsLength()
    {
        byte[] expected =
        [
            1,
            0, 0, 0, 4, .. "seal"u8,
            0, 0, 0, 8, .. "template"u8,
            0, 0, 0, 2, .. "{}"u8,
        ];

        Assert.Equal(expected, AdminSeal.SignedText(SealPurpose.Template, "{}"u8));
    }

    // The labels are part of the format: a seal made before must verify after a rename of the enum.
    [Theory]
    [InlineData(SealPurpose.UserFile, "user")]
    [InlineData(SealPurpose.Levels, "levels")]
    [InlineData(SealPurpose.Settings, "settings")]
    [InlineData(SealPurpose.Template, "template")]
    public void SignedText_HasTheLabelOfItsPurpose(SealPurpose purpose, string label)
    {
        byte[] expected = [1, 0, 0, 0, 4, .. "seal"u8, 0, 0, 0, (byte)label.Length, .. Encoding.UTF8.GetBytes(label), 0, 0, 0, 0];

        Assert.Equal(expected, AdminSeal.SignedText(purpose, []));
    }
}

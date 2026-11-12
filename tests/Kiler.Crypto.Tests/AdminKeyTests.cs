using System.Security.Cryptography;

namespace Kiler.Crypto.Tests;

public class AdminKeyTests
{
    private static readonly byte[] Text = """{"account":"demo.admin","level":3,"canEdit":true,"isAdmin":true}"""u8.ToArray();

    [Fact]
    public void New_GivesAKeyOfP256WhosePublicKeyIsItsSubjectPublicKeyInfo()
    {
        using var key = AdminKey.New();

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
        using var one = AdminKey.New();
        using var two = AdminKey.New();

        Assert.NotEqual(one.PublicKey, two.PublicKey);
    }

    [Fact]
    public void Seal_TwiceTheSameText_GivesTwoSealsThatBothVerify()
    {
        using var key = AdminKey.New();

        var first = key.Seal(SealPurpose.UserFile, Text);
        var second = key.Seal(SealPurpose.UserFile, Text);

        Assert.NotEqual(first, second);
        Assert.True(AdminSeal.Verify(key.PublicKey, SealPurpose.UserFile, Text, first));
        Assert.True(AdminSeal.Verify(key.PublicKey, SealPurpose.UserFile, Text, second));
    }

    [Fact]
    public void Seal_AfterDispose_Throws()
    {
        var key = AdminKey.New();
        key.Dispose();

        Assert.Throws<ObjectDisposedException>(() => key.Seal(SealPurpose.UserFile, Text));
    }

    [Fact]
    public void LockOfTheLevelKeys_WithADisposedAdminKey_Throws()
    {
        var key = AdminKey.New();
        key.Dispose();

        Assert.Throws<ObjectDisposedException>(() => LevelKeys.Lock(new Dictionary<int, SecretKey>(), key.PublicKey, key, "demo password"));
    }

    [Fact]
    public void ToString_IsTheNameOfTheType()
    {
        using var key = AdminKey.New();

        Assert.Equal(typeof(AdminKey).FullName, key.ToString());
    }
}

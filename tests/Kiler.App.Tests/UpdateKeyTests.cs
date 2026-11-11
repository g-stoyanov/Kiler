using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;

namespace Kiler.App.Tests;

public class UpdateKeyTests
{
    [Fact]
    public void PublicKey_IsAPublicKeyOfP256InItsOneBase64Form()
    {
        var bytes = Convert.FromBase64String(UpdateKey.PublicKey);
        Assert.Equal(UpdateKey.PublicKey, Convert.ToBase64String(bytes));

        using var key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(bytes, out var read);
        Assert.Equal(bytes.Length, read);
        var curve = key.ExportParameters(includePrivateParameters: false).Curve;
        Assert.True(curve.IsNamed, "The curve of the key is not named.");
        Assert.Equal(ECCurve.NamedCurves.nistP256.Oid.Value, curve.Oid.Value);
    }

    /// <summary>
    /// The public keys of Kiler.Crypto.Tests (M3.4): the key file of <c>SigningKeyTests</c>, which unlocks for anyone, as its
    /// password is in the source; the key of the signature written earlier in <c>UpdateSignatureTests</c>; and the key of the
    /// test vectors of RFC 6979, A.2.5, whose private key the RFC gives.
    /// </summary>
    public static TheoryData<string> KeysOfTheTests
    {
        get
        {
            const string Ux = "60FED4BA255A9D31C961EB74C6356D68C049B8923B61FA6CE669622E60F29FB6";
            const string Uy = "7903FE1008B8BC99A41AE9E95628BC64F2F1B20C2D7E9F5177A3C294D4462299";
            using var rfc6979 = ECDsa.Create(new ECParameters
            {
                Curve = ECCurve.NamedCurves.nistP256,
                Q = new ECPoint { X = Convert.FromHexString(Ux), Y = Convert.FromHexString(Uy) },
            });
            return
            [
                "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEKos/QtaqldBnRN009NliUySK5+DIxOI5yFR2LSrUXU1b2Xie1kt6+HE8jFjPfobYAU6pxXzs3lNEe+rGgnCcrQ==",
                "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEe1i5Nq9CQ898C4HDsiHtZT+meaVA12hjHrr2urCdo4xYx1xMulkBKjNaC+Fc0yp7Dg4VwW8Hs7x5f8nXwlVLYg==",
                Convert.ToBase64String(rfc6979.ExportSubjectPublicKeyInfo()),
            ];
        }
    }

    [Theory]
    [MemberData(nameof(KeysOfTheTests))]
    public void PublicKey_IsNoKeyOfTheTests(string keyOfTheTests)
    {
        // Public keys, so the message may show them.
        Assert.NotEqual(UpdateKey.PublicKey, keyOfTheTests);
    }

    [Fact]
    public void KilerApp_NeverUsesTheSigningKey()
    {
        using var pe = new PEReader(File.OpenRead(typeof(UpdateKey).Assembly.Location));
        var metadata = pe.GetMetadataReader();
        var types = metadata.TypeReferences
            .Select(handle => metadata.GetTypeReference(handle))
            .Select(type => $"{metadata.GetString(type.Namespace)}.{metadata.GetString(type.Name)}")
            .ToList();

        Assert.Contains(types, type => type.StartsWith("Kiler.Core.", StringComparison.Ordinal));
        Assert.DoesNotContain("Kiler.Crypto.SigningKey", types);
    }
}

using System.Security.Cryptography;
using System.Text;
using Kiler.Crypto;

namespace Kiler.Core.Tests;

public class VersionFileTests
{
    private static readonly byte[] Hash = SHA256.HashData("demo Kiler.exe"u8);

    [Fact]
    public void ToJson_GivesTheVersionTheHashAsLowercaseHexAndTheSignature()
    {
        var file = new VersionFile(new ReleaseVersion(1, 10, 2), Hash, "c2ln+mF0d/Jl");

        var expected = $$"""
            {
              "version": "1.10.2",
              "sha256": "{{Convert.ToHexStringLower(Hash)}}",
              "signature": "c2ln+mF0d/Jl"
            }

            """.ReplaceLineEndings("\n");
        Assert.Equal(expected, Encoding.UTF8.GetString(file.ToJson()));
        Assert.Equal(Convert.ToHexStringLower(Hash), file.Sha256);
    }

    [Fact]
    public void New_WithAHashOfAnotherLength_Throws()
    {
        Assert.Throws<ArgumentException>(() => new VersionFile(new ReleaseVersion(1, 0, 0), Hash.AsSpan(1), "c2lnbmF0dXJl"));
    }

    [Fact]
    public void ToJson_HasNoByteOrderMark()
    {
        var json = new VersionFile(new ReleaseVersion(1, 0, 0), Hash, "c2lnbmF0dXJl").ToJson();

        Assert.Equal((byte)'{', json[0]);
    }
}

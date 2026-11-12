using System.Security.Cryptography;
using System.Text;
using Kiler.Crypto;

namespace Kiler.Core.Tests;

public class VersionFileTests
{
    private static readonly byte[] Hash = SHA256.HashData("demo Kiler.exe"u8);

    private static readonly string Hex = Convert.ToHexStringLower(Hash);

    [Fact]
    public void ToJson_GivesTheVersionTheHashAsLowercaseHexAndTheSignatures()
    {
        var file = new VersionFile(new ReleaseVersion(1, 10, 2), Hash, ["c2ln+mF0d/Jl"]);

        var expected = $$"""
            {
              "version": "1.10.2",
              "sha256": "{{Hex}}",
              "signatures": [
                "c2ln+mF0d/Jl"
              ]
            }

            """.ReplaceLineEndings("\n");
        Assert.Equal(expected, Encoding.UTF8.GetString(file.ToJson()));
        Assert.Equal(Hex, file.Sha256);
    }

    [Fact]
    public void New_WithAHashOfAnotherLength_Throws()
    {
        Assert.Throws<ArgumentException>(() => new VersionFile(new ReleaseVersion(1, 0, 0), Hash.AsSpan(1), ["c2lnbmF0dXJl"]));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public void New_WithoutOneOrTwoSignatures_Throws(int count)
    {
        string[] signatures = [.. Enumerable.Repeat("c2lnbmF0dXJl", count)];

        Assert.Throws<ArgumentException>(() => new VersionFile(new ReleaseVersion(1, 0, 0), Hash, signatures));
    }

    [Fact]
    public void New_WithANullSignature_Throws()
    {
        Assert.Throws<ArgumentException>(() => new VersionFile(new ReleaseVersion(1, 0, 0), Hash, ["c2lnbmF0dXJl", null!]));
    }

    [Fact]
    public void New_KeepsACopyOfTheSignatures()
    {
        string[] signatures = ["b25l", "dHdv"];
        var file = new VersionFile(new ReleaseVersion(1, 0, 0), Hash, signatures);

        signatures[0] = "Y2hhbmdlZA==";

        Assert.Equal(["b25l", "dHdv"], file.Signatures);
    }

    [Fact]
    public void ToJson_HasNoByteOrderMark()
    {
        var json = new VersionFile(new ReleaseVersion(1, 0, 0), Hash, ["c2lnbmF0dXJl"]).ToJson();

        Assert.Equal((byte)'{', json[0]);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void TryRead_ReadsWhatToJsonWrites(int count)
    {
        string[] signatures = [.. new[] { "b2xk+a2V5", "bmV3/2tleQ==" }.Take(count)];
        var written = new VersionFile(new ReleaseVersion(2, 0, 11), Hash, signatures);

        Assert.True(VersionFile.TryRead(written.ToJson(), out var read, out var invalid), invalid);

        Assert.Equal(new ReleaseVersion(2, 0, 11), read.Version);
        Assert.Equal(Hex, read.Sha256);
        Assert.Equal(signatures, read.Signatures);
    }

    [Fact]
    public void TryRead_AByteOrderMarkAtTheStart_IsSkipped()
    {
        byte[] json = [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(Json("\"1.0.1\"", $"\"{Hex}\"", "[\"c2lnbmF0dXJl\"]"))];

        Assert.True(VersionFile.TryRead(json, out var read, out var invalid), invalid);

        Assert.Equal(new ReleaseVersion(1, 0, 1), read.Version);
    }

    [Fact]
    public void TryRead_AMemberItDoesNotKnow_IsSkipped()
    {
        var json = Encoding.UTF8.GetBytes($$$"""{"version": "1.0.1", "sha256": "{{{Hex}}}", "signatures": ["c2lnbmF0dXJl"], "note": {"a": [1]}}""");

        Assert.True(VersionFile.TryRead(json, out _, out var invalid), invalid);
    }

    [Fact]
    public void TryRead_TheLargestFile_IsRead()
    {
        var json = Padded(VersionFile.MaxSize);

        Assert.True(VersionFile.TryRead(json, out _, out var invalid), invalid);
    }

    [Fact]
    public void TryRead_AFileLargerThanTheLimit_DoesNotFit()
    {
        var json = Padded(VersionFile.MaxSize + 1);

        Assert.False(VersionFile.TryRead(json, out var file, out var invalid));

        Assert.Null(file);
        Assert.Equal($"It is larger than {VersionFile.MaxSize} bytes.", invalid);
    }

    /// <summary>
    /// Contents that do not fit, each with its reason; none of them repeats a value of the file. The values are those of a
    /// file that anyone who writes the shared folder may put there.
    /// </summary>
    public static TheoryData<string, string> NotVersionFiles
    {
        get
        {
            const string Version = "\"1.0.1\"";
            const string Signatures = "[\"c2lnbmF0dXJl\"]";
            var sha256 = $"\"{Hex}\"";
            const string NotJson = "It is not JSON, or a value does not have the type it must have.";
            return new()
            {
                { string.Empty, NotJson },
                { "{", NotJson },
                { "not json", NotJson },
                { "[]", NotJson },
                { "\"1.0.1\"", NotJson },
                { $"{{\"version\": {Version}, \"sha256\": {sha256}, \"signatures\": {Signatures}}} {{}}", NotJson },
                { "null", "It is not a JSON object." },
                { $"{{\"sha256\": {sha256}, \"signatures\": {Signatures}}}", "version is missing." },
                { Json("null", sha256, Signatures), "version is missing." },
                { Json("1", sha256, Signatures), NotJson },
                { Json("\"1.0\"", sha256, Signatures), "version is not major.minor.patch." },
                { Json("\"01.0.0\"", sha256, Signatures), "version is not major.minor.patch." },
                { Json("\"1.0.0+abc\"", sha256, Signatures), "version is not major.minor.patch." },
                { $"{{\"version\": {Version}, \"signatures\": {Signatures}}}", "sha256 is missing." },
                { Json(Version, $"\"{Hex.ToUpperInvariant()}\"", Signatures), "sha256 is not a SHA-256 as lowercase hex." },
                { Json(Version, $"\"{Hex[1..]}\"", Signatures), "sha256 is not a SHA-256 as lowercase hex." },
                { Json(Version, $"\"{Hex}0\"", Signatures), "sha256 is not a SHA-256 as lowercase hex." },
                { Json(Version, $"\"{Hex[1..]}g\"", Signatures), "sha256 is not a SHA-256 as lowercase hex." },
                { Json(Version, $"\"{Hex[1..]} \"", Signatures), "sha256 is not a SHA-256 as lowercase hex." },
                { $"{{\"version\": {Version}, \"sha256\": {sha256}}}", "signatures is missing." },
                { $"{{\"version\": {Version}, \"sha256\": {sha256}, \"signature\": \"c2lnbmF0dXJl\"}}", "signatures is missing." },
                { Json(Version, sha256, "\"c2lnbmF0dXJl\""), NotJson },
                { Json(Version, sha256, "[1]"), NotJson },
                { Json(Version, sha256, "[]"), "signatures does not hold from 1 to 2 signatures." },
                { Json(Version, sha256, "[\"b25l\", \"dHdv\", \"dGhyZWU=\"]"), "signatures does not hold from 1 to 2 signatures." },
                { Json(Version, sha256, "[\"b25l\", null]"), "signatures holds a null." },
                { $"{{\"Version\": {Version}, \"sha256\": {sha256}, \"signatures\": {Signatures}}}", "version is missing." },
            };
        }
    }

    [Theory]
    [MemberData(nameof(NotVersionFiles))]
    public void TryRead_AContentThatDoesNotFit_GivesItsReason(string json, string reason)
    {
        Assert.False(VersionFile.TryRead(Encoding.UTF8.GetBytes(json), out var file, out var invalid));

        Assert.Null(file);
        Assert.Equal(reason, invalid);
    }

    [Fact]
    public void TryRead_BytesThatAreNotUtf8_DoNotFit()
    {
        byte[] json = [.. Encoding.UTF8.GetBytes("{\"version\": \"1.0."), 0xFF, .. Encoding.UTF8.GetBytes("1\"}")];

        Assert.False(VersionFile.TryRead(json, out _, out var invalid));

        Assert.Equal("It is not JSON, or a value does not have the type it must have.", invalid);
    }

    private static string Json(string version, string sha256, string signatures) =>
        $"{{\"version\": {version}, \"sha256\": {sha256}, \"signatures\": {signatures}}}";

    /// <summary>A file that fits, with spaces at its end up to <paramref name="size"/> bytes.</summary>
    private static byte[] Padded(int size)
    {
        var json = Encoding.UTF8.GetBytes(Json("\"1.0.1\"", $"\"{Hex}\"", "[\"c2lnbmF0dXJl\"]"));
        return [.. json, .. Enumerable.Repeat((byte)' ', size - json.Length)];
    }
}

using System.Text.Encodings.Web;
using System.Text.Json;
using Kiler.Crypto;

namespace Kiler.Core;

/// <summary>
/// <c>version.json</c> of a release, next to <c>Kiler.exe</c> in the shared folder (design 4, M3 step 11): the version of
/// the exe, its SHA-256 as lowercase hex, and the signature of the two (<see cref="UpdateSignature"/>). The release tool
/// writes it (M3.5), and a local copy reads it (M3.6). The copy checks the signature with the hash of its own copy of the
/// exe, never with the hash text of the file: that is there for people, to compare with the SHA-256 announced outside the
/// share (docs/release.md).
/// </summary>
public sealed class VersionFile
{
    public const string Name = "version.json";

    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Indented = true,
        NewLine = "\n",
    };

    public VersionFile(ReleaseVersion version, ReadOnlySpan<byte> sha256, string signature)
    {
        ArgumentNullException.ThrowIfNull(signature);
        if (sha256.Length != UpdateSignature.HashSize)
        {
            throw new ArgumentException($"A SHA-256 has {UpdateSignature.HashSize} bytes.", nameof(sha256));
        }

        Version = version;
        Sha256 = Convert.ToHexStringLower(sha256);
        Signature = signature;
    }

    public ReleaseVersion Version { get; }

    /// <summary>The SHA-256 of the exe as lowercase hex, as the signed text has it.</summary>
    public string Sha256 { get; }

    /// <summary>The signature of <see cref="Version"/> and the SHA-256, base64.</summary>
    public string Signature { get; }

    /// <summary>
    /// The UTF-8 of the file, indented, with line feeds and one at the end, and no byte order mark. As the documents of the
    /// data layer, only what JSON itself needs is escaped, so the "+" of the base64 stays as it is for people to read.
    /// </summary>
    public byte[] ToJson()
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            var json = new VersionJson { Version = Version.ToString(), Sha256 = Sha256, Signature = Signature };
            JsonSerializer.Serialize(writer, json, CoreJsonContext.Default.VersionJson);
        }

        buffer.WriteByte((byte)'\n');
        return buffer.ToArray();
    }
}

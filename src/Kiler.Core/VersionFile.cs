using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Text.Encodings.Web;
using System.Text.Json;
using Kiler.Crypto;

namespace Kiler.Core;

/// <summary>
/// <c>version.json</c> of a release, next to <c>Kiler.exe</c> in the shared folder (design 4, M3 step 11): the version of
/// the exe, its SHA-256 as lowercase hex, and one or two signatures of the two (<see cref="UpdateSignature"/>). The release
/// tool writes it (M3.5), and a local copy reads it (M3.6) and takes the release when one of the signatures verifies with
/// the key built into the copy: during a change of the signing key, a release carries a signature of the old key and one of
/// the new (decided by the user in M3.6). The copy checks a signature with the hash of its own copy of the exe, never with
/// the hash text of the file: that is there for people, to compare with the SHA-256 announced outside the share
/// (docs/release.md).
/// </summary>
public sealed class VersionFile
{
    public const string Name = "version.json";

    /// <summary>The largest <c>version.json</c> that a copy reads; one with two signatures has some 400 bytes.</summary>
    public const int MaxSize = 4096;

    /// <summary>The largest <c>Kiler.exe</c> that the release tool signs and a copy takes; Kiler.exe has some 22 MB.</summary>
    public const int MaxExeSize = 256 * 1024 * 1024;

    /// <summary>The signatures of a release: one, and two during a change of the signing key.</summary>
    public const int MaxSignatures = 2;

    private const int HexLength = UpdateSignature.HashSize * 2;

    /// <summary>The byte order mark of UTF-8.</summary>
    private static ReadOnlySpan<byte> ByteOrderMark => [0xEF, 0xBB, 0xBF];

    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Indented = true,
        NewLine = "\n",
    };

    /// <exception cref="ArgumentException">
    /// The hash is not of <see cref="UpdateSignature.HashSize"/> bytes, or there are not from 1 to
    /// <see cref="MaxSignatures"/> signatures, or one is null.
    /// </exception>
    public VersionFile(ReleaseVersion version, ReadOnlySpan<byte> sha256, IReadOnlyList<string> signatures)
    {
        ArgumentNullException.ThrowIfNull(signatures);
        if (sha256.Length != UpdateSignature.HashSize)
        {
            throw new ArgumentException($"A SHA-256 has {UpdateSignature.HashSize} bytes.", nameof(sha256));
        }

        if (InvalidSignatures(signatures) is { } reason)
        {
            throw new ArgumentException(reason, nameof(signatures));
        }

        Version = version;
        Sha256 = Convert.ToHexStringLower(sha256);
        Signatures = Array.AsReadOnly([.. signatures]);
    }

    public ReleaseVersion Version { get; }

    /// <summary>The SHA-256 of the exe as lowercase hex, as the signed text has it.</summary>
    public string Sha256 { get; }

    /// <summary>The signatures of <see cref="Version"/> and the SHA-256, each base64; a copy takes the release when one verifies.</summary>
    public ReadOnlyCollection<string> Signatures { get; }

    /// <summary>
    /// The file of <paramref name="json"/>, or why it does not fit. Anyone who can write the shared folder can write the file,
    /// so it is read only up to <see cref="MaxSize"/>, and a reason names a field but never repeats a value of the file. A
    /// byte order mark of UTF-8 at the start is skipped, as an editor may save one. Whether a signature is base64 is left to
    /// the check of the signature.
    /// </summary>
    public static bool TryRead(ReadOnlySpan<byte> json, [NotNullWhen(true)] out VersionFile? file, [NotNullWhen(false)] out string? invalid)
    {
        file = null;
        if (json.Length > MaxSize)
        {
            invalid = $"It is larger than {MaxSize} bytes.";
            return false;
        }

        if (json.StartsWith(ByteOrderMark))
        {
            json = json[ByteOrderMark.Length..];
        }

        VersionJson? content;
        try
        {
            content = JsonSerializer.Deserialize(json, CoreJsonContext.Default.VersionJson);
        }
        // The message is not kept: its path can hold a name from the share.
        catch (JsonException)
        {
            invalid = "It is not JSON, or a value does not have the type it must have.";
            return false;
        }

        invalid = Invalid(content, out var version);
        if (invalid is not null)
        {
            return false;
        }

        file = new VersionFile(version, Convert.FromHexString(content!.Sha256!), [.. content.Signatures!.Select(signature => signature!)]);
        return true;
    }

    /// <summary>
    /// The UTF-8 of the file, indented, with line feeds and one at the end, and no byte order mark. As the documents of the
    /// data layer, only what JSON itself needs is escaped, so the "+" of the base64 stays as it is for people to read.
    /// </summary>
    public byte[] ToJson()
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            var json = new VersionJson { Version = Version.ToString(), Sha256 = Sha256, Signatures = [.. Signatures] };
            JsonSerializer.Serialize(writer, json, CoreJsonContext.Default.VersionJson);
        }

        buffer.WriteByte((byte)'\n');
        return buffer.ToArray();
    }

    /// <summary>Why the content does not fit, or null, with its version.</summary>
    private static string? Invalid(VersionJson? content, out ReleaseVersion version)
    {
        version = default;
        if (content is null)
        {
            return "It is not a JSON object.";
        }

        if (content.Version is null)
        {
            return "version is missing.";
        }

        if (!ReleaseVersion.TryParse(content.Version, out version))
        {
            return "version is not major.minor.patch.";
        }

        if (content.Sha256 is null)
        {
            return "sha256 is missing.";
        }

        if (content.Sha256.Length != HexLength || !content.Sha256.All(c => char.IsAsciiDigit(c) || c is >= 'a' and <= 'f'))
        {
            return "sha256 is not a SHA-256 as lowercase hex.";
        }

        return content.Signatures is null ? "signatures is missing." : InvalidSignatures(content.Signatures);
    }

    private static string? InvalidSignatures(IReadOnlyList<string?> signatures)
    {
        if (signatures.Count is < 1 or > MaxSignatures)
        {
            return $"signatures does not hold from 1 to {MaxSignatures} signatures.";
        }

        return signatures.Contains(null) ? "signatures holds a null." : null;
    }
}

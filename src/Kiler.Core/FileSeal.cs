using System.Buffers;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kiler.Crypto;
using Kiler.Storage;

namespace Kiler.Core;

/// <summary>
/// The admin seal of a file sealed whole (M4.11): <c>levels.json</c>, <c>settings.json</c> or a template, with the digest of
/// the text that its <c>stamp</c> covers (<see cref="TextOf(DocumentKey, int, JsonObject)"/>, <see cref="AdminSeal.Digest"/>),
/// made when the file is read or sealed, as a model keeps no node of its content (core.md, "Models"). The digest, not the
/// text, as the text of a file can be several times its size (M4.11 security review). The seal of a user file covers only
/// its rights, which the model has, so <see cref="UserFile"/> makes its text when it checks
/// (<see cref="TextOf(string, string, int, bool, bool)"/>).
/// <para>
/// The text is compact JSON in UTF-8, written with <see cref="JavaScriptEncoder.Default"/>, which escapes every character
/// outside ASCII, so it does not hang on the Unicode tables of a version of .NET; and it is written from the values, so it
/// does not hang on the spaces and the escapes of the file either (decided by the user in M4.11, docs/tasks/M4.md).
/// </para>
/// </summary>
internal sealed class FileSeal
{
    /// <summary>The member of the seal in the JSON of a file.</summary>
    public const string Member = "stamp";

    private const string IdName = "id";
    private const string VersionName = "version";

    private static readonly JsonWriterOptions TextOptions = new() { Encoder = JavaScriptEncoder.Default };

    private readonly byte[] _digest;

    private FileSeal(byte[] digest, string stamp)
    {
        _digest = digest;
        Stamp = stamp;
    }

    /// <summary>The <c>stamp</c> of the file, which may not be a seal at all.</summary>
    public string Stamp { get; }

    /// <summary>Whether <see cref="Stamp"/> is a seal of the text by the admin key of <paramref name="publicKey"/>.</summary>
    public bool IsBy(string publicKey) => AdminSeal.VerifyDigest(publicKey, _digest, Stamp);

    /// <summary>
    /// The seal of a file as it is read, every member of <paramref name="content"/> but <c>stamp</c> as
    /// <paramref name="version"/> of <paramref name="key"/>: <c>null</c> without a <c>stamp</c>.
    /// </summary>
    public static FileSeal? Read(SealPurpose purpose, DocumentKey key, int version, JsonObject content, string? stamp) =>
        stamp is null ? null : new(AdminSeal.Digest(purpose, TextOf(key, version, content)), stamp);

    /// <summary>
    /// A new seal of <paramref name="content"/>, the content without <c>stamp</c> that will be written as
    /// <paramref name="version"/> of <paramref name="key"/>.
    /// </summary>
    /// <exception cref="ObjectDisposedException">The admin key is disposed.</exception>
    public static FileSeal Make(AdminKey adminKey, SealPurpose purpose, DocumentKey key, int version, JsonObject content)
    {
        var text = TextOf(key, version, content);
        return new(AdminSeal.Digest(purpose, text), adminKey.Seal(purpose, text));
    }

    /// <summary>
    /// The text of a file sealed whole: an object of its <c>id</c> and <c>version</c>, then every member of the content but
    /// <c>stamp</c> in the order of the file, also those that the model does not know, so a member that a later Kiler adds
    /// does not break the seal for an older one. A member <c>id</c> or <c>version</c> of the content, in any case, is left
    /// out, as the layer leaves it out of the file (<see cref="DocumentFormat"/>).
    /// </summary>
    internal static byte[] TextOf(DocumentKey key, int version, JsonObject content)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentOutOfRangeException.ThrowIfLessThan(version, 1);
        return Write(writer =>
        {
            writer.WriteString(IdName, key.Id);
            writer.WriteNumber(VersionName, version);
            foreach (var (name, value) in content)
            {
                if (name == Member || name.Equals(IdName, StringComparison.OrdinalIgnoreCase) || name.Equals(VersionName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                writer.WritePropertyName(name);
                if (value is null)
                {
                    writer.WriteNullValue();
                }
                else
                {
                    value.WriteTo(writer);
                }
            }
        });
    }

    /// <summary>
    /// The text of a user file: its rights, never <c>salt</c>, <c>keys</c>, the admin key, <c>mustChangePassword</c> or its
    /// version, which its user changes without the private key (docs/tasks/M3.md, "What the seal covers").
    /// </summary>
    internal static byte[] TextOf(string account, string displayName, int level, bool canEdit, bool isAdmin) => Write(writer =>
    {
        writer.WriteString(IdName, account);
        writer.WriteString("displayName", displayName);
        writer.WriteNumber("level", level);
        writer.WriteBoolean("canEdit", canEdit);
        writer.WriteBoolean("isAdmin", isAdmin);
    });

    private static byte[] Write(Action<Utf8JsonWriter> members)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, TextOptions))
        {
            writer.WriteStartObject();
            members(writer);
            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }
}

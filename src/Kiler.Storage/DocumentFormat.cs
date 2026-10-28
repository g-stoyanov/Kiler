using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Unicode;

namespace Kiler.Storage;

/// <summary>
/// The bytes of a document file (M2.2): a JSON object in UTF-8 with <c>id</c> and <c>version</c> at the top. The layer reads
/// only these two and keeps the rest as it is; it sets <c>version</c> on every write, 1 for a new document and one more than
/// the version that a write changes. The content goes through <see cref="JsonNode"/>, which needs no reflection.
/// </summary>
public static class DocumentFormat
{
    /// <summary>The largest document file, 1 MiB.</summary>
    public const int MaxSize = 1024 * 1024;

    /// <summary>
    /// The deepest nesting of objects and arrays in a document file, the document itself included. Half of the serializer's
    /// default of 64, so an upper layer can still put a document into a response of its own.
    /// </summary>
    public const int MaxDepth = 32;

    private const string IdName = "id";
    private const string VersionName = "version";
    private const int FirstVersion = 1;

    /// <summary>
    /// Indented with LF, without a byte order mark. The file is never put into a page as it is, so only what JSON itself
    /// needs is escaped, and Cyrillic and <c>&lt;</c> stay readable in a text editor.
    /// </summary>
    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Indented = true,
        NewLine = "\n",
        MaxDepth = MaxDepth,
    };

    /// <summary>Strict JSON: no comments and no trailing commas (the defaults), and no name twice in one object.</summary>
    private static readonly JsonDocumentOptions ReaderOptions = new() { MaxDepth = MaxDepth, AllowDuplicateProperties = false };

    /// <summary>The bytes of a new document: version 1, whatever the content says.</summary>
    internal static byte[] New(DocumentKey key, JsonObject content) => Write(key, FirstVersion, content);

    /// <summary>
    /// The bytes of the version after <paramref name="changes"/>, whatever the content says. <see cref="int.MaxValue"/> has
    /// no version after it.
    /// </summary>
    internal static byte[] Next(DocumentKey key, int changes, JsonObject content)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(changes, FirstVersion);
        ArgumentOutOfRangeException.ThrowIfEqual(changes, int.MaxValue);
        return Write(key, changes + 1, content);
    }

    /// <summary>
    /// The document in <paramref name="bytes"/>, or why they are a damaged file of <paramref name="key"/>. Whether they are
    /// damaged, and why, comes from <see cref="ReadHead"/>, so the two never judge a file apart (M2.7c); only the content of a
    /// good file is then parsed into nodes.
    /// </summary>
    internal static DocumentRead Read(DocumentKey key, ReadOnlyMemory<byte> bytes)
    {
        var head = ReadHead(key, bytes);
        if (head.Damage is { } damage)
        {
            return DocumentRead.Damaged(damage);
        }

        // The head has checked the whole file with the same options, so the parse takes it.
        var document = JsonNode.Parse(WithoutBom(bytes).Span, documentOptions: ReaderOptions)!.AsObject();
        document.Remove(IdName);
        document.Remove(VersionName);
        return DocumentRead.Good(new Document(key, head.Version, document));
    }

    /// <summary>
    /// The <c>version</c> of the document in <paramref name="bytes"/>, or why they are a damaged file of
    /// <paramref name="key"/>, without the owner (M2.7c). The whole file is checked as strictly as a document is read, through
    /// <see cref="JsonDocument"/>, whose buffers are pooled and given back, and no node of its content is made: the local copy
    /// of an admin keeps the private folders of other users only for a restore (M2.9), and never parses their content.
    /// </summary>
    internal static DocumentHead ReadHead(DocumentKey key, ReadOnlyMemory<byte> bytes)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (bytes.Length > MaxSize)
        {
            return new DocumentHead(0, DocumentDamage.TooLarge, null);
        }

        // The parse leaves the strings as they are in the file, so bytes that are not UTF-8, or an escaped lone surrogate,
        // would fail only at the first read of that string, in an upper layer.
        bytes = WithoutBom(bytes);
        if (!Utf8.IsValid(bytes.Span))
        {
            return new DocumentHead(0, DocumentDamage.NotJson, null);
        }

        try
        {
            using var document = JsonDocument.Parse(bytes, ReaderOptions);
            return StringsDecode(bytes.Span) ? HeadOf(key, document.RootElement) : new DocumentHead(0, DocumentDamage.NotJson, null);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            // The check of names twice throws InvalidOperationException for a name that does not decode.
            return new DocumentHead(0, DocumentDamage.NotJson, null);
        }
    }

    /// <summary>Reads a document from a stream, as <see cref="TryReadBytes(Stream, out byte[])"/> reads it.</summary>
    internal static DocumentRead Read(DocumentKey key, Stream stream) =>
        TryReadBytes(stream, out var bytes) ? Read(key, bytes) : DocumentRead.Damaged(DocumentDamage.TooLarge);

    /// <summary>
    /// The bytes of a file as they are, or <c>false</c> when it is larger than <see cref="MaxSize"/>. It reads at most
    /// <see cref="MaxSize"/> + 1 bytes, so a file over the limit is not read whole. The size from the handle comes first, and
    /// such a file is not read at all; the limit still holds when the file grows while it is read, as an edit in place
    /// outside Kiler can make it.
    /// </summary>
    internal static bool TryReadBytes(Stream stream, out byte[] bytes) => TryReadBytes(stream, MaxSize, out bytes);

    /// <summary>
    /// The bytes of a file as they are, or <c>false</c> when it is larger than <paramref name="max"/>, at most
    /// <see cref="MaxSize"/>, as <see cref="TryReadBytes(Stream, out byte[])"/> reads them. A load reads a file so with the
    /// size its listing gave, which its budget counts (M2.7).
    /// </summary>
    internal static bool TryReadBytes(Stream stream, long max, out byte[] bytes)
    {
        bytes = [];
        max = Math.Min(max, MaxSize);
        var size = stream.Length;
        if (size > max)
        {
            return false;
        }

        // One byte more than the size, to see that the file has grown.
        var buffer = new byte[size + 1];
        var length = 0;
        int read;
        while ((read = stream.Read(buffer.AsSpan(length))) > 0)
        {
            length += read;
            if (length == buffer.Length)
            {
                if (length > max)
                {
                    return false;
                }

                Array.Resize(ref buffer, (int)Math.Min(buffer.Length * 2L, max + 1));
            }
        }

        bytes = buffer[..length];
        return true;
    }

    /// <summary>
    /// <c>id</c> and <c>version</c> first, then the content in its order without its own <c>id</c> and <c>version</c> in any
    /// case, and a line end after the object. Content that would make a damaged file, too large or nested too deep, is
    /// refused. A string that is not valid UTF-16 is written with U+FFFD in place of the lone surrogate.
    /// </summary>
    private static byte[] Write(DocumentKey key, int version, JsonObject content)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(content);
        var buffer = new ArrayBufferWriter<byte>();
        try
        {
            using var writer = new Utf8JsonWriter(buffer, WriterOptions);
            writer.WriteStartObject();
            writer.WriteString(IdName, key.Id);
            writer.WriteNumber(VersionName, version);
            foreach (var (name, value) in content)
            {
                if (name.Equals(IdName, StringComparison.OrdinalIgnoreCase) || name.Equals(VersionName, StringComparison.OrdinalIgnoreCase))
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

            writer.WriteEndObject();
        }
        catch (InvalidOperationException ex)
        {
            // The writer refuses nesting deeper than MaxDepth.
            throw new ArgumentException($"The content is nested deeper than {MaxDepth} levels.", nameof(content), ex);
        }

        buffer.Write("\n"u8);
        if (buffer.WrittenCount > MaxSize)
        {
            throw new ArgumentException($"The document would be larger than {MaxSize} bytes.", nameof(content));
        }

        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>RFC 8259 lets a reader skip a byte order mark. Kiler never writes one, a text editor can.</summary>
    private static ReadOnlyMemory<byte> WithoutBom(ReadOnlyMemory<byte> bytes) =>
        bytes.Span.StartsWith(Encoding.UTF8.Preamble) ? bytes[Encoding.UTF8.Preamble.Length..] : bytes;

    /// <summary>The head of a file of valid JSON: an object with <c>id</c> and <c>version</c>, as a document has them.</summary>
    private static DocumentHead HeadOf(DocumentKey key, JsonElement document)
    {
        if (document.ValueKind != JsonValueKind.Object)
        {
            return new DocumentHead(0, DocumentDamage.NotAnObject, null);
        }

        // An upper layer that reads names ignoring case would take "ID" or "Version" for the layer's own. Each name at the
        // top is read once, as a file can have any number of them.
        var (idVariant, versionVariant) = (false, false);
        foreach (var property in document.EnumerateObject())
        {
            var name = property.Name;
            idVariant |= name != IdName && name.Equals(IdName, StringComparison.OrdinalIgnoreCase);
            versionVariant |= name != VersionName && name.Equals(VersionName, StringComparison.OrdinalIgnoreCase);
        }

        if (!document.TryGetProperty(IdName, out var id) || id.ValueKind != JsonValueKind.String || id.GetString() != key.Id || idVariant)
        {
            return new DocumentHead(0, DocumentDamage.WrongId, null);
        }

        if (!document.TryGetProperty(VersionName, out var version) || version.ValueKind != JsonValueKind.Number
            || !version.TryGetInt32(out var number) || number < FirstVersion || versionVariant)
        {
            return new DocumentHead(0, DocumentDamage.WrongVersion, null);
        }

        return new DocumentHead(number, null, null);
    }

    /// <summary>Whether every escaped string and name decodes; the others are valid UTF-8 already.</summary>
    private static bool StringsDecode(ReadOnlySpan<byte> json)
    {
        var reader = new Utf8JsonReader(json, new JsonReaderOptions { MaxDepth = MaxDepth });
        try
        {
            while (reader.Read())
            {
                if ((reader.TokenType is JsonTokenType.String or JsonTokenType.PropertyName) && reader.ValueIsEscaped)
                {
                    reader.GetString();
                }
            }

            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}

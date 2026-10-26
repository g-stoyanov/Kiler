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

    /// <summary>The document in <paramref name="bytes"/>, or why they are a damaged file of <paramref name="key"/>.</summary>
    internal static DocumentRead Read(DocumentKey key, ReadOnlySpan<byte> bytes)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (bytes.Length > MaxSize)
        {
            return DocumentRead.Damaged(DocumentDamage.TooLarge);
        }

        // RFC 8259 lets a reader skip a byte order mark. Kiler never writes one, a text editor can.
        var bom = Encoding.UTF8.Preamble;
        if (bytes.StartsWith(bom))
        {
            bytes = bytes[bom.Length..];
        }

        // The parse leaves the strings as they are in the file, so bytes that are not UTF-8, or an escaped lone surrogate,
        // would fail only at the first read of that string, in an upper layer.
        if (!Utf8.IsValid(bytes) || !TryParse(bytes, out var node) || !StringsDecode(bytes))
        {
            return DocumentRead.Damaged(DocumentDamage.NotJson);
        }

        if (node is not JsonObject document)
        {
            return DocumentRead.Damaged(DocumentDamage.NotAnObject);
        }

        // An upper layer that reads names ignoring case would take "ID" or "Version" for the layer's own.
        if (!document.TryGetPropertyValue(IdName, out var id) || id is not JsonValue idValue
            || !idValue.TryGetValue<string>(out var idText) || idText != key.Id || HasCaseVariant(document, IdName))
        {
            return DocumentRead.Damaged(DocumentDamage.WrongId);
        }

        if (!document.TryGetPropertyValue(VersionName, out var version) || version is not JsonValue versionValue
            || !versionValue.TryGetValue<int>(out var number) || number < FirstVersion || HasCaseVariant(document, VersionName))
        {
            return DocumentRead.Damaged(DocumentDamage.WrongVersion);
        }

        document.Remove(IdName);
        document.Remove(VersionName);
        return DocumentRead.Good(new Document(key, number, document));
    }

    /// <summary>Reads a document from a stream, as <see cref="TryReadBytes"/> reads it.</summary>
    internal static DocumentRead Read(DocumentKey key, Stream stream) =>
        TryReadBytes(stream, out var bytes) ? Read(key, bytes) : DocumentRead.Damaged(DocumentDamage.TooLarge);

    /// <summary>
    /// The bytes of a file as they are, or <c>false</c> when it is larger than <see cref="MaxSize"/>. It reads at most
    /// <see cref="MaxSize"/> + 1 bytes, so a file over the limit is not read whole. The size from the handle comes first, and
    /// such a file is not read at all; the limit still holds when the file grows while it is read, as an edit in place
    /// outside Kiler can make it.
    /// </summary>
    internal static bool TryReadBytes(Stream stream, out byte[] bytes)
    {
        bytes = [];
        var size = stream.Length;
        if (size > MaxSize)
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
                if (length > MaxSize)
                {
                    return false;
                }

                Array.Resize(ref buffer, Math.Min(buffer.Length * 2, MaxSize + 1));
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

    /// <summary>Whether the object has a name that is <paramref name="name"/> in another case, such as <c>ID</c>.</summary>
    private static bool HasCaseVariant(JsonObject document, string name) =>
        document.Any(property => property.Key != name && property.Key.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Parses the bytes when they are valid JSON. The node is <c>null</c> for the JSON <c>null</c>.</summary>
    private static bool TryParse(ReadOnlySpan<byte> bytes, out JsonNode? node)
    {
        try
        {
            node = JsonNode.Parse(bytes, documentOptions: ReaderOptions);
            return true;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            // The check of names twice throws InvalidOperationException for a name that does not decode.
            node = null;
            return false;
        }
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

using System.Text.Json.Nodes;

namespace Kiler.Storage;

/// <summary>
/// A document that was read (M2.2): a JSON object with <c>id</c> and <c>version</c> at the top, of which the layer knows only
/// these two. The models of records, users, templates, stashes and settings come with M4 and M5 in Kiler.Core.
/// </summary>
public sealed class Document
{
    internal Document(DocumentKey key, int version, JsonObject content)
    {
        Key = key;
        Version = version;
        Content = content;
    }

    /// <summary>Where the document is; its <see cref="DocumentKey.Id"/> is the <c>id</c> in the file.</summary>
    public DocumentKey Key { get; }

    /// <summary>The <c>version</c> in the file, 1 or more. The layer sets it on every write.</summary>
    public int Version { get; }

    /// <summary>
    /// The object of the file without <c>id</c> and <c>version</c>, as it is, in its order. The layer keeps no reference to
    /// it, so it belongs to the caller.
    /// </summary>
    public JsonObject Content { get; }
}

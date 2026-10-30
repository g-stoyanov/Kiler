using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Nodes;
using Kiler.Storage;

namespace Kiler.Core;

/// <summary>
/// <c>stashes\&lt;id&gt;.json</c> (M5.3, design 6.6): a stash, which the paths of the records name by its id. It has a name
/// and its dates and authors, and no level: stashes are at level 0, so every reader has them. The names are unique without
/// regard to case when a stash is made (M5.4), but two of one name, made at the same moment, are both read; an admin merges
/// them after the exam. It is on the share, so a content that does not fit is invalid, with a reason.
/// </summary>
public sealed class StashFile
{
    /// <param name="id">The id of the document, an <see cref="ItemId"/>.</param>
    /// <param name="name">The name, not blank.</param>
    /// <param name="authorship">Who made it and when, and who changed it last and when.</param>
    /// <exception cref="ArgumentException">A value does not fit; the message says why.</exception>
    public StashFile(string id, string name, Authorship authorship)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(authorship);
        if ((InvalidId(id) ?? Invalid(name)) is { } reason)
        {
            throw new ArgumentException(reason);
        }

        Id = id;
        Name = name;
        Authorship = authorship;
    }

    public string Id { get; }

    public DocumentKey Key => KeyOf(Id);

    public string Name { get; }

    public Authorship Authorship { get; }

    /// <summary>The stash of <paramref name="id"/>, an <see cref="ItemId"/>.</summary>
    public static DocumentKey KeyOf(string id) => DocumentKey.InFolder(DataFolder.Stashes, id);

    /// <summary>The model of the document, or why its content does not fit.</summary>
    /// <exception cref="ArgumentException">The document is not in <c>stashes\</c> of the data folder.</exception>
    public static bool TryRead(Document document, [NotNullWhen(true)] out StashFile? stash, [NotNullWhen(false)] out string? invalid)
    {
        ArgumentNullException.ThrowIfNull(document);
        return TryRead(document.Key, document.Content, out stash, out invalid);
    }

    /// <summary>The content of the document, without <c>id</c> and <c>version</c>, which the layer adds.</summary>
    public JsonObject ToContent() => ModelJson.Write(
        new StashJson
        {
            Name = Name,
            CreatedBy = Authorship.CreatedBy,
            CreatedAt = Authorship.WriteDate(Authorship.CreatedAt),
            UpdatedBy = Authorship.UpdatedBy,
            UpdatedAt = Authorship.WriteDate(Authorship.UpdatedAt),
        },
        CoreJsonContext.Default.StashJson);

    internal static bool TryRead(DocumentKey key, JsonObject content, [NotNullWhen(true)] out StashFile? stash, [NotNullWhen(false)] out string? invalid)
    {
        if (key.Account is not null || key.Folder != DataFolder.Stashes)
        {
            throw new ArgumentException("The document is not a stash.", nameof(key));
        }

        stash = null;
        invalid = InvalidId(key.Id);
        if (invalid is not null)
        {
            return false;
        }

        if (ModelJson.Read(content, CoreJsonContext.Default.StashJson) is not { } json)
        {
            invalid = ModelJson.WrongType;
            return false;
        }

        if (json.Name is not { } name)
        {
            invalid = "name is missing.";
            return false;
        }

        invalid = Invalid(name);
        if (invalid is not null || !Authorship.TryRead(json.CreatedBy, json.CreatedAt, json.UpdatedBy, json.UpdatedAt, out var authorship, out invalid))
        {
            return false;
        }

        stash = new StashFile(key.Id, name, authorship);
        return true;
    }

    /// <summary>Why the id of a document of <c>stashes\</c> is not one of a stash, or <c>null</c>.</summary>
    private static string? InvalidId(string? id) => ItemId.IsValid(id) ? null : "The name of the file is not the id of a stash.";

    private static string? Invalid(string name) => string.IsNullOrWhiteSpace(name) ? "name is blank." : null;
}

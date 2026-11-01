using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Nodes;
using Kiler.Storage;

namespace Kiler.Core;

/// <summary>
/// <c>stashes\&lt;id&gt;.json</c> (M5.3, design 6.6): a stash, which the paths of the records name by its id. It has a name
/// and its dates and authors, and no level: stashes are at level 0, so every reader has them. The names are unique without
/// regard to case among the live stashes when a stash is made (M5.4), but two of one name, made at the same moment, are both
/// read; an admin merges them (M6.22). A merged stash keeps <see cref="Redirect"/>, the id of the stash it was merged into,
/// and a deleted one <see cref="Deleted"/>, and the file stays with its name, so the paths that name it are read through it
/// (<see cref="RecordSet.PathOf"/>, M6.21). It is on the share, so a content that does not fit is invalid, with a reason.
/// </summary>
public sealed class StashFile
{
    /// <summary>The most redirects that an id of a path follows (decided at the start of M6.16 to M6.24, docs/tasks/M6.md).</summary>
    public const int MaxRedirects = 8;

    /// <param name="id">The id of the document, an <see cref="ItemId"/>.</param>
    /// <param name="name">The name, not blank.</param>
    /// <param name="authorship">Who made it and when, and who changed it last and when.</param>
    /// <param name="redirect">The id of the stash that it was merged into, an <see cref="ItemId"/>; null when it is not merged.</param>
    /// <param name="deleted">Whether an admin deleted it.</param>
    /// <exception cref="ArgumentException">A value does not fit; the message says why.</exception>
    public StashFile(string id, string name, Authorship authorship, string? redirect = null, bool deleted = false)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(authorship);
        if ((InvalidId(id) ?? Invalid(name) ?? InvalidRedirect(redirect)) is { } reason)
        {
            throw new ArgumentException(reason);
        }

        Id = id;
        Name = name;
        Authorship = authorship;
        Redirect = redirect;
        Deleted = deleted;
    }

    public string Id { get; }

    public DocumentKey Key => KeyOf(Id);

    public string Name { get; }

    public Authorship Authorship { get; }

    /// <summary>The id of the stash that it was merged into; null when it is not merged. A path follows it (<see cref="RecordSet.PathOf"/>).</summary>
    public string? Redirect { get; }

    /// <summary>Whether an admin deleted it: a path is cut before it (<see cref="RecordSet.PathOf"/>). A stash with <see cref="Redirect"/> is followed, and this is not read.</summary>
    public bool Deleted { get; }

    /// <summary>Neither merged nor deleted: the stashes that <c>GET /api/stashes</c> gives and the name of a new stash is checked against.</summary>
    public bool IsLive => Redirect is null && !Deleted;

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
            Redirect = Redirect,
            Deleted = Deleted ? true : null,
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

        invalid = Invalid(name) ?? InvalidRedirect(json.Redirect);
        if (invalid is not null || !Authorship.TryRead(json.CreatedBy, json.CreatedAt, json.UpdatedBy, json.UpdatedAt, out var authorship, out invalid))
        {
            return false;
        }

        stash = new StashFile(key.Id, name, authorship, json.Redirect, json.Deleted is true);
        return true;
    }

    /// <summary>Why the id of a document of <c>stashes\</c> is not one of a stash, or <c>null</c>.</summary>
    private static string? InvalidId(string? id) => ItemId.IsValid(id) ? null : "The name of the file is not the id of a stash.";

    private static string? Invalid(string name) => string.IsNullOrWhiteSpace(name) ? "name is blank." : null;

    /// <summary>Why a <c>redirect</c> is not the id of a stash, or <c>null</c>; one that leads back to it is read as a loop (<see cref="RecordSet.PathOf"/>).</summary>
    private static string? InvalidRedirect(string? redirect) => redirect is null || ItemId.IsValid(redirect) ? null : "redirect is not the id of a stash.";
}

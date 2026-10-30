using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json.Nodes;
using Kiler.Storage;

namespace Kiler.Core;

/// <summary>
/// <c>records\&lt;id&gt;.json</c> (M5.3, design 5.2): a shared record as the file has it. <c>public</c> is level 0 in clear: the
/// header (the stash path, the name, the tags) and the fields of level 0, the description among them. <c>protected</c> holds
/// a group of fields per level from 1, each encrypted with the key of its level (<see cref="Kiler.Crypto.FieldGroup"/>), and
/// <c>fields</c> lists in clear the keys of each group (decided at the start of M5). The model does not decrypt and does not
/// know the template: <see cref="RecordView"/> reads it for a session. It is on the share, so a content that does not fit is
/// invalid, with a reason.
/// </summary>
public sealed class RecordFile
{
    /// <summary>The most stashes in a path (design 6.6).</summary>
    public const int MaxStashes = 4;

    /// <summary>The key of the description among the fields: it is at level 0 unless it is raised (design 6.1).</summary>
    public const string Description = "description";

    /// <param name="id">The id of the document, an <see cref="ItemId"/>.</param>
    /// <param name="template">The id of its template, which must pass <see cref="StorageName"/>.</param>
    /// <param name="authorship">Who made it and when, and who changed it last and when.</param>
    /// <param name="stash">The path, 1 to <see cref="MaxStashes"/> ids of stashes without repeats.</param>
    /// <param name="name">The name, not blank.</param>
    /// <param name="tags">The tags, none blank.</param>
    /// <param name="publicFields">The fields of level 0 by key, the description among them; none of the header.</param>
    /// <param name="protectedGroups">The encrypted group of each level from 1 to <see cref="LevelsFile.MaxTop"/>.</param>
    /// <param name="groupFields">The keys of the fields of each group, of the levels of <paramref name="protectedGroups"/>.</param>
    /// <exception cref="ArgumentException">A value does not fit; the message says why.</exception>
    public RecordFile(
        string id,
        string template,
        Authorship authorship,
        IReadOnlyList<string> stash,
        string name,
        IReadOnlyList<string> tags,
        IReadOnlyDictionary<string, FieldValue> publicFields,
        IReadOnlyDictionary<int, string> protectedGroups,
        IReadOnlyDictionary<int, IReadOnlyList<string>> groupFields)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(authorship);
        ArgumentNullException.ThrowIfNull(stash);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(tags);
        ArgumentNullException.ThrowIfNull(publicFields);
        ArgumentNullException.ThrowIfNull(protectedGroups);
        ArgumentNullException.ThrowIfNull(groupFields);
        if ((InvalidId(id) ?? Invalid(template, stash, name, tags, publicFields, protectedGroups, groupFields)) is { } reason)
        {
            throw new ArgumentException(reason);
        }

        Id = id;
        Template = template;
        Authorship = authorship;
        Stash = [.. stash];
        Name = name;
        Tags = [.. tags];
        Public = new Dictionary<string, FieldValue>(publicFields, StringComparer.Ordinal);
        Protected = new SortedDictionary<int, string>(protectedGroups.ToDictionary());
        Fields = new SortedDictionary<int, IReadOnlyList<string>>(groupFields.ToDictionary(group => group.Key, group => (IReadOnlyList<string>)[.. group.Value]));
    }

    public string Id { get; }

    public DocumentKey Key => KeyOf(Id);

    /// <summary>The id of the template; it can be missing from <c>templates\</c>, or not fit.</summary>
    public string Template { get; }

    public Authorship Authorship { get; }

    /// <summary>The ids of the stashes of the path, from the first; a stash can be missing from <c>stashes\</c>.</summary>
    public IReadOnlyList<string> Stash { get; }

    public string Name { get; }

    public IReadOnlyList<string> Tags { get; }

    /// <summary>The fields of level 0 that have a value, by key: those of the template, the description, and any other that the file has.</summary>
    public IReadOnlyDictionary<string, FieldValue> Public { get; }

    /// <summary>The encrypted group of each level, by level in order: the levels of the record.</summary>
    public IReadOnlyDictionary<int, string> Protected { get; }

    /// <summary>The keys that the file lists for each group, by level in order. For a group that a session reads, the decrypted group is the truth.</summary>
    public IReadOnlyDictionary<int, IReadOnlyList<string>> Fields { get; }

    /// <summary>The record of <paramref name="id"/>, an <see cref="ItemId"/>.</summary>
    public static DocumentKey KeyOf(string id) => DocumentKey.InFolder(DataFolder.Records, id);

    /// <summary>The model of the document, or why its content does not fit.</summary>
    /// <exception cref="ArgumentException">The document is not in <c>records\</c> of the data folder.</exception>
    public static bool TryRead(Document document, [NotNullWhen(true)] out RecordFile? record, [NotNullWhen(false)] out string? invalid)
    {
        ArgumentNullException.ThrowIfNull(document);
        return TryRead(document.Key, document.Content, out record, out invalid);
    }

    /// <summary>The content of the document, without <c>id</c> and <c>version</c>, which the layer adds.</summary>
    public JsonObject ToContent()
    {
        var content = ModelJson.Write(
            new RecordJson
            {
                Template = Template,
                CreatedBy = Authorship.CreatedBy,
                CreatedAt = Authorship.WriteDate(Authorship.CreatedAt),
                UpdatedBy = Authorship.UpdatedBy,
                UpdatedAt = Authorship.WriteDate(Authorship.UpdatedAt),
                Public = new RecordPublicJson { Stash = [.. Stash], Name = Name, Tags = [.. Tags] },
                Protected = Protected.ToDictionary(group => LevelName(group.Key), group => (string?)group.Value),
                Fields = Fields.ToDictionary(group => LevelName(group.Key), group => (List<string?>?)[.. group.Value]),
            },
            CoreJsonContext.Default.RecordJson);
        var publicFields = content["public"]!.AsObject();
        foreach (var (key, value) in Public)
        {
            publicFields[key] = value.ToNode();
        }

        return content;
    }

    internal static bool TryRead(DocumentKey key, JsonObject content, [NotNullWhen(true)] out RecordFile? record, [NotNullWhen(false)] out string? invalid)
    {
        if (key.Account is not null || key.Folder != DataFolder.Records)
        {
            throw new ArgumentException("The document is not a shared record.", nameof(key));
        }

        record = null;
        invalid = InvalidId(key.Id);
        if (invalid is not null)
        {
            return false;
        }

        if (ModelJson.Read(content, CoreJsonContext.Default.RecordJson) is not { } json)
        {
            invalid = ModelJson.WrongType;
            return false;
        }

        if (!Authorship.TryRead(json.CreatedBy, json.CreatedAt, json.UpdatedBy, json.UpdatedAt, out var authorship, out invalid))
        {
            return false;
        }

        if (json.Public is not { } header)
        {
            invalid = "public is missing.";
            return false;
        }

        if (json.Template is not { } template)
        {
            invalid = "template is missing.";
            return false;
        }

        if (header.Stash is null || header.Stash.Contains(null))
        {
            invalid = "public.stash is missing or has null.";
            return false;
        }

        if (header.Name is not { } name)
        {
            invalid = "public.name is missing.";
            return false;
        }

        if (header.Tags is null || header.Tags.Contains(null))
        {
            invalid = "public.tags is missing or has null.";
            return false;
        }

        List<string> stash = [.. header.Stash.OfType<string>()];
        List<string> tags = [.. header.Tags.OfType<string>()];

        var publicFields = new Dictionary<string, FieldValue>(StringComparer.Ordinal);
        foreach (var (field, element) in header.Fields ?? [])
        {
            if (!FieldValue.TryRead(element, out var value))
            {
                invalid = "A field of public is not a text, a number or a code.";
                return false;
            }

            if (value is not null)
            {
                publicFields.Add(field, value);
            }
        }

        if (!TryReadGroups(json, out var protectedGroups, out var groupFields, out invalid))
        {
            return false;
        }

        invalid = Invalid(template, stash, name, tags, publicFields, protectedGroups, groupFields);
        if (invalid is not null)
        {
            return false;
        }

        record = new RecordFile(key.Id, template, authorship, stash, name, tags, publicFields, protectedGroups, groupFields);
        return true;
    }

    /// <summary><c>protected</c> and <c>fields</c> by level, or why they do not fit: each name is the digit of a level, as in a user file (<see cref="KeySets"/>).</summary>
    private static bool TryReadGroups(
        RecordJson json,
        [NotNullWhen(true)] out Dictionary<int, string>? protectedGroups,
        [NotNullWhen(true)] out Dictionary<int, IReadOnlyList<string>>? groupFields,
        [NotNullWhen(false)] out string? invalid)
    {
        protectedGroups = null;
        groupFields = null;
        if (json.Protected is null || json.Fields is null)
        {
            invalid = $"{(json.Protected is null ? "protected" : "fields")} is missing.";
            return false;
        }

        var groups = new Dictionary<int, string>();
        foreach (var (name, group) in json.Protected)
        {
            if (KeySets.LevelOf(name, LevelsFile.MaxTop) is not { } level || group is null)
            {
                invalid = $"protected has a name that is not the digit of a level from 1 to {LevelsFile.MaxTop}, or a group that is null.";
                return false;
            }

            groups.Add(level, group);
        }

        var lists = new Dictionary<int, IReadOnlyList<string>>();
        foreach (var (name, keys) in json.Fields)
        {
            if (KeySets.LevelOf(name, LevelsFile.MaxTop) is not { } level || keys is null || keys.Contains(null))
            {
                invalid = $"fields has a name that is not the digit of a level from 1 to {LevelsFile.MaxTop}, or a list that is null or has null.";
                return false;
            }

            lists.Add(level, [.. keys.OfType<string>()]);
        }

        protectedGroups = groups;
        groupFields = lists;
        invalid = null;
        return true;
    }

    /// <summary>Why the id of a document of <c>records\</c> is not one of a record, or <c>null</c>.</summary>
    private static string? InvalidId(string? id) => ItemId.IsValid(id) ? null : "The name of the file is not the id of a record.";

    /// <summary>Why a record does not fit as a whole, or <c>null</c>; the reason names a member, never a value.</summary>
    private static string? Invalid(
        string template,
        IReadOnlyList<string> stash,
        string name,
        IReadOnlyList<string> tags,
        IReadOnlyDictionary<string, FieldValue> publicFields,
        IReadOnlyDictionary<int, string> protectedGroups,
        IReadOnlyDictionary<int, IReadOnlyList<string>> groupFields)
    {
        if (!StorageName.IsValid(template))
        {
            return "template is not the id of a template.";
        }

        if (stash.Count is 0 or > MaxStashes || !stash.All(ItemId.IsValid))
        {
            return $"public.stash is not 1 to {MaxStashes} ids of stashes.";
        }

        if (stash.Distinct(StringComparer.Ordinal).Count() != stash.Count)
        {
            return "public.stash has a stash twice.";
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            return "public.name is blank.";
        }

        if (tags.Any(string.IsNullOrWhiteSpace))
        {
            return "An entry of public.tags is blank.";
        }

        if (publicFields.Keys.Any(key => key is "stash" or "name" or "tags") || publicFields.Values.Any(value => value is null))
        {
            return "A field of public is a member of the header or has no value.";
        }

        if (protectedGroups.Keys.Any(level => level is < 1 or > LevelsFile.MaxTop) || protectedGroups.Values.Any(group => group is null))
        {
            return $"protected has a level that is not from 1 to {LevelsFile.MaxTop}, or a group that is null.";
        }

        if (!protectedGroups.Keys.Order().SequenceEqual(groupFields.Keys.Order()))
        {
            return "fields does not name the levels of protected.";
        }

        var listed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in groupFields.Values.SelectMany(keys => keys))
        {
            if (key is null || publicFields.ContainsKey(key) || !listed.Add(key))
            {
                return "fields names a field twice, or a field of public.";
            }
        }

        return null;
    }

    private static string LevelName(int level) => level.ToString(CultureInfo.InvariantCulture);
}

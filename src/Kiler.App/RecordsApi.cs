using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Nodes;
using Kiler.Core;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Kiler.App;

/// <param name="Account">The account that Kiler wrote.</param>
/// <param name="Name">The display name of its user file; null when the team has no user file of it that fits.</param>
internal sealed record AuthorResponse(string Account, string? Name);

/// <param name="Id">The id of the stash.</param>
/// <param name="Name">Its name; null when it cannot be read, as it is missing or does not fit.</param>
internal sealed record PathStashResponse(string Id, string? Name);

/// <summary>A field of a record, and its description, as the page gets it (docs/api.md, "Field").</summary>
/// <param name="Key">The key of the field in the template; <c>description</c> for the description.</param>
/// <param name="Level">The level of the field in this record, or the one that a value would get.</param>
/// <param name="Kind"><c>value</c>, <c>empty</c>, <c>secret</c>, <c>locked</c> or <c>unreadable</c>.</param>
/// <param name="Value">The value of a <c>value</c> (docs/api.md, "Values by type"); null for the other kinds.</param>
/// <param name="LevelName">The name of the level of a <c>locked</c> field; null otherwise, or when <c>levels.json</c> cannot be read.</param>
internal sealed record FieldResponse(string Key, int Level, string Kind, JsonNode? Value, string? LevelName)
{
    /// <summary>Without the value, which is decrypted (SKILL.md, "Style").</summary>
    public override string ToString() => $"{Key} ({Level}, {Kind})";
}

/// <summary>An item of <c>GET /api/records</c> (docs/api.md, "List item").</summary>
/// <param name="Id">The id of the record.</param>
/// <param name="Template">The id of its template.</param>
/// <param name="Version">The version of its file, which an edit and a delete are over.</param>
/// <param name="Stash">The stashes of its path, from the first.</param>
/// <param name="Name">Its name.</param>
/// <param name="Description">Its description, as a field.</param>
/// <param name="Tags">Its tags.</param>
/// <param name="Fields">The fields of the template with <c>list</c>, in its order, never a <c>secret</c>.</param>
/// <param name="UpdatedBy">Who changed it last.</param>
/// <param name="UpdatedAt">When, in UTC.</param>
internal sealed record ListItemResponse(
    string Id,
    string Template,
    int Version,
    IReadOnlyList<PathStashResponse> Stash,
    string Name,
    FieldResponse Description,
    IReadOnlyList<string> Tags,
    IReadOnlyList<FieldResponse> Fields,
    AuthorResponse UpdatedBy,
    DateTime UpdatedAt);

/// <param name="Id">The id of the stash.</param>
/// <param name="Name">Its name; null when it cannot be read.</param>
/// <param name="Count">The records at the node and under it.</param>
/// <param name="Children">The nodes under it, by name.</param>
internal sealed record StashNodeResponse(string Id, string? Name, int Count, IReadOnlyList<StashNodeResponse> Children);

/// <param name="Id">The id of a template.</param>
/// <param name="Count">How many records it gives.</param>
internal sealed record TemplateCountResponse(string Id, int Count);

/// <param name="Tag">A tag.</param>
/// <param name="Count">How many records it gives.</param>
internal sealed record TagCountResponse(string Tag, int Count);

/// <param name="Value">The text of a value that the session reads.</param>
/// <param name="Count">How many records it gives.</param>
internal sealed record ValueCountResponse(string Value, int Count)
{
    /// <summary>Without the value, which can be decrypted (SKILL.md, "Style").</summary>
    public override string ToString() => $"{nameof(ValueCountResponse)} ({Count})";
}

/// <param name="Key">The key of a field with <c>filter</c>.</param>
/// <param name="Values">Its values, by their text.</param>
internal sealed record FieldFacetResponse(string Key, IReadOnlyList<ValueCountResponse> Values);

/// <param name="Templates">The templates, by id.</param>
/// <param name="Tags">The tags, by tag.</param>
/// <param name="Fields">The fields with <c>filter</c>, by key.</param>
internal sealed record FacetsResponse(IReadOnlyList<TemplateCountResponse> Templates, IReadOnlyList<TagCountResponse> Tags, IReadOnlyList<FieldFacetResponse> Fields);

/// <param name="Records">The records that match everything, by name.</param>
/// <param name="Tree">The tree of stashes of the records that match everything but <c>stash</c>.</param>
/// <param name="Facets">The counts of each filter.</param>
internal sealed record RecordsResponse(IReadOnlyList<ListItemResponse> Records, IReadOnlyList<StashNodeResponse> Tree, FacetsResponse Facets);

/// <summary>The answer of <c>GET /api/records/{id}</c> and of the writes (docs/api.md, "Record").</summary>
/// <param name="Id">The id of the record.</param>
/// <param name="Template">The id of its template.</param>
/// <param name="Version">The version of its file, which an edit and a delete are over.</param>
/// <param name="Stash">The stashes of its path, from the first.</param>
/// <param name="Name">Its name.</param>
/// <param name="Description">Its description, as a field.</param>
/// <param name="Tags">Its tags.</param>
/// <param name="Levels">The levels of its groups, by which the page offers the delete and the restores.</param>
/// <param name="Fields">Every field of the template, in its order; empty when the template is missing or does not fit.</param>
/// <param name="CreatedBy">Who created it.</param>
/// <param name="CreatedAt">When, in UTC.</param>
/// <param name="UpdatedBy">Who changed it last.</param>
/// <param name="UpdatedAt">When, in UTC.</param>
internal sealed record RecordResponse(
    string Id,
    string Template,
    int Version,
    IReadOnlyList<PathStashResponse> Stash,
    string Name,
    FieldResponse Description,
    IReadOnlyList<string> Tags,
    IReadOnlyList<int> Levels,
    IReadOnlyList<FieldResponse> Fields,
    AuthorResponse CreatedBy,
    DateTime CreatedAt,
    AuthorResponse UpdatedBy,
    DateTime UpdatedAt);

/// <param name="Field">The key of a <c>secret</c> of the record.</param>
/// <param name="Use"><c>show</c> or <c>copy</c>, which the audit of Prod records after the exam.</param>
internal sealed record SecretRequest(string? Field, string? Use);

/// <param name="Value">The value of the <c>secret</c>, which only this answer gives.</param>
internal sealed record SecretResponse(string Value)
{
    /// <summary>The name of the type: a record prints its properties, and this one holds a secret (SKILL.md, "Style").</summary>
    public override string ToString() => nameof(SecretResponse);
}

/// <summary>
/// The records (docs/api.md): the list (M5.6), <c>GET /api/records</c>, with the search, the filters, the tree and the facets
/// over the records in memory as the session of the request reads them (<see cref="RecordSearch"/>); a record (M5.7),
/// <c>GET /api/records/{id}</c>; and the value of a <c>secret</c> (M5.7), <c>POST /api/records/{id}/secret</c>, which
/// decrypts its group again (<see cref="RecordView.Secret"/>). They need <see cref="Need.Reader"/>, so a guest in guest mode
/// reads level 0, but the secret refuses a guest.
/// </summary>
internal static class RecordsApi
{
    /// <summary>The 404 of a record that is not there, or is in the trash.</summary>
    public const string NotThere = "The record is not there. It may have been deleted.";

    private const string FieldPrefix = "field.";

    public static void Map(IEndpointRouteBuilder endpoints, KilerData data)
    {
        endpoints.MapGet("/api/records", (HttpContext context) => List(context, data)).Needs(Need.Reader);
        endpoints.MapGet("/api/records/{id}", (HttpContext context, string id) => Get(context, data, id)).Needs(Need.Reader);
        endpoints.MapPost("/api/records/{id}/secret", (HttpContext context, string id, SecretRequest request) => Secret(context, data, id, request))
            .Needs(Need.Reader);
    }

    /// <summary>What the page says when the request of a secret is refused for its field (docs/api.md).</summary>
    public static string Message(SecretOutcome outcome) => outcome switch
    {
        SecretOutcome.NoField => "The template of the record has no field of that key.",
        SecretOutcome.NotSecret => "The field is not a secret.",
        SecretOutcome.Locked => "The field is at a level that you do not hold.",
        SecretOutcome.Empty => "The field has no value now.",
        _ => "The field cannot be read: its group does not decrypt.",
    };

    /// <summary>
    /// A record as the page gets it (docs/api.md, "Record"), for <c>GET /api/records/{id}</c>, the writes and the
    /// <c>current</c> of a conflict: every field of its template, a <c>secret</c> without its value.
    /// </summary>
    internal static RecordResponse Record(RecordView view, RecordSet set, TeamFiles files)
    {
        var record = view.File.Model!;
        var fields = view.Template is { } template
            ? template.Fields.Zip(view.Fields).Select(pair => Field(pair.Second, pair.First.Type, set, files))
            : [];
        return new(
            record.Id,
            record.Template,
            view.File.Version.GetValueOrDefault(),
            Stash(record, set),
            record.Name,
            Field(view.Description!, FieldType.Text, set, files),
            record.Tags,
            [.. record.Protected.Keys],
            [.. fields],
            Author(record.Authorship.CreatedBy, files),
            record.Authorship.CreatedAt,
            Author(record.Authorship.UpdatedBy, files),
            record.Authorship.UpdatedAt);
    }

    private static Results<Ok<RecordsResponse>, BadRequest<ErrorResponse>, Conflict<ErrorResponse>> List(HttpContext context, KilerData data)
    {
        if (!TryRead(context.Request.Query, out var query, out var error))
        {
            return TypedResults.BadRequest(new ErrorResponse(error));
        }

        if (data.Files is not { } files || data.Records is not { } records)
        {
            return TypedResults.Conflict(new ErrorResponse(SessionApi.NoData));
        }

        // A lock since the check is SessionLockedException, which the rights answer as locked (host.md, "Rights").
        var set = records.Read(AccessRules.SessionOf(context));
        var found = RecordSearch.Find(set, query);
        return TypedResults.Ok(new RecordsResponse(
            [.. found.Records.Select(view => ListItem(view, set, files))],
            [.. found.Tree.Select(Node)],
            new FacetsResponse(
                [.. found.Templates.Select(template => new TemplateCountResponse(template.Value, template.Count))],
                [.. found.Tags.Select(tag => new TagCountResponse(tag.Value, tag.Count))],
                [.. found.Fields.Select(field => new FieldFacetResponse(field.Key, [.. field.Values.Select(value => new ValueCountResponse(value.Value, value.Count))]))])));
    }

    private static Results<Ok<RecordResponse>, NotFound<ErrorResponse>, Conflict<ErrorResponse>> Get(HttpContext context, KilerData data, string id)
    {
        if (data.Files is not { } files || data.Records is not { } records)
        {
            return TypedResults.Conflict(new ErrorResponse(SessionApi.NoData));
        }

        var set = records.Read(AccessRules.SessionOf(context));
        return Find(set, id) is { } view
            ? TypedResults.Ok(Record(view, set, files))
            : TypedResults.NotFound(new ErrorResponse(NotThere));
    }

    /// <summary>
    /// The value of a <c>secret</c> for "show" and "copy" (docs/api.md), with <c>Cache-Control: no-store</c>: its group is
    /// decrypted again, and the value is in this answer only.
    /// </summary>
    private static Results<Ok<SecretResponse>, BadRequest<ErrorResponse>, NotFound<ErrorResponse>, Conflict<ErrorResponse>, JsonHttpResult<ErrorResponse>> Secret(
        HttpContext context, KilerData data, string id, SecretRequest request)
    {
        context.Response.Headers.CacheControl = "no-store";

        // Need.Reader lets a guest through in guest mode, who reads level 0 only, where a secret is not (design 7.1).
        if (AccessRules.SessionOf(context) is not { } session)
        {
            return Forbidden(AccessRules.Message(AccessRefusal.NotSignedIn));
        }

        if (request.Use is not ("show" or "copy"))
        {
            return TypedResults.BadRequest(new ErrorResponse("use is not show or copy."));
        }

        if (data.Records is not { } records)
        {
            return TypedResults.Conflict(new ErrorResponse(SessionApi.NoData));
        }

        if (Find(records.Read(session), id) is not { } view)
        {
            return TypedResults.NotFound(new ErrorResponse(NotThere));
        }

        // The use is only checked until the audit of Prod after the exam, which records it (PLAN section 10).
        var outcome = view.Secret(request.Field ?? string.Empty, session, out var value);
        return outcome switch
        {
            SecretOutcome.Value => TypedResults.Ok(new SecretResponse(value!)),
            SecretOutcome.Locked => Forbidden(Message(outcome)),
            SecretOutcome.NoField or SecretOutcome.NotSecret => TypedResults.BadRequest(new ErrorResponse(Message(outcome))),
            _ => TypedResults.Conflict(new ErrorResponse(Message(outcome))),
        };
    }

    /// <summary>The record <paramref name="id"/> of the set, unless its file does not fit, as the list leaves it out (docs/api.md).</summary>
    private static RecordView? Find(RecordSet set, string id) => set.Records.GetValueOrDefault(id) is { File.IsValid: true } view ? view : null;

    private static JsonHttpResult<ErrorResponse> Forbidden(string error) =>
        TypedResults.Json(new ErrorResponse(error), KilerJsonContext.Default.ErrorResponse, statusCode: StatusCodes.Status403Forbidden);

    /// <summary>The query of the list, or why it does not fit; an empty part counts as left out (docs/api.md).</summary>
    private static bool TryRead(IQueryCollection parts, [NotNullWhen(true)] out RecordQuery? query, [NotNullWhen(false)] out string? error)
    {
        query = null;
        if (parts.FirstOrDefault(part => part.Value.Count > 1 && !string.Equals(part.Key, "tag", StringComparison.OrdinalIgnoreCase)) is { Key: { } twice })
        {
            error = $"{twice} is given more than once.";
            return false;
        }

        string[] stash = [];
        if (parts["stash"].ToString() is { Length: > 0 } path)
        {
            stash = path.Split(',');
            if (stash.Length > RecordFile.MaxStashes || !stash.All(ItemId.IsValid))
            {
                error = $"stash is not 1 to {RecordFile.MaxStashes} ids of stashes.";
                return false;
            }
        }

        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, value) in parts)
        {
            if (name.StartsWith(FieldPrefix, StringComparison.OrdinalIgnoreCase) && value.ToString() is { Length: > 0 } text)
            {
                fields[name[FieldPrefix.Length..]] = text;
            }
        }

        error = null;
        query = new RecordQuery
        {
            Text = parts["q"].ToString(),
            Stash = stash,
            Template = parts["template"].ToString() is { Length: > 0 } template ? template : null,
            Tags = [.. parts["tag"].Where(tag => !string.IsNullOrEmpty(tag)).Select(tag => tag!)],
            Fields = fields,
        };
        return true;
    }

    private static StashNodeResponse Node(StashNode node) => new(node.Id, node.Name, node.Count, [.. node.Children.Select(Node)]);

    /// <summary>A record of the list: its fields with <c>list</c>, never a <c>secret</c>, whose template says so or whose group marks it.</summary>
    private static ListItemResponse ListItem(RecordView view, RecordSet set, TeamFiles files)
    {
        var record = view.File.Model!;
        var listed = view.Template is { } template
            ? template.Fields.Zip(view.Fields)
                .Where(pair => pair.First.List && pair.First.Type != FieldType.Secret && pair.Second.Kind != FieldKind.Secret)
                .Select(pair => Field(pair.Second, pair.First.Type, set, files))
            : [];
        return new(
            record.Id,
            record.Template,
            view.File.Version.GetValueOrDefault(),
            Stash(record, set),
            record.Name,
            Field(view.Description!, FieldType.Text, set, files),
            record.Tags,
            [.. listed],
            Author(record.Authorship.UpdatedBy, files),
            record.Authorship.UpdatedAt);
    }

    /// <summary>A field as the page gets it; its value keeps the shape that it was written in (docs/api.md, "Values by type").</summary>
    private static FieldResponse Field(RecordField field, FieldType type, RecordSet set, TeamFiles files)
    {
        var value = field.Value is not { } read ? null : read.Kind switch
        {
            FieldValueKind.Number => JsonValue.Create(read.Number),
            FieldValueKind.Code => new JsonObject { ["language"] = read.Language, ["code"] = read.Text },
            _ when type == FieldType.Ref => new JsonObject { ["id"] = read.Text, ["name"] = set.RecordName(read.Text!) },
            _ => (JsonNode)JsonValue.Create(read.Text!),
        };
        var levelName = field.Kind == FieldKind.Locked
            ? files.Levels?.Model?.Levels.FirstOrDefault(level => level.Level == field.Level)?.Name
            : null;
        return new(field.Key, field.Level, KindOf(field.Kind), value, levelName);
    }

    private static string KindOf(FieldKind kind) => kind switch
    {
        FieldKind.Value => "value",
        FieldKind.Empty => "empty",
        FieldKind.Secret => "secret",
        FieldKind.Locked => "locked",
        _ => "unreadable",
    };

    private static PathStashResponse[] Stash(RecordFile record, RecordSet set) => [.. record.Stash.Select(id => new PathStashResponse(id, set.StashName(id)))];

    private static AuthorResponse Author(string account, TeamFiles files) => new(account, files.Users.GetValueOrDefault(account)?.Model?.DisplayName);
}

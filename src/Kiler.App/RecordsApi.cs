using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
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
/// A new record or an edit as the page sends it (docs/api.md, <c>POST /api/records</c> and <c>PUT /api/records/{id}</c>). A
/// member that it does not have, such as the dates and the authors, is ignored.
/// </summary>
/// <param name="Template">The id of the template of a new record; an edit does not read it.</param>
/// <param name="Version">The version that the editor read, which an edit is over; a new record does not read it.</param>
/// <param name="Stash">The ids of the stashes of the path, from the first.</param>
/// <param name="Name">The name.</param>
/// <param name="Description">The description.</param>
/// <param name="Tags">The tags.</param>
/// <param name="Fields">The values of the fields of the template by key, each a JSON value of its type, read by <see cref="FieldValue.TryRead"/>.</param>
/// <param name="Levels">The levels asked, by key.</param>
internal sealed record RecordRequest(
    string? Template,
    int? Version,
    IReadOnlyList<string?>? Stash,
    string? Name,
    string? Description,
    IReadOnlyList<string?>? Tags,
    IReadOnlyDictionary<string, JsonElement>? Fields,
    IReadOnlyDictionary<string, int>? Levels)
{
    /// <summary>The name of the type: a record prints its properties, and this one holds the value of a <c>secret</c> (SKILL.md, "Style").</summary>
    public override string ToString() => nameof(RecordRequest);
}

/// <summary>The 400 of a write (docs/api.md, "Conventions").</summary>
/// <param name="Error">Why the write was refused, for the page.</param>
/// <param name="Field">The key of what does not fit, or null when it is none of the members of the record.</param>
internal sealed record WriteErrorResponse(string Error, string? Field);

/// <summary>The 409 of a write over an older version (docs/api.md, "Conflict").</summary>
/// <param name="Error">That the record has been changed meanwhile.</param>
/// <param name="Current">The record as it is now, as this session reads it.</param>
internal sealed record ConflictResponse(string Error, RecordResponse Current);

/// <param name="Tags">The tags of the records, each once without regard to case, by name.</param>
internal sealed record TagsResponse(IReadOnlyList<string> Tags);

/// <summary>An item of <c>GET /api/trash</c> and the answer of a delete (docs/api.md, "Trash item"): the list item with <c>levels</c>.</summary>
/// <param name="Id">The id of the record.</param>
/// <param name="Template">The id of its template.</param>
/// <param name="Version">The version of its file, which the delete kept.</param>
/// <param name="Stash">The stashes of its path, from the first.</param>
/// <param name="Name">Its name.</param>
/// <param name="Description">Its description, as a field.</param>
/// <param name="Tags">Its tags.</param>
/// <param name="Levels">The levels of its groups, by which the page offers the restore.</param>
/// <param name="Fields">The fields of the template with <c>list</c>, in its order, never a <c>secret</c>.</param>
/// <param name="UpdatedBy">Who changed it last; the trash does not say who deleted it.</param>
/// <param name="UpdatedAt">When, in UTC.</param>
internal sealed record TrashItemResponse(
    string Id,
    string Template,
    int Version,
    IReadOnlyList<PathStashResponse> Stash,
    string Name,
    FieldResponse Description,
    IReadOnlyList<string> Tags,
    IReadOnlyList<int> Levels,
    IReadOnlyList<FieldResponse> Fields,
    AuthorResponse UpdatedBy,
    DateTime UpdatedAt);

/// <param name="Records">The records in the trash, by name.</param>
internal sealed record TrashResponse(IReadOnlyList<TrashItemResponse> Records);

/// <summary>
/// The records (docs/api.md): the list (M5.6), <c>GET /api/records</c>, with the search, the filters, the tree and the facets
/// over the records in memory as the session of the request reads them (<see cref="RecordSearch"/>); a record (M5.7),
/// <c>GET /api/records/{id}</c>; the value of a <c>secret</c> (M5.7), <c>POST /api/records/{id}/secret</c>, which
/// decrypts its group again (<see cref="RecordView.Secret"/>); and the tags (M5.9), <c>GET /api/tags</c>. They need
/// <see cref="Need.Reader"/>, so a guest in guest mode reads level 0, but the secret refuses a guest. A new record and an edit
/// (M5.9), <c>POST /api/records</c> and <c>PUT /api/records/{id}</c>, need <see cref="Need.Editor"/> and are
/// <see cref="RecordActions"/>; the dates and the authors come from Kiler, never from the request. The trash (M5.10):
/// <c>GET /api/trash</c> for a reader, and the delete, <c>DELETE /api/records/{id}</c>, and the restore,
/// <c>POST /api/trash/{id}/restore</c>, for an editor who holds every level of the record.
/// </summary>
internal static class RecordsApi
{
    /// <summary>The 404 of a record that is not there, or is in the trash.</summary>
    public const string NotThere = "The record is not there. It may have been deleted.";

    /// <summary>The 404 of a restore of a record that is not in the trash, also one that another Kiler has restored meanwhile (M5.10).</summary>
    public const string NotInTrash = "The record is not in the trash. It may have been restored.";

    /// <summary>The 400 of a value of <c>fields</c> that has no shape of a value, with its key.</summary>
    public const string NoShape = "The value is not a text, a number or a code.";

    /// <summary>The 400 of an edit without the version that the editor read.</summary>
    public const string NoVersion = "The request has no version.";

    private const string FieldPrefix = "field.";

    public static void Map(IEndpointRouteBuilder endpoints, KilerData data, TimeProvider time)
    {
        endpoints.MapGet("/api/records", (HttpContext context) => List(context, data)).Needs(Need.Reader);
        endpoints.MapGet("/api/records/{id}", (HttpContext context, string id) => Get(context, data, id)).Needs(Need.Reader);
        endpoints.MapPost("/api/records/{id}/secret", (HttpContext context, string id, SecretRequest request) => Secret(context, data, id, request))
            .Needs(Need.Reader);
        endpoints.MapPost("/api/records", (HttpContext context, RecordRequest request) => Create(context, data, request, time)).Needs(Need.Editor);
        endpoints.MapPut("/api/records/{id}", (HttpContext context, string id, RecordRequest request) => Edit(context, data, id, request, time))
            .Needs(Need.Editor);
        endpoints.MapGet("/api/tags", (HttpContext context) => Tags(context, data)).Needs(Need.Reader);
        endpoints.MapDelete("/api/records/{id}", (HttpContext context, string id, int? version) => Delete(context, data, id, version)).Needs(Need.Editor);
        endpoints.MapGet("/api/trash", (HttpContext context) => Trash(context, data)).Needs(Need.Reader);
        endpoints.MapPost("/api/trash/{id}/restore", (HttpContext context, string id) => Restore(context, data, id)).Needs(Need.Editor);
    }

    /// <summary>What the page says when a write of a record was refused, but for a value that does not fit, whose reason is its own (docs/api.md).</summary>
    public static string Message(RecordOutcome outcome) => outcome switch
    {
        RecordOutcome.Busy => "The record is being written now. Try again in a moment.",
        RecordOutcome.Offline => "The shared folder cannot be reached. The change can be made once it is back.",
        RecordOutcome.Conflict => "The record has been changed meanwhile.",
        RecordOutcome.Missing => NotThere,
        RecordOutcome.NoTemplate => "The template of this record is not there, or cannot be used, so the record cannot be changed.",
        RecordOutcome.Unreadable => "A part of this record cannot be read, so it cannot be changed. An admin restores it from the history.",
        RecordOutcome.NotHeld => "A part of this record is at a level that you do not hold.",
        _ => "The record could not be written to the shared folder. Try again in a moment.",
    };

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

    private static Results<Ok<RecordResponse>, Created<RecordResponse>, BadRequest<WriteErrorResponse>, NotFound<ErrorResponse>, Conflict<ErrorResponse>, Conflict<ConflictResponse>> Create(
        HttpContext context, KilerData data, RecordRequest request, TimeProvider time)
    {
        // Need.Editor lets no request through without a session.
        var author = AccessRules.SessionOf(context) ?? throw new InvalidOperationException("A new record has no session.");
        if (!TryDraft(request, request.Template, out var draft, out var refused))
        {
            return TypedResults.BadRequest(refused);
        }

        if (data.Files is not { } files || data.Records is not { } records)
        {
            return TypedResults.Conflict(new ErrorResponse(SessionApi.NoData));
        }

        // A lock before the write, or after it before its record was read, is SessionLockedException, which the rights answer
        // as locked (host.md, "Rights").
        return Answer(RecordActions.NewRecord(author, records, files, draft, time), author, records, files, created: true);
    }

    private static Results<Ok<RecordResponse>, Created<RecordResponse>, BadRequest<WriteErrorResponse>, NotFound<ErrorResponse>, Conflict<ErrorResponse>, Conflict<ConflictResponse>> Edit(
        HttpContext context, KilerData data, string id, RecordRequest request, TimeProvider time)
    {
        var editor = AccessRules.SessionOf(context) ?? throw new InvalidOperationException("An edit has no session.");
        if (request.Version is not { } version)
        {
            return TypedResults.BadRequest(new WriteErrorResponse(NoVersion, null));
        }

        // An edit keeps the template of the record, so the template of the request is not read (core.md).
        if (!TryDraft(request, null, out var draft, out var refused))
        {
            return TypedResults.BadRequest(refused);
        }

        if (data.Files is not { } files || data.Records is not { } records)
        {
            return TypedResults.Conflict(new ErrorResponse(SessionApi.NoData));
        }

        return Answer(RecordActions.Edit(editor, records, id, version, draft, time), editor, records, files, created: false);
    }

    /// <summary>
    /// The answer of a write of a record (docs/api.md): the record as <paramref name="session"/> reads it, 201 for a new one
    /// and 200 for an edit; at a conflict, the 409 with the record as it is now.
    /// </summary>
    private static Results<Ok<RecordResponse>, Created<RecordResponse>, BadRequest<WriteErrorResponse>, NotFound<ErrorResponse>, Conflict<ErrorResponse>, Conflict<ConflictResponse>> Answer(
        RecordResult result, Session session, Records records, TeamFiles files, bool created)
    {
        if (result.Outcome is RecordOutcome.Done or RecordOutcome.Conflict)
        {
            // A record that a load took away at once, or one on the share whose file does not fit, has no record to give (decided in M5.9).
            if (result.Record is not { File.IsValid: true } view)
            {
                return TypedResults.NotFound(new ErrorResponse(NotThere));
            }

            var record = Record(view, records.Read(session), files);
            return result.Outcome == RecordOutcome.Conflict ? TypedResults.Conflict(new ConflictResponse(Message(result.Outcome), record))
                : created ? TypedResults.Created($"/api/records/{record.Id}", record)
                : TypedResults.Ok(record);
        }

        return result.Outcome switch
        {
            RecordOutcome.Invalid => TypedResults.BadRequest(new WriteErrorResponse(result.Invalid!, result.Field)),
            RecordOutcome.Missing => TypedResults.NotFound(new ErrorResponse(Message(result.Outcome))),
            _ => TypedResults.Conflict(new ErrorResponse(Message(result.Outcome))),
        };
    }

    /// <summary>
    /// The draft of a write (core.md, "Templates, stashes and records"): a list or an object that the request leaves out is
    /// empty, and each value of <c>fields</c> is read as a value of a document is, a key with <c>null</c> or <c>""</c> there as
    /// none. A value of no shape is refused with its key, and so is a text with an escaped lone surrogate, which has the shape
    /// of a value but no string (M5.4 security review).
    /// </summary>
    private static bool TryDraft(RecordRequest request, string? template, [NotNullWhen(true)] out RecordDraft? draft, [NotNullWhen(false)] out WriteErrorResponse? refused)
    {
        draft = null;
        var fields = new Dictionary<string, FieldValue?>(StringComparer.Ordinal);
        foreach (var (key, element) in request.Fields ?? new Dictionary<string, JsonElement>())
        {
            FieldValue? value;
            bool fits;
            try
            {
                fits = FieldValue.TryRead(element, out value);
            }
            catch (InvalidOperationException)
            {
                (fits, value) = (false, null);
            }

            if (!fits)
            {
                refused = new(NoShape, key);
                return false;
            }

            fields[key] = value;
        }

        refused = null;
        draft = new(template, request.Stash ?? [], request.Name, request.Description, request.Tags ?? [], fields, request.Levels ?? new Dictionary<string, int>());
        return true;
    }

    /// <summary>The tags of the records that the session reads, for the suggestions of the form (docs/api.md, <c>GET /api/tags</c>).</summary>
    private static Results<Ok<TagsResponse>, Conflict<ErrorResponse>> Tags(HttpContext context, KilerData data)
    {
        if (data.Records is not { } records)
        {
            return TypedResults.Conflict(new ErrorResponse(SessionApi.NoData));
        }

        var tags = records.Read(AccessRules.SessionOf(context)).Records.Values
            .Where(view => view.File.IsValid)
            .SelectMany(view => view.File.Model!.Tags)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase);
        return TypedResults.Ok(new TagsResponse([.. tags]));
    }

    /// <summary>
    /// The delete of a record over the version that the editor read (docs/api.md, <c>DELETE /api/records/{id}</c>): 200 with
    /// the record as it is in the trash, so the page can offer to restore it; at a conflict, the 409 with the record as it is now.
    /// </summary>
    private static Results<Ok<TrashItemResponse>, BadRequest<WriteErrorResponse>, NotFound<ErrorResponse>, Conflict<ErrorResponse>, Conflict<ConflictResponse>, JsonHttpResult<ErrorResponse>> Delete(
        HttpContext context, KilerData data, string id, int? version)
    {
        var editor = AccessRules.SessionOf(context) ?? throw new InvalidOperationException("A delete has no session.");
        if (version is not { } over)
        {
            return TypedResults.BadRequest(new WriteErrorResponse(NoVersion, null));
        }

        if (data.Files is not { } files || data.Records is not { } records)
        {
            return TypedResults.Conflict(new ErrorResponse(SessionApi.NoData));
        }

        var result = RecordActions.Delete(editor, records, id, over);
        if (result.Outcome is RecordOutcome.Done or RecordOutcome.Conflict)
        {
            // As in Answer: a record that a load took away at once, or one on the share whose file does not fit, has none to give.
            if (result.Record is not { File.IsValid: true } view)
            {
                return TypedResults.NotFound(new ErrorResponse(NotThere));
            }

            var set = records.Read(editor);
            return result.Outcome == RecordOutcome.Done
                ? TypedResults.Ok(TrashItem(view, set, files))
                : TypedResults.Conflict(new ConflictResponse(Message(result.Outcome), Record(view, set, files)));
        }

        return result.Outcome switch
        {
            RecordOutcome.NotHeld => Forbidden(Message(result.Outcome)),
            RecordOutcome.Missing => TypedResults.NotFound(new ErrorResponse(NotThere)),
            _ => TypedResults.Conflict(new ErrorResponse(Message(result.Outcome))),
        };
    }

    /// <summary>The records in the trash as the session reads them, by name (docs/api.md, <c>GET /api/trash</c>); one whose file does not fit is left out, as the list leaves it out.</summary>
    private static Results<Ok<TrashResponse>, Conflict<ErrorResponse>> Trash(HttpContext context, KilerData data)
    {
        if (data.Files is not { } files || data.Records is not { } records)
        {
            return TypedResults.Conflict(new ErrorResponse(SessionApi.NoData));
        }

        var set = records.Read(AccessRules.SessionOf(context));
        var trash = set.Trash.Values
            .Where(view => view.File.IsValid)
            .OrderBy(view => view.File.Model!.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(view => view.File.Model!.Id, StringComparer.Ordinal)
            .Select(view => TrashItem(view, set, files));
        return TypedResults.Ok(new TrashResponse([.. trash]));
    }

    /// <summary>The restore of a record from the trash (docs/api.md, <c>POST /api/trash/{id}/restore</c>): 200 with the record as the editor reads it.</summary>
    private static Results<Ok<RecordResponse>, NotFound<ErrorResponse>, Conflict<ErrorResponse>, JsonHttpResult<ErrorResponse>> Restore(HttpContext context, KilerData data, string id)
    {
        var editor = AccessRules.SessionOf(context) ?? throw new InvalidOperationException("A restore has no session.");
        if (data.Files is not { } files || data.Records is not { } records)
        {
            return TypedResults.Conflict(new ErrorResponse(SessionApi.NoData));
        }

        var result = RecordActions.Restore(editor, records, id);
        if (result.Outcome == RecordOutcome.Done)
        {
            return result.Record is { File.IsValid: true } view
                ? TypedResults.Ok(Record(view, records.Read(editor), files))
                : TypedResults.NotFound(new ErrorResponse(NotThere));
        }

        return result.Outcome switch
        {
            RecordOutcome.NotHeld => Forbidden(Message(result.Outcome)),
            RecordOutcome.Missing => TypedResults.NotFound(new ErrorResponse(NotInTrash)),
            _ => TypedResults.Conflict(new ErrorResponse(Message(result.Outcome))),
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

    /// <summary>A record of the trash: the list item with the levels of its groups (docs/api.md, "Trash item").</summary>
    private static TrashItemResponse TrashItem(RecordView view, RecordSet set, TeamFiles files)
    {
        var item = ListItem(view, set, files);
        return new(item.Id, item.Template, item.Version, item.Stash, item.Name, item.Description, item.Tags, [.. view.File.Model!.Protected.Keys], item.Fields, item.UpdatedBy, item.UpdatedAt);
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

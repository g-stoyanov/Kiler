using Kiler.Core;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Kiler.App;

/// <param name="Id">The id of the stash.</param>
/// <param name="Name">Its name.</param>
/// <param name="Used">Whether a record has it at the position asked, under the path asked.</param>
internal sealed record StashChoiceResponse(string Id, string Name, bool Used);

/// <param name="Stashes">Every stash that can be read: those used at the position first, then the rest, each part by name.</param>
internal sealed record StashesResponse(IReadOnlyList<StashChoiceResponse> Stashes);

/// <param name="Name">The name of the new stash.</param>
internal sealed record StashRequest(string? Name);

/// <summary>A live stash as the admin's screen of the stashes shows it (M6.22, <see cref="StashCount"/>).</summary>
/// <param name="Id">The id of the stash.</param>
/// <param name="Name">Its name.</param>
/// <param name="Version">The version of its file, which a rename, a merge and a delete send back.</param>
/// <param name="Records">The number of the shared records whose path, read through the merged and deleted stashes, has it.</param>
internal sealed record AdminStashResponse(string Id, string Name, int Version, int Records);

/// <param name="Stashes">The live stashes, by name.</param>
internal sealed record AdminStashesResponse(IReadOnlyList<AdminStashResponse> Stashes);

/// <param name="Name">The new name of the stash.</param>
/// <param name="Version">The version of the stash that the admin read.</param>
internal sealed record StashRenameRequest(string? Name, int? Version);

/// <param name="Into">The id of the live stash to merge it into.</param>
/// <param name="Version">The version of the stash that the admin read.</param>
internal sealed record StashMergeRequest(string? Into, int? Version);

/// <summary>
/// The stashes of the form of a record (M5.9, docs/api.md, design 6.6): <c>GET /api/stashes</c>, the stashes for the drop-down
/// of a position of the path, which needs <see cref="Need.Reader"/>; and <c>POST /api/stashes</c>, a new stash
/// (<see cref="RecordActions.NewStash(Session, Records, string?, TimeProvider)"/>), which needs <see cref="Need.Editor"/>. And
/// the stashes of an admin (M6.22, <see cref="StashActions"/>), each <see cref="Need.Admin"/>: <c>GET /api/admin/stashes</c>,
/// the live stashes with the number of the shared records of each; and the rename, the merge and the delete of a stash, each
/// over the version that the admin read, which answer with the list as it is after the write.
/// </summary>
internal static class StashesApi
{
    /// <summary>The 409 of a new stash or a rename whose name a live stash has, without regard to case.</summary>
    public const string Exists = "There is a stash of that name already.";

    /// <summary>The 409 of a new stash that the layer did not write, but offline.</summary>
    public const string NotWritten = "The stash could not be written to the shared folder. Try again in a moment.";

    /// <summary>The 404 of an action of an admin on a stash that is not there, does not fit, or is merged or deleted.</summary>
    public const string NotThere = "The stash is not there. It may have been merged or deleted.";

    public static void Map(IEndpointRouteBuilder endpoints, KilerData data, TimeProvider time)
    {
        endpoints.MapGet("/api/stashes", (HttpContext context) => List(context, data)).Needs(Need.Reader);
        // A new stash tells the other pages of this Kiler (M6.11), and so do the actions of an admin, which change the paths.
        endpoints.MapPost("/api/stashes", (HttpContext context, StashRequest request) => Create(context, data, request, time)).Needs(Need.Editor).ChangesData();
        endpoints.MapGet("/api/admin/stashes", (HttpContext context) => AdminList(context, data)).Needs(Need.Admin);
        endpoints.MapPut("/api/admin/stashes/{id}", (HttpContext context, string id, StashRenameRequest request) =>
            Act(context, data, request.Version, (admin, records, version) => StashActions.Rename(admin, records, id, version, request.Name, time)))
            .Needs(Need.Admin).ChangesData();
        endpoints.MapPost("/api/admin/stashes/{id}/merge", (HttpContext context, string id, StashMergeRequest request) =>
            Act(context, data, request.Version, (admin, records, version) => StashActions.Merge(admin, records, id, version, request.Into, time)))
            .Needs(Need.Admin).ChangesData();
        endpoints.MapDelete("/api/admin/stashes/{id}", (HttpContext context, string id, int? version) =>
            Act(context, data, version, (admin, records, over) => StashActions.Delete(admin, records, id, over, time)))
            .Needs(Need.Admin).ChangesData();
    }

    /// <summary>What the page says when an action of an admin on a stash was refused, but for a value that does not fit, whose reason is its own.</summary>
    public static string Message(StashResult result) => result.Outcome switch
    {
        StashOutcome.Exists => Exists,
        StashOutcome.Missing => NotThere,
        StashOutcome.Conflict => "The stash has been changed meanwhile. Read the list again and try once more.",
        StashOutcome.Used when result.Records == 1 => "A record is in this stash, so it cannot be deleted. Merge it into another stash instead.",
        StashOutcome.Used => $"{result.Records} records are in this stash, so it cannot be deleted. Merge it into another stash instead.",
        StashOutcome.Busy => "The stash is being written now. Try again in a moment.",
        StashOutcome.Offline => RecordsApi.Message(RecordOutcome.Offline),
        _ => NotWritten,
    };

    /// <summary>
    /// The stashes for the position after <c>path</c>, 0 to 3 ids, or the first position without it: every live stash that can
    /// be read, those that a record has at that position under that path marked as used and first. The path and those of the
    /// records are read through the merged and deleted stashes (<see cref="RecordSet.PathOf"/>, M6.21).
    /// </summary>
    private static Results<Ok<StashesResponse>, BadRequest<ErrorResponse>, Conflict<ErrorResponse>> List(HttpContext context, KilerData data)
    {
        var given = context.Request.Query["path"];
        string[] path = [];
        if (given.ToString() is { Length: > 0 } text)
        {
            path = text.Split(',');
        }

        if (given.Count > 1 || path.Length >= RecordFile.MaxStashes || !path.All(ItemId.IsValid))
        {
            return TypedResults.BadRequest(new ErrorResponse($"path is not 0 to {RecordFile.MaxStashes - 1} ids of stashes."));
        }

        if (data.Records is not { } records)
        {
            return TypedResults.Conflict(new ErrorResponse(SessionApi.NoData));
        }

        var set = records.Read(AccessRules.SessionOf(context));
        var start = set.PathOf(path);
        var used = set.Records.Values
            .Select(view => view.File.Model)
            .OfType<RecordFile>()
            .Select(record => set.PathOf(record.Stash))
            .Where(stash => stash.Count > start.Count && stash.Take(start.Count).SequenceEqual(start, StringComparer.Ordinal))
            .Select(stash => stash[start.Count])
            .ToHashSet(StringComparer.Ordinal);
        var stashes = set.Stashes.Values
            .Select(entry => entry.Model)
            .OfType<StashFile>()
            .Where(stash => stash.IsLive)
            .Select(stash => new StashChoiceResponse(stash.Id, stash.Name, used.Contains(stash.Id)))
            .OrderByDescending(stash => stash.Used)
            .ThenBy(stash => stash.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(stash => stash.Id, StringComparer.Ordinal);
        return TypedResults.Ok(new StashesResponse([.. stashes]));
    }

    private static Results<Created<PathStashResponse>, BadRequest<WriteErrorResponse>, Conflict<ErrorResponse>> Create(
        HttpContext context, KilerData data, StashRequest request, TimeProvider time)
    {
        // Need.Editor lets no request through without a session.
        var author = AccessRules.SessionOf(context) ?? throw new InvalidOperationException("A new stash has no session.");
        if (data.Records is not { } records)
        {
            return TypedResults.Conflict(new ErrorResponse(SessionApi.NoData));
        }

        var result = RecordActions.NewStash(author, records, request.Name, time);
        return result.Outcome switch
        {
            // No endpoint gives a stash alone, so the 201 has no address.
            RecordOutcome.Done => TypedResults.Created((string?)null, new PathStashResponse(result.Stash!.Id, result.Stash.Name)),
            RecordOutcome.Invalid => TypedResults.BadRequest(new WriteErrorResponse(result.Invalid!, result.Field)),
            RecordOutcome.Exists => TypedResults.Conflict(new ErrorResponse(Exists)),
            RecordOutcome.Offline => TypedResults.Conflict(new ErrorResponse(RecordsApi.Message(result.Outcome))),
            _ => TypedResults.Conflict(new ErrorResponse(NotWritten)),
        };
    }

    /// <summary>The live stashes with the number of the shared records of each, for the admin's screen (<see cref="StashActions.List"/>).</summary>
    private static Results<Ok<AdminStashesResponse>, Conflict<ErrorResponse>> AdminList(HttpContext context, KilerData data)
    {
        if (data.Records is not { } records)
        {
            return TypedResults.Conflict(new ErrorResponse(SessionApi.NoData));
        }

        return TypedResults.Ok(AdminStashes(records.Read(AccessRules.SessionOf(context))));
    }

    /// <summary>
    /// An action of an admin on a stash over <paramref name="version"/>, the version that the admin read: 200 with the list as
    /// it is after the write; 400 without a version, as for an edit, or with a value that does not fit; 404 for a stash that is
    /// not there; 409 for the rest (<see cref="Message"/>).
    /// </summary>
    private static Results<Ok<AdminStashesResponse>, BadRequest<WriteErrorResponse>, NotFound<ErrorResponse>, Conflict<ErrorResponse>> Act(
        HttpContext context, KilerData data, int? version, Func<Session, Records, int, StashResult> action)
    {
        // Need.Admin lets no request through without a session.
        var admin = AccessRules.SessionOf(context) ?? throw new InvalidOperationException("An action on a stash has no session.");
        if (version is not { } over)
        {
            return TypedResults.BadRequest(new WriteErrorResponse(RecordsApi.NoVersion, null));
        }

        if (data.Records is not { } records)
        {
            return TypedResults.Conflict(new ErrorResponse(SessionApi.NoData));
        }

        var result = action(admin, records, over);
        return result.Outcome switch
        {
            StashOutcome.Done => TypedResults.Ok(AdminStashes(records.Read(admin))),
            StashOutcome.Invalid => TypedResults.BadRequest(new WriteErrorResponse(result.Invalid!, result.Field)),
            StashOutcome.Missing => TypedResults.NotFound(new ErrorResponse(Message(result))),
            _ => TypedResults.Conflict(new ErrorResponse(Message(result))),
        };
    }

    private static AdminStashesResponse AdminStashes(RecordSet set) =>
        new([.. StashActions.List(set).Select(stash => new AdminStashResponse(stash.Id, stash.Name, stash.Version, stash.Records))]);
}

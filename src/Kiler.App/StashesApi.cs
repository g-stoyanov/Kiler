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

/// <summary>
/// The stashes of the form of a record (M5.9, docs/api.md, design 6.6): <c>GET /api/stashes</c>, the stashes for the drop-down
/// of a position of the path, which needs <see cref="Need.Reader"/>; and <c>POST /api/stashes</c>, a new stash
/// (<see cref="RecordActions.NewStash(Session, Records, string?, TimeProvider)"/>), which needs <see cref="Need.Editor"/>.
/// </summary>
internal static class StashesApi
{
    /// <summary>The 409 of a new stash whose name a stash has, without regard to case.</summary>
    public const string Exists = "There is a stash of that name already.";

    /// <summary>The 409 of a new stash that the layer did not write, but offline.</summary>
    public const string NotWritten = "The stash could not be written to the shared folder. Try again in a moment.";

    public static void Map(IEndpointRouteBuilder endpoints, KilerData data, TimeProvider time)
    {
        endpoints.MapGet("/api/stashes", (HttpContext context) => List(context, data)).Needs(Need.Reader);
        // A new stash tells the other pages of this Kiler (M6.11).
        endpoints.MapPost("/api/stashes", (HttpContext context, StashRequest request) => Create(context, data, request, time)).Needs(Need.Editor).ChangesData();
    }

    /// <summary>
    /// The stashes for the position after <c>path</c>, 0 to 3 ids, or the first position without it: every stash that can be
    /// read, those that a record has at that position under that path marked as used and first.
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
        var used = set.Records.Values
            .Select(view => view.File.Model)
            .OfType<RecordFile>()
            .Where(record => record.Stash.Count > path.Length && record.Stash.Take(path.Length).SequenceEqual(path, StringComparer.Ordinal))
            .Select(record => record.Stash[path.Length])
            .ToHashSet(StringComparer.Ordinal);
        var stashes = set.Stashes.Values
            .Select(entry => entry.Model)
            .OfType<StashFile>()
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
}

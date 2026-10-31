using Kiler.Core;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Kiler.App;

/// <summary>A view of a secret of the top level as the audit screen shows it (docs/api.md, <c>GET /api/admin/audit</c>).</summary>
/// <param name="At">When, in UTC.</param>
/// <param name="Account">Who: the account of the file of the line.</param>
/// <param name="Record">The id of the record.</param>
/// <param name="Name">The name of the record at that moment, so the screen needs no record, also for one deleted since.</param>
/// <param name="Field">The key of the field.</param>
/// <param name="Use"><c>show</c> or <c>copy</c>.</param>
/// <param name="Level">The level of the field in the record.</param>
internal sealed record AuditEntryResponse(DateTime At, string Account, string Record, string Name, string Field, string Use, int Level);

/// <param name="Entries">The entries of the span, the newest first.</param>
/// <param name="Skipped">
/// The lines that do not fit, and each file or folder of the audit that could not be read whole, once (<see cref="AuditSpan"/>).
/// </param>
internal sealed record AuditResponse(IReadOnlyList<AuditEntryResponse> Entries, int Skipped);

/// <summary>
/// <c>GET /api/admin/audit?from=&lt;UTC&gt;&amp;to=&lt;UTC&gt;</c> (M6.18, docs/api.md): the views of the secrets of the top level
/// with <c>from</c> ≤ <c>at</c> &lt; <c>to</c>, read from the share at each request (<see cref="Audit.Read"/>), for an admin.
/// A span that cannot be read is 400; offline, a share that cannot be reached and no data are 409.
/// </summary>
internal static class AuditApi
{
    /// <summary>The 400 of a <c>from</c> or <c>to</c> that is missing, given twice or not a time in UTC.</summary>
    public const string NoSpan = "from and to are times in UTC, such as 2026-10-31T00:00:00Z.";

    /// <summary>The 409 of a read of the audit that the shared folder refused, offline too.</summary>
    public const string NotRead = "The audit cannot be read from the shared folder now. Try again once it can be reached.";

    public static void Map(IEndpointRouteBuilder endpoints, KilerData data) =>
        endpoints.MapGet("/api/admin/audit", (HttpContext context) => Read(context, data)).Needs(Need.Admin);

    private static Results<Ok<AuditResponse>, BadRequest<ErrorResponse>, Conflict<ErrorResponse>> Read(HttpContext context, KilerData data)
    {
        if (!Audit.TryReadTime(One(context, "from"), out var from) || !Audit.TryReadTime(One(context, "to"), out var to))
        {
            return TypedResults.BadRequest(new ErrorResponse(NoSpan));
        }

        if (Audit.InvalidSpan(from, to) is { } reason)
        {
            return TypedResults.BadRequest(new ErrorResponse(reason));
        }

        if (data.Files is null || data.Copy is not { } copy)
        {
            return TypedResults.Conflict(new ErrorResponse(SessionApi.NoData));
        }

        AuditSpan span;
        try
        {
            span = Audit.Read(copy, from, to);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // OfflineException is an IOException; a share that cannot be reached throws as for a load (storage.md, "The audit").
            return TypedResults.Conflict(new ErrorResponse(NotRead));
        }

        return TypedResults.Ok(new AuditResponse(
            [.. span.Entries.Select(entry => new AuditEntryResponse(entry.At, entry.Account, entry.Record, entry.Name, entry.Field, Audit.NameOf(entry.Use), entry.Level))],
            span.Skipped));
    }

    /// <summary>The value of a part of the query given once; null when it is missing or given more than once.</summary>
    private static string? One(HttpContext context, string name) => context.Request.Query[name] is { Count: 1 } values ? values[0] : null;
}

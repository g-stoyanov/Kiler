using Kiler.Core;

namespace Kiler.App;

/// <summary>What an endpoint needs (<see cref="AccessRules.Needs"/>), in its metadata.</summary>
internal sealed record NeedMetadata(Need Need);

/// <summary>
/// The rights on every request (M4.4, step 9 of M4): each endpoint states what it needs (<see cref="Needs"/>), and this check
/// runs after routing, before the endpoint, so it knows the endpoint. A <see cref="RouteEndpoint"/> that states nothing is a
/// fault of the code and answers 500, never as one that needs nothing. A request that does not get what its endpoint needs is 403 with the
/// reason (<see cref="ErrorResponse"/>); 401 stays the answer of a missing cookie (<see cref="AccessGuard"/>). An endpoint
/// that uses the keys finds the session that was checked with <see cref="SessionOf"/>, and a lock while it runs answers as
/// locked (<see cref="SessionLockedException"/>).
/// </summary>
internal static class AccessRules
{
    public const string Locked = "The session has been locked. Sign in again.";

    public static void Use(IApplicationBuilder app, CurrentSession sessions, KilerData data) =>
        app.Use((context, next) => CheckAsync(context, next, sessions, data));

    /// <summary>States what the endpoint needs.</summary>
    public static TBuilder Needs<TBuilder>(this TBuilder endpoint, Need need)
        where TBuilder : IEndpointConventionBuilder =>
        endpoint.WithMetadata(new NeedMetadata(need));

    /// <summary>The session that the check let through; null for a guest, and for an endpoint that needs nothing.</summary>
    public static Session? SessionOf(HttpContext context) => context.Features.Get<Session>();

    /// <summary>What the page says when a request does not get what its endpoint needs.</summary>
    public static string Message(AccessRefusal refusal) => refusal switch
    {
        AccessRefusal.NotSignedIn => "Sign in first.",
        AccessRefusal.MustChangePassword => "Change your temporary password first.",
        AccessRefusal.CannotEdit => "Your account cannot edit.",
        _ => "Only an admin who holds the keys of every level can do this.",
    };

    private static async Task CheckAsync(HttpContext context, RequestDelegate next, CurrentSession sessions, KilerData data)
    {
        // The UI files and an address that no endpoint takes; and the endpoints that routing makes of its own for a method or
        // a content type that the endpoint of the address does not take, which only answer 405 or 415 and are no RouteEndpoint.
        if (context.GetEndpoint() is not RouteEndpoint endpoint)
        {
            await next(context);
            return;
        }

        var need = endpoint.Metadata.GetMetadata<NeedMetadata>()?.Need
            ?? throw new InvalidOperationException($"The endpoint {endpoint.DisplayName} does not state what it needs.");
        if (need == Need.Nothing)
        {
            await next(context);
            return;
        }

        // Read once: a session that a lock disposes from here on stays this request's, and its keys answer as locked.
        var session = sessions.Session is { IsLocked: false } current ? current : null;
        if (Access.Check(need, session, data.Files) is { } refusal)
        {
            await RefuseAsync(context, Message(refusal));
            return;
        }

        context.Features.Set(session);

        try
        {
            await next(context);
        }
        catch (SessionLockedException) when (!context.Response.HasStarted)
        {
            await RefuseAsync(context, Locked);
        }
    }

    private static Task RefuseAsync(HttpContext context, string error)
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return context.Response.WriteAsJsonAsync(new ErrorResponse(error), KilerJsonContext.Default.ErrorResponse);
    }
}

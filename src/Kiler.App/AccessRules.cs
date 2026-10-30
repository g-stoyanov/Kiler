using Kiler.Core;

namespace Kiler.App;

/// <summary>What an endpoint needs (<see cref="AccessRules.Needs"/>), in its metadata.</summary>
internal sealed record NeedMetadata(Need Need);

/// <summary>A request of the endpoint is no activity for the automatic lock (<see cref="AccessRules.NotActivity"/>), in its metadata.</summary>
internal sealed record NotActivityMetadata;

/// <summary>
/// The rights on every request (M4.4, step 9 of M4): each endpoint states what it needs (<see cref="Needs"/>), and this check
/// runs after routing, before the endpoint, so it knows the endpoint. A <see cref="RouteEndpoint"/> that states nothing is a
/// fault of the code and answers 500, never as one that needs nothing. A request that does not get what its endpoint needs is 403 with the
/// reason (<see cref="ErrorResponse"/>); 401 stays the answer of a missing cookie (<see cref="AccessGuard"/>). An endpoint
/// that uses the keys finds the session that was checked with <see cref="SessionOf"/>, and a lock while it runs answers as
/// locked (<see cref="SessionLockedException"/>). Each request of an endpoint is an activity of the page for the automatic lock
/// (<see cref="AutoLock"/>, M4.5b), but those of the endpoints that a page asks of its own (<see cref="NotActivity"/>).
/// </summary>
internal static class AccessRules
{
    public const string Locked = "The session has been locked. Sign in again.";

    public static void Use(IApplicationBuilder app, CurrentSession sessions, KilerData data, AutoLock autoLock) =>
        app.Use((context, next) => CheckAsync(context, next, sessions, data, autoLock));

    /// <summary>States what the endpoint needs.</summary>
    public static TBuilder Needs<TBuilder>(this TBuilder endpoint, Need need)
        where TBuilder : IEndpointConventionBuilder =>
        endpoint.WithMetadata(new NeedMetadata(need));

    /// <summary>
    /// States that a request of the endpoint does not put the automatic lock off: a page asks it of its own, again and again,
    /// as the event stream, the health check and the state of the data (decided at the start of M4 and in M4.5).
    /// </summary>
    public static TBuilder NotActivity<TBuilder>(this TBuilder endpoint)
        where TBuilder : IEndpointConventionBuilder =>
        endpoint.WithMetadata(new NotActivityMetadata());

    /// <summary>The session that the check let through; null for a guest, and for an endpoint that needs nothing.</summary>
    public static Session? SessionOf(HttpContext context) => context.Features.Get<Session>();

    /// <summary>What the page says when a request does not get what its endpoint needs.</summary>
    public static string Message(AccessRefusal refusal) => refusal switch
    {
        AccessRefusal.NotSignedIn => "Sign in first.",
        AccessRefusal.MustChangePassword => "Change your temporary password first.",
        AccessRefusal.CannotEdit => "Your account cannot edit.",
        AccessRefusal.LevelsChanged => "levels.json has changed since you signed in. Sign in again.",
        _ => "Only an admin who holds the keys of every level can do this.",
    };

    private static async Task CheckAsync(HttpContext context, RequestDelegate next, CurrentSession sessions, KilerData data, AutoLock autoLock)
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

        // Before the session is read, so the automatic lock seldom comes between the check and the endpoint; one that does
        // makes the keys answer as locked, as the "Lock" button does. A refused request is the user's too.
        if (endpoint.Metadata.GetMetadata<NotActivityMetadata>() is null)
        {
            autoLock.Activity();
        }

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

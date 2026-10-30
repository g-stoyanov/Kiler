using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Kiler.Core;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Kiler.App;

/// <summary>
/// <c>GET /api/events</c>: a stream of server-sent events that every open Kiler page holds, so the server knows that a
/// page is open (<see cref="Heartbeat"/>). It sends <c>ping</c> at once and then every
/// <see cref="HeartbeatTimes.PingInterval"/>, and <c>session</c> at each sign-in and lock (<see cref="CurrentSession.Changed"/>,
/// M4.5b), on which the page drops what it shows and reads the session again. Each event has a name and its data as a string,
/// so other events, such as <c>changed</c> in M6, go into the same stream. A page asks it of its own, so it is no activity
/// for the automatic lock.
/// </summary>
internal static class EventsApi
{
    private static readonly SseItem<string> Ping = new(string.Empty, "ping");

    /// <summary>No data: the page reads the session from its endpoint, which says only what the page may know.</summary>
    private static readonly SseItem<string> SessionChanged = new(string.Empty, "session");

    public static void Map(IEndpointRouteBuilder endpoints, Heartbeat heartbeat, CurrentSession sessions, TimeSpan pingInterval, TimeProvider time, CancellationToken stopping) =>
        endpoints.MapGet("/api/events", Results<ServerSentEventsResult<string>, StatusCodeHttpResult> (HttpContext context) =>
        {
            // The server is stopping: the page gets an error and does not try again.
            if (heartbeat.TryConnect() is not { } connection)
            {
                return TypedResults.StatusCode(StatusCodes.Status503ServiceUnavailable);
            }

            // Disposed when the request ends, also when the stream never starts.
            context.Response.RegisterForDispose(connection);
            return TypedResults.ServerSentEvents(Events(sessions, pingInterval, time, stopping));
        }).Needs(Need.Nothing).NotActivity();

    /// <summary>
    /// <c>ping</c> at once, which also sends the headers, and then every <paramref name="pingInterval"/>; <c>session</c> after
    /// each change of <paramref name="sessions"/>, before a <c>ping</c> that is due with it. Changes that come before the
    /// stream has sent the <c>session</c> of an earlier one give one <c>session</c> together, as it carries no data. The stream
    /// ends when the page goes away (<paramref name="cancellationToken"/>, the request's) or when the server stops: Kestrel
    /// waits for open requests before it stops, so a stream that stayed open would hold the stop up. It ends normally, as an
    /// exception after the headers would leave the response without its end.
    /// </summary>
    public static async IAsyncEnumerable<SseItem<string>> Events(
        CurrentSession sessions, TimeSpan pingInterval, TimeProvider time, CancellationToken stopping, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var end = CancellationTokenSource.CreateLinkedTokenSource(stopping, cancellationToken);
        using var timer = new PeriodicTimer(pingInterval, time);
        var changes = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
        void OnChanged(object? sender, EventArgs e) => changes.Writer.TryWrite(true);
        sessions.Changed += OnChanged;
        try
        {
            yield return Ping;
            Task<bool>? tick = null;
            Task<bool>? change = null;
            while (true)
            {
                // Only one wait of the timer may be under way, so a wait that the other one beat is kept for the next round.
                tick ??= timer.WaitForNextTickAsync(end.Token).AsTask();
                change ??= changes.Reader.WaitToReadAsync(end.Token).AsTask();
                // WhenAny does not throw: the end cancels both waits.
                await Task.WhenAny(tick, change);
                if (end.IsCancellationRequested)
                {
                    yield break;
                }

                // One event a round, and the change first, also when the tick has come too.
                if (change.IsCompleted)
                {
                    change = null;
                    changes.Reader.TryRead(out _);
                    yield return SessionChanged;
                }
                else
                {
                    tick = null;
                    yield return Ping;
                }
            }
        }
        finally
        {
            sessions.Changed -= OnChanged;
        }
    }
}

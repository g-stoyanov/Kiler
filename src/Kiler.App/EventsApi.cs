using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Kiler.Core;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Kiler.App;

/// <summary>An endpoint whose answer of success has changed the stashes, the records or the trash (<see cref="EventsApi.ChangesData"/>), in its metadata.</summary>
internal sealed record ChangesDataMetadata;

/// <summary>
/// <c>GET /api/events</c>: a stream of server-sent events that every open Kiler page holds, so the server knows that a
/// page is open (<see cref="Heartbeat"/>). It sends <c>ping</c> at once and then every
/// <see cref="HeartbeatTimes.PingInterval"/>, <c>session</c> at each sign-in and lock (<see cref="CurrentSession.Changed"/>,
/// M4.5b), on which the page drops what it shows and reads the session again, and <c>changed</c> at each change of the data
/// (<see cref="DataChanges"/>, M6.11), on which the page reads again what it shows. Each event has a name and its data as a
/// string; none carries data, as the page reads from the endpoints, which say only what the session may know. A page asks it
/// of its own, so it is no activity for the automatic lock.
/// </summary>
internal static class EventsApi
{
    private static readonly SseItem<string> Ping = new(string.Empty, "ping");

    /// <summary>No data: the page reads the session from its endpoint, which says only what the page may know.</summary>
    private static readonly SseItem<string> SessionChanged = new(string.Empty, "session");

    /// <summary>No data: the page reads again what it shows, which also says only what the session may read.</summary>
    private static readonly SseItem<string> DataChanged = new(string.Empty, "changed");

    public static void Map(
        IEndpointRouteBuilder endpoints, Heartbeat heartbeat, CurrentSession sessions, DataChanges changes, TimeSpan pingInterval, TimeProvider time, CancellationToken stopping) =>
        endpoints.MapGet("/api/events", Results<ServerSentEventsResult<string>, StatusCodeHttpResult> (HttpContext context) =>
        {
            // The server is stopping: the page gets an error and does not try again.
            if (heartbeat.TryConnect() is not { } connection)
            {
                return TypedResults.StatusCode(StatusCodes.Status503ServiceUnavailable);
            }

            // Disposed when the request ends, also when the stream never starts.
            context.Response.RegisterForDispose(connection);
            return TypedResults.ServerSentEvents(Events(sessions, changes, pingInterval, time, stopping));
        }).Needs(Need.Nothing).NotActivity();

    /// <summary>
    /// States that an answer of success of the endpoint has changed the stashes, the records or the trash, so the event stream
    /// sends <c>changed</c> after it (<see cref="Use"/>): a write of this Kiler, which no load gives (M2.7a).
    /// </summary>
    public static TBuilder ChangesData<TBuilder>(this TBuilder endpoint)
        where TBuilder : IEndpointConventionBuilder =>
        endpoint.WithMetadata(new ChangesDataMetadata());

    /// <summary>
    /// Raises <paramref name="changes"/> after each answer of success of an endpoint with <see cref="ChangesData"/>. After
    /// routing, so it knows the endpoint, and after the rights, so a refused request does not reach it.
    /// </summary>
    public static void Use(IApplicationBuilder app, DataChanges changes) =>
        app.Use(async (context, next) =>
        {
            await next(context);
            if (context.GetEndpoint()?.Metadata.GetMetadata<ChangesDataMetadata>() is not null && context.Response.StatusCode is >= 200 and < 300)
            {
                changes.Raise();
            }
        });

    /// <summary>
    /// <c>ping</c> at once, which also sends the headers, and then every <paramref name="pingInterval"/>; <c>session</c> after
    /// each change of <paramref name="sessions"/>, and <c>changed</c> after each of <paramref name="changes"/>, before a
    /// <c>ping</c> that is due with them, and <c>session</c> first. Changes that come before the stream has sent the event of an
    /// earlier one give one event together, as it carries no data. The stream ends when the page goes away
    /// (<paramref name="cancellationToken"/>, the request's) or when the server stops: Kestrel waits for open requests before it
    /// stops, so a stream that stayed open would hold the stop up. It ends normally, as an exception after the headers would
    /// leave the response without its end.
    /// </summary>
    public static async IAsyncEnumerable<SseItem<string>> Events(
        CurrentSession sessions,
        DataChanges changes,
        TimeSpan pingInterval,
        TimeProvider time,
        CancellationToken stopping,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var end = CancellationTokenSource.CreateLinkedTokenSource(stopping, cancellationToken);
        using var timer = new PeriodicTimer(pingInterval, time);
        var sessionChanges = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
        var dataChanges = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
        void OnSessionChanged(object? sender, EventArgs e) => sessionChanges.Writer.TryWrite(true);
        void OnDataChanged(object? sender, EventArgs e) => dataChanges.Writer.TryWrite(true);
        sessions.Changed += OnSessionChanged;
        changes.Changed += OnDataChanged;
        try
        {
            yield return Ping;
            Task<bool>? tick = null;
            Task<bool>? session = null;
            Task<bool>? data = null;
            while (true)
            {
                // Only one wait of the timer may be under way, so a wait that another one beat is kept for the next round.
                tick ??= timer.WaitForNextTickAsync(end.Token).AsTask();
                session ??= sessionChanges.Reader.WaitToReadAsync(end.Token).AsTask();
                data ??= dataChanges.Reader.WaitToReadAsync(end.Token).AsTask();
                // WhenAny does not throw: the end cancels the waits.
                await Task.WhenAny(tick, session, data);
                if (end.IsCancellationRequested)
                {
                    yield break;
                }

                // A wait of a channel that has ended is made again in the next round. What has come is read from the channels,
                // not from the waits: a channel ends its waiter on the thread pool after the write, so the wait of a change that
                // came first can still be running when the other has ended (M6.11).
                if (session.IsCompleted)
                {
                    session = null;
                }

                if (data.IsCompleted)
                {
                    data = null;
                }

                // One event a round: the session first, then the data, then a ping that is due; none when a wait has ended
                // for a change that an earlier round sent.
                if (sessionChanges.Reader.TryRead(out _))
                {
                    yield return SessionChanged;
                }
                else if (dataChanges.Reader.TryRead(out _))
                {
                    yield return DataChanged;
                }
                else if (tick.IsCompleted)
                {
                    tick = null;
                    yield return Ping;
                }
            }
        }
        finally
        {
            sessions.Changed -= OnSessionChanged;
            changes.Changed -= OnDataChanged;
        }
    }
}

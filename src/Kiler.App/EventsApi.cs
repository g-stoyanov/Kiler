using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using Kiler.Core;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Kiler.App;

/// <summary>
/// <c>GET /api/events</c>: a stream of server-sent events that every open Kiler page holds, so the server knows that a
/// page is open (<see cref="Heartbeat"/>). It sends <c>ping</c> at once and then every
/// <see cref="HeartbeatTimes.PingInterval"/>. Each event has a name and its data as a string, so other events, such as
/// <c>changed</c> in M6, go into the same stream.
/// </summary>
internal static class EventsApi
{
    private static readonly SseItem<string> Ping = new(string.Empty, "ping");

    public static void Map(IEndpointRouteBuilder endpoints, Heartbeat heartbeat, TimeSpan pingInterval, TimeProvider time, CancellationToken stopping) =>
        endpoints.MapGet("/api/events", Results<ServerSentEventsResult<string>, StatusCodeHttpResult> (HttpContext context) =>
        {
            // The server is stopping: the page gets an error and does not try again.
            if (heartbeat.TryConnect() is not { } connection)
            {
                return TypedResults.StatusCode(StatusCodes.Status503ServiceUnavailable);
            }

            // Disposed when the request ends, also when the stream never starts.
            context.Response.RegisterForDispose(connection);
            return TypedResults.ServerSentEvents(Events(pingInterval, time, stopping));
        }).Needs(Need.Nothing);

    /// <summary>
    /// <c>ping</c> at once, which also sends the headers, and then every <paramref name="pingInterval"/>. The stream ends
    /// when the page goes away (<paramref name="cancellationToken"/>, the request's) or when the server stops: Kestrel
    /// waits for open requests before it stops, so a stream that stayed open would hold the stop up.
    /// </summary>
    public static async IAsyncEnumerable<SseItem<string>> Events(
        TimeSpan pingInterval, TimeProvider time, CancellationToken stopping, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var end = CancellationTokenSource.CreateLinkedTokenSource(stopping, cancellationToken);
        using var timer = new PeriodicTimer(pingInterval, time);
        yield return Ping;
        while (await WaitForTickAsync(timer, end.Token))
        {
            yield return Ping;
        }
    }

    /// <summary>
    /// False at the end. The cancellation is caught here, because the stream must end normally: an exception after the
    /// headers would leave the response without its end.
    /// </summary>
    private static async Task<bool> WaitForTickAsync(PeriodicTimer timer, CancellationToken end)
    {
        try
        {
            return await timer.WaitForNextTickAsync(end);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}

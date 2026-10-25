using System.Net;
using System.Net.ServerSentEvents;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Kiler.App.Tests;

/// <summary><c>/api/events</c>: the stream on a fake clock, and what reaches the client of the real Kestrel.</summary>
public class EventsApiTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan Tick = TimeSpan.FromTicks(1);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Events_SendPingAtOnceAndThenEveryInterval()
    {
        var time = new FakeTimeProvider();
        await using var events = EventsApi.Events(Interval, time, CancellationToken.None).GetAsyncEnumerator(Cancel);

        var first = events.MoveNextAsync();
        Assert.True(first.IsCompletedSuccessfully);
        Assert.True(await first);
        AssertPing(events.Current);

        for (var i = 0; i < 3; i++)
        {
            var next = events.MoveNextAsync().AsTask();
            time.Advance(Interval - Tick);
            Assert.False(next.IsCompleted);

            time.Advance(Tick);
            Assert.True(await next.WaitAsync(Timeout, Cancel));
            AssertPing(events.Current);
        }
    }

    [Fact]
    public async Task Events_WhenTheServerStops_EndWithoutAnError()
    {
        using var stopping = new CancellationTokenSource();
        await using var events = EventsApi.Events(Interval, new FakeTimeProvider(), stopping.Token).GetAsyncEnumerator(Cancel);
        Assert.True(await events.MoveNextAsync());
        var next = events.MoveNextAsync().AsTask();

        await stopping.CancelAsync();

        Assert.False(await next.WaitAsync(Timeout, Cancel));
    }

    [Fact]
    public async Task Events_WhenThePageGoesAway_EndWithoutAnError()
    {
        using var aborted = new CancellationTokenSource();
        await using var events = EventsApi.Events(Interval, new FakeTimeProvider(), CancellationToken.None).GetAsyncEnumerator(aborted.Token);
        Assert.True(await events.MoveNextAsync());
        var next = events.MoveNextAsync().AsTask();

        await aborted.CancelAsync();

        Assert.False(await next.WaitAsync(Timeout, Cancel));
    }

    [Fact]
    public async Task GetEvents_WithCookie_IsAnUncachedEventStreamThatSendsPingAtOnce()
    {
        await using var server = await KilerServer.StartAsync();

        // OpenAsync reads the first event: "event: ping", an empty "data:" line and the blank line. The browser
        // dispatches an event only when it has a data line.
        using var page = await EventStream.OpenAsync(server);

        Assert.Equal("text/event-stream", page.Response.Content.Headers.ContentType?.MediaType);
        Assert.True(page.Response.Headers.CacheControl?.NoCache);
        Assert.True(page.Response.Headers.CacheControl?.NoStore);
    }

    [Fact]
    public async Task GetEvents_OpenForSomeIntervals_GetsAPingInEach()
    {
        await using var server = await KilerServer.StartAsync(new StartOptions { PingInterval = TimeSpan.FromMilliseconds(100) });
        using var page = await EventStream.OpenAsync(server);

        for (var i = 0; i < 3; i++)
        {
            Assert.Equal("event: ping\ndata: \n\n", await page.ReadEventAsync());
        }
    }

    [Fact]
    public async Task StopWithAPageOpen_EndsItsStreamAndDoesNotWaitForIt()
    {
        await using var server = await KilerServer.StartAsync();
        using var page = await EventStream.OpenAsync(server);

        // Kestrel waits up to 30 s for open requests, so a stream that stayed open would hold the stop that long.
        await server.StopAsync().WaitAsync(TimeSpan.FromSeconds(5), Cancel);

        Assert.Null(await page.ReadEventAsync());
    }

    [Fact]
    public async Task WhileKilerStops_EventsAndHealthAnswer503()
    {
        // What a second start or a page sees between the start of the stop and Kestrel letting the port go: here
        // nothing waits for the stop, so Kestrel keeps listening.
        await using var app = KilerHost.Build(new StartOptions());
        await app.StartAsync(Cancel);
        try
        {
            app.Lifetime.StopApplication();
            using var client = new HttpClient { BaseAddress = new Uri(Assert.Single(app.Urls)) };
            var token = app.Services.GetRequiredService<AccessToken>();
            client.DefaultRequestHeaders.Add("Cookie", $"{InstancePlace.CookieNameFor(null)}={token.Value}");

            using var events = await client.GetAsync("/api/events", HttpCompletionOption.ResponseHeadersRead, Cancel);
            using var health = await client.GetAsync("/api/health", Cancel);

            Assert.Equal(HttpStatusCode.ServiceUnavailable, events.StatusCode);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, health.StatusCode);
        }
        finally
        {
            await app.StopAsync(Cancel);
        }
    }

    private static void AssertPing(SseItem<string> item)
    {
        Assert.Equal("ping", item.EventType);
        Assert.Equal(string.Empty, item.Data);
    }
}

using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;

namespace Kiler.App.Tests;

/// <summary>
/// The stop by the heartbeat on the real Kestrel, with short waits on the real clock. The ping interval keeps its
/// default of 15 s, so a closed page is noticed by the connection closing, not by a failed ping.
/// </summary>
public class ShutdownTests
{
    /// <summary>A wait that no test reaches, so the stop comes from the wait under test.</summary>
    private static readonly TimeSpan Never = TimeSpan.FromHours(1);

    /// <summary>A timer of the server may fire this much before the clock of the test says.</summary>
    private static readonly TimeSpan Early = TimeSpan.FromMilliseconds(100);

    /// <summary>The most the stop may come after its wait, on a busy machine.</summary>
    private static readonly TimeSpan Late = TimeSpan.FromSeconds(5);

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task NoPage_KilerStopsAfterTheFirstWait()
    {
        var firstWait = TimeSpan.FromSeconds(1);
        var started = Stopwatch.GetTimestamp();
        await using var server = await KilerServer.StartAsync(new StartOptions { FirstWait = firstWait, IdleWait = Never });

        await AssertStopsAfterAsync(server, started, firstWait);
    }

    [Fact]
    public async Task PageOpensAndCloses_KilerStopsAfterTheIdleWait()
    {
        var idleWait = TimeSpan.FromSeconds(1);
        await using var server = await KilerServer.StartAsync(new StartOptions { FirstWait = Never, IdleWait = idleWait });
        using var page = await EventStream.OpenAsync(server);

        var closed = Stopwatch.GetTimestamp();
        page.Dispose();

        await AssertStopsAfterAsync(server, closed, idleWait);
    }

    [Fact]
    public async Task PageReopensWithinTheIdleWait_KilerRunsUntilItCloses()
    {
        // A page reload: the old connection closes and the new one opens.
        var idleWait = TimeSpan.FromSeconds(2);
        await using var server = await KilerServer.StartAsync(new StartOptions { FirstWait = Never, IdleWait = idleWait });
        using (await EventStream.OpenAsync(server))
        {
        }

        using var reloaded = await EventStream.OpenAsync(server);
        await Task.Delay(idleWait + TimeSpan.FromSeconds(1), Cancel);
        Assert.False(server.Stopping.IsCompleted);

        var closed = Stopwatch.GetTimestamp();
        reloaded.Dispose();

        await AssertStopsAfterAsync(server, closed, idleWait);
    }

    [Fact]
    public async Task HealthCheckDuringTheIdleWait_PutsTheStopOffByTheFirstWait()
    {
        // The last page has closed, and a second start asks before it opens the browser.
        var firstWait = TimeSpan.FromSeconds(4);
        await using var server = await KilerServer.StartAsync(new StartOptions { FirstWait = firstWait, IdleWait = TimeSpan.FromSeconds(2) });
        // The client connects before the page closes, so the health check does not wait for a new connection. A path
        // without an endpoint puts nothing off.
        using var client = server.CreateSignedInClient();
        using (var warmUp = await client.GetAsync("/api/unknown", Cancel))
        {
            Assert.Equal(HttpStatusCode.NotFound, warmUp.StatusCode);
        }

        using (await EventStream.OpenAsync(server))
        {
        }

        var asked = Stopwatch.GetTimestamp();
        using (var response = await client.GetAsync("/api/health", Cancel))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        await AssertStopsAfterAsync(server, asked, firstWait);
    }

    [Fact]
    public async Task RequestsThatTheGuardRejects_DoNotKeepKilerRunning()
    {
        var firstWait = TimeSpan.FromSeconds(1);
        var started = Stopwatch.GetTimestamp();
        await using var server = await KilerServer.StartAsync(new StartOptions { FirstWait = firstWait, IdleWait = Never });
        using var client = server.CreateClient();

        while (!server.Stopping.IsCompleted && Stopwatch.GetElapsedTime(started) < firstWait + Late)
        {
            foreach (var (request, rejected) in RejectedRequests(server))
            {
                using (request)
                {
                    try
                    {
                        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, Cancel);
                        Assert.Equal(rejected, response.StatusCode);
                    }
                    // The server stopped while the request was on the way.
                    catch (HttpRequestException) when (server.Stopping.IsCompleted)
                    {
                    }
                }
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50), Cancel);
        }

        Assert.True(server.Stopping.IsCompleted, "Kiler kept running while the guard rejected the requests.");
        await AssertStopsAfterAsync(server, started, firstWait);
    }

    /// <summary>The page and health requests that the guard turns away, each with the status it gets.</summary>
    private static IEnumerable<(HttpRequestMessage Request, HttpStatusCode Rejected)> RejectedRequests(KilerServer server)
    {
        var cookie = $"{server.CookieName}={server.Token.Value}";

        yield return (new HttpRequestMessage(HttpMethod.Get, "/api/events"), HttpStatusCode.Unauthorized);
        yield return (new HttpRequestMessage(HttpMethod.Get, "/api/health"), HttpStatusCode.Unauthorized);

        var crossSite = new HttpRequestMessage(HttpMethod.Get, "/api/events");
        crossSite.Headers.Add("Cookie", cookie);
        crossSite.Headers.Add("Sec-Fetch-Site", "cross-site");
        yield return (crossSite, HttpStatusCode.Forbidden);

        foreach (var path in new[] { "/api/events", "/api/health" })
        {
            var foreignHost = new HttpRequestMessage(HttpMethod.Get, path);
            foreignHost.Headers.Add("Cookie", cookie);
            foreignHost.Headers.Host = $"kiler.example:{server.Port}";
            yield return (foreignHost, HttpStatusCode.BadRequest);
        }
    }

    /// <summary>
    /// The host starts to stop <paramref name="wait"/> after <paramref name="from"/>, not sooner and not much later, then
    /// lets the port go. <paramref name="from"/> is taken before the action that starts the wait.
    /// </summary>
    private static async Task AssertStopsAfterAsync(KilerServer server, long from, TimeSpan wait)
    {
        var left = wait + Late - Stopwatch.GetElapsedTime(from);
        await server.Stopping.WaitAsync(left > TimeSpan.Zero ? left : TimeSpan.Zero, Cancel);
        var elapsed = Stopwatch.GetElapsedTime(from);
        Assert.True(elapsed >= wait - Early, $"Kiler stopped after {elapsed}, before its wait of {wait}.");

        await server.Stopped.WaitAsync(Late, Cancel);
        var listeners = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners();
        Assert.DoesNotContain(listeners, endpoint => endpoint.Port == server.Port);
    }
}

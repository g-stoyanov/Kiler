using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Kiler.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Kiler.App.Tests;

/// <summary>
/// The automatic lock (M4.5b) in Kiler on a fake clock, with a made-up Kiler (<see cref="MadeUpKiler"/>): which requests put it
/// off, the minutes of <c>settings.json</c>, and the <c>session</c> event at each sign-in and lock. A page holds the event
/// stream from the start, as the heartbeat runs on the same clock and would stop Kiler without one.
/// </summary>
public sealed class AutoLockApiTests : IDisposable
{
    private const string Reader = "/api/test/reader";
    private const string SessionEvent = "event: session\ndata: \n\n";

    /// <summary>The longest wait for a load of a few files on the local disk, on a busy CI machine.</summary>
    private static readonly TimeSpan Quick = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Tick = TimeSpan.FromTicks(1);
    private static readonly TimeSpan Default = TimeSpan.FromMinutes(AutoLock.DefaultMinutes);

    private readonly TempFolder _folder = new();
    private readonly LineWriter _output = new();
    private readonly LineWriter _errors = new();
    private readonly FakeTimeProvider _time = new();

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        _output.Dispose();
        _errors.Dispose();
        _folder.Dispose();
    }

    [Fact]
    public async Task WithoutActivity_TheSessionLocks_ThePageGetsSession_AndARequestAfterItIsRefused()
    {
        using var data = NewData();
        await using var server = await StartAsync(data);
        var sessions = server.Services.GetRequiredService<CurrentSession>();
        using var page = await EventStream.OpenAsync(server);
        using var client = server.CreateSignedInClient();
        await SignInAsync(client);
        Assert.Equal(SessionEvent, await ReadAfterPingsAsync(page));

        _time.Advance(Default - Tick);
        // What a page asks of its own, again and again, does not put the lock off.
        using (var health = await client.GetAsync("/api/health", Cancel))
        using (var state = await client.GetAsync("/api/data", Cancel))
        {
            Assert.Equal(HttpStatusCode.OK, health.StatusCode);
            Assert.Equal(HttpStatusCode.OK, state.StatusCode);
        }

        using (await EventStream.OpenAsync(server))
        {
        }

        Assert.NotNull(sessions.Session);
        _time.Advance(Tick);

        Assert.Null(sessions.Session);
        Assert.Equal(SessionEvent, await ReadAfterPingsAsync(page));
        using (var refused = await client.GetAsync(Reader, Cancel))
        {
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
            Assert.Equal(AccessRules.Message(AccessRefusal.NotSignedIn), (string?)(await ReadAsync(refused))["error"]);
        }

        using var session = await client.GetAsync("/api/session", Cancel);
        Assert.False((bool?)(await ReadAsync(session))["signedIn"]);
    }

    [Fact]
    public async Task ARequestOfThePage_PutsTheLockOff()
    {
        using var data = NewData();
        await using var server = await StartAsync(data);
        var sessions = server.Services.GetRequiredService<CurrentSession>();
        using var page = await EventStream.OpenAsync(server);
        using var client = server.CreateSignedInClient();
        await SignInAsync(client);

        _time.Advance(TimeSpan.FromMinutes(20));
        using (var read = await client.GetAsync(Reader, Cancel))
        {
            Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        }

        _time.Advance(Default - Tick);
        Assert.NotNull(sessions.Session);

        _time.Advance(Tick);
        Assert.Null(sessions.Session);
    }

    [Theory]
    [InlineData(5, 5)]
    [InlineData(0, AutoLock.DefaultMinutes)]
    public async Task TheMinutes_AreThoseOfTheSettings_OrTheDefaultWithoutSettingsThatFit(int inTheSettings, int minutes)
    {
        using var data = NewData(new JsonObject { ["guestMode"] = false, ["autoLockMinutes"] = inTheSettings });
        await using var server = await StartAsync(data);
        var sessions = server.Services.GetRequiredService<CurrentSession>();
        using var page = await EventStream.OpenAsync(server);
        using var client = server.CreateSignedInClient();
        await SignInAsync(client);

        _time.Advance(TimeSpan.FromMinutes(minutes) - Tick);
        Assert.NotNull(sessions.Session);

        _time.Advance(Tick);
        Assert.Null(sessions.Session);
    }

    [Fact]
    public async Task ASignInAndTheLockButton_GiveSessionToEveryPage()
    {
        using var data = NewData();
        await using var server = await StartAsync(data);
        using var first = await EventStream.OpenAsync(server);
        using var second = await EventStream.OpenAsync(server);
        using var client = server.CreateSignedInClient();

        await SignInAsync(client);

        Assert.Equal(SessionEvent, await ReadAfterPingsAsync(first));
        Assert.Equal(SessionEvent, await ReadAfterPingsAsync(second));

        using (var locked = await client.DeleteAsync("/api/session", Cancel))
        {
            Assert.Equal(HttpStatusCode.OK, locked.StatusCode);
        }

        Assert.Equal(SessionEvent, await ReadAfterPingsAsync(first));
        Assert.Equal(SessionEvent, await ReadAfterPingsAsync(second));
    }

    [Fact]
    public async Task OnlyTheStream_TheHealthCheck_AndTheStateOfTheData_AreNoActivity()
    {
        await using var app = KilerHost.Build(new StartOptions(), account: new FakeAccount(MadeUpKiler.Admin));

        var quiet = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.Metadata.GetMetadata<NotActivityMetadata>() is not null)
            .Select(endpoint => endpoint.RoutePattern.RawText)
            .Order(StringComparer.Ordinal);

        Assert.Equal(["/api/data", "/api/events", "/api/health"], quiet);
    }

    private static async Task SignInAsync(HttpClient client)
    {
        using var content = new StringContent(new JsonObject { ["password"] = MadeUpKiler.Password }.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await client.PostAsync("/api/session", content, Cancel);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>The next event that is not a <c>ping</c>: the fake clock gives a ping with each long step.</summary>
    private static async Task<string?> ReadAfterPingsAsync(EventStream page)
    {
        while (true)
        {
            var next = await page.ReadEventAsync();
            if (next != "event: ping\ndata: \n\n")
            {
                return next;
            }
        }
    }

    private static async Task<JsonObject> ReadAsync(HttpResponseMessage response)
    {
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        return Assert.IsType<JsonObject>(JsonNode.Parse(await response.Content.ReadAsStringAsync(Cancel)));
    }

    /// <summary>A data folder with the made-up Kiler and <paramref name="settings"/>, if given, and a local folder of the test.</summary>
    private KilerData NewData(JsonObject? settings = null)
    {
        var place = DataPlace.For(Path.Combine(_folder.Path, "data"), Path.Combine(_folder.Path, "Kiler"));
        MadeUpKiler.WriteTo(MadeUpData.Create(place.DataFolder, Path.Combine(_folder.Path, "writer")), guestMode: false, settings);
        return new KilerData(place, FileOwner.Instance, TimeProvider.System, _output, _errors);
    }

    /// <summary>Starts Kiler on the fake clock as the user of level 1, with a test endpoint of a reader, and waits until the copy is open.</summary>
    private async Task<KilerServer> StartAsync(KilerData data)
    {
        var app = KilerHost.Build(new StartOptions { DataFolder = data.Folder }, data: data, account: new FakeAccount(MadeUpKiler.User), time: _time);
        app.MapGet(Reader, () => "read").Needs(Need.Reader);
        var server = await KilerServer.StartAsync(app, data.Folder);
        for (var waited = Stopwatch.StartNew(); data.State != DataState.Open; await Task.Delay(TimeSpan.FromMilliseconds(20), Cancel))
        {
            Assert.True(waited.Elapsed < Quick, $"The state stayed {data.State}, not open.");
        }

        return server;
    }
}

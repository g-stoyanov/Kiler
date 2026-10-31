using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Kiler.Core;
using Microsoft.Extensions.Time.Testing;

namespace Kiler.App.Tests;

/// <summary>
/// <c>GET /api/admin/audit</c> (M6.18, docs/api.md) on a made-up Kiler with records (<see cref="MadeUpKiler.WriteRecords"/>),
/// whose top level is Prod, level 2: an admin reads the views that the request of a secret recorded, and the endpoint refuses
/// whom it must, a span that it cannot read and a share that cannot be reached. It runs on a clock that moves only when a test
/// moves it, so the times of the views are known.
/// </summary>
public sealed class AuditApiTests : IDisposable
{
    /// <summary>A caller without a session, with guest mode.</summary>
    private const string Guest = "guest";

    /// <summary>The day of the views, from its start to the start of the next, as the page asks it.</summary>
    private const string Day = "/api/admin/audit?from=2026-10-31T00:00:00Z&to=2026-11-01T00:00:00.000Z";

    /// <summary>The longest wait for a load of a few files on the local disk, on a busy CI machine.</summary>
    private static readonly TimeSpan Quick = TimeSpan.FromSeconds(30);

    private readonly TempFolder _folder = new();

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 31, 10, 0, 0, TimeSpan.Zero));

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    private string DataFolder => Path.Combine(_folder.Path, "data");

    public void Dispose() => _folder.Dispose();

    [Fact]
    public async Task AnAdmin_GetsTheViewsOfTheTopLevel_NewestFirst_AndTheCountOfTheLinesThatDoNotFit()
    {
        using var data = NewData(guestMode: false);
        await using var server = await StartAsync(data, MadeUpKiler.Admin);
        using var client = await SignedInAsync(server);
        await ShowAsync(client, "show");
        _time.Advance(TimeSpan.FromSeconds(2));
        await ShowAsync(client, "copy");
        File.AppendAllText(Path.Combine(DataFolder, "audit", "2026-10-31", MadeUpKiler.Admin + ".jsonl"), "{}\n");

        using var response = await client.GetAsync(Day, Cancel);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            """{"entries":["""
            + """{"at":"2026-10-31T10:00:02Z","account":"demo.admin","record":"k3x9q2mf","name":"Payments prod database","field":"password","use":"copy","level":2},"""
            + """{"at":"2026-10-31T10:00:00Z","account":"demo.admin","record":"k3x9q2mf","name":"Payments prod database","field":"password","use":"show","level":2}"""
            + """],"skipped":1}""",
            await response.Content.ReadAsStringAsync(Cancel));
    }

    [Theory]
    [InlineData(MadeUpKiler.User, AccessRefusal.NotAdmin)]
    [InlineData(MadeUpKiler.Editor, AccessRefusal.NotAdmin)]
    [InlineData(Guest, AccessRefusal.NotSignedIn)]
    public async Task TheAudit_IsOnlyForAnAdmin(string caller, AccessRefusal refusal)
    {
        using var data = NewData(guestMode: caller == Guest);
        await using var server = await StartAsync(data, caller == Guest ? MadeUpKiler.User : caller);
        using var client = caller == Guest ? server.CreateSignedInClient() : await SignedInAsync(server);

        using var response = await client.GetAsync(Day, Cancel);

        await AssertErrorAsync(response, HttpStatusCode.Forbidden, AccessRules.Message(refusal));
    }

    [Theory]
    [InlineData("from=2026-10-01T00:00:00Z&to=2026-11-01T00:00:01Z", "to is more than 31 days after from.")]
    [InlineData("from=2026-10-31T00:00:00Z&to=2026-10-30T00:00:00Z", "to is not after from.")]
    [InlineData("from=9999-12-30T00:00:00Z&to=9999-12-31T00:00:01Z", "to is after the last date that Kiler reads.")]
    [InlineData("from=2026-10-31T00:00:00Z", AuditApi.NoSpan)]
    [InlineData("from=2026-10-31T00:00:00Z&to=2026-11-01", AuditApi.NoSpan)]
    [InlineData("from=2026-10-31T00:00:00%2B02:00&to=2026-11-01T00:00:00Z", AuditApi.NoSpan)]
    [InlineData("from=2026-10-31T00:00:00Z&to=2026-11-01T00:00:00Z&to=2026-11-01T00:00:00Z", AuditApi.NoSpan)]
    public async Task ASpanThatCannotBeRead_IsRefused(string query, string error)
    {
        using var data = NewData(guestMode: false);
        await using var server = await StartAsync(data, MadeUpKiler.Admin);
        using var client = await SignedInAsync(server);

        using var response = await client.GetAsync("/api/admin/audit?" + query, Cancel);

        await AssertErrorAsync(response, HttpStatusCode.BadRequest, error);
    }

    [Fact]
    public async Task AShareThatCannotBeReached_IsAConflict()
    {
        using var data = NewData(guestMode: false);
        await using var server = await StartAsync(data, MadeUpKiler.Admin);
        using var client = await SignedInAsync(server);
        Directory.Move(DataFolder, DataFolder + "-away");

        using var response = await client.GetAsync(Day, Cancel);

        await AssertErrorAsync(response, HttpStatusCode.Conflict, AuditApi.NotRead);
    }

    /// <summary>Asks the password of the database for <paramref name="use"/>, which records the view.</summary>
    private static async Task ShowAsync(HttpClient client, string use)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/records/{MadeUpKiler.Database}/secret")
        {
            Content = new StringContent(new JsonObject { ["field"] = "password", ["use"] = use }.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        using var response = await client.SendAsync(request, Cancel);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task<HttpClient> SignedInAsync(KilerServer server)
    {
        var client = server.CreateSignedInClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/session")
        {
            Content = new StringContent(new JsonObject { ["password"] = MadeUpKiler.Password }.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        using var signedIn = await client.SendAsync(request, Cancel);
        Assert.Equal(HttpStatusCode.OK, signedIn.StatusCode);
        return client;
    }

    private static async Task AssertErrorAsync(HttpResponseMessage response, HttpStatusCode status, string error)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var answer = Assert.IsType<JsonObject>(JsonNode.Parse(await response.Content.ReadAsStringAsync(Cancel)));
        Assert.Equal(error, (string?)answer["error"]);
    }

    /// <summary>Starts Kiler with the data, the account and the clock of the test, and waits until the copy is open.</summary>
    private async Task<KilerServer> StartAsync(KilerData data, string account)
    {
        var app = KilerHost.Build(new StartOptions { DataFolder = data.Folder }, data: data, account: new FakeAccount(account), time: _time);
        var server = await KilerServer.StartAsync(app, data.Folder);
        for (var waited = Stopwatch.StartNew(); data.State != DataState.Open; await Task.Delay(TimeSpan.FromMilliseconds(20), Cancel))
        {
            Assert.True(waited.Elapsed < Quick, $"The state stayed {data.State}, not open.");
        }

        return server;
    }

    /// <summary>A data folder with the made-up Kiler and its records, and a local folder of the test.</summary>
    private KilerData NewData(bool guestMode)
    {
        MadeUpKiler.WriteRecords(MadeUpKiler.WriteTo(MadeUpData.Create(DataFolder, Path.Combine(_folder.Path, "writer")), guestMode));
        return new(DataPlace.For(DataFolder, Path.Combine(_folder.Path, "Kiler")), FileOwner.Instance, TimeProvider.System, TextWriter.Null, TextWriter.Null);
    }
}

using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Kiler.Core;

namespace Kiler.App.Tests;

/// <summary>
/// <c>GET /api/records</c> (M5.6, docs/api.md) on a made-up Kiler with three records (<see cref="MadeUpKiler.WriteRecords"/>):
/// what each kind of caller reads, the query, and whom it refuses. It runs with reflection-based JSON off, as Native AOT
/// does, so it reads records as the published exe does.
/// </summary>
public sealed class RecordsApiTests : IDisposable
{
    /// <summary>A caller without a session, with guest mode.</summary>
    private const string Guest = "guest";

    /// <summary>A caller without a session, without guest mode.</summary>
    private const string NoGuestMode = "no-guest-mode";

    /// <summary>The admin, signed in and then locked.</summary>
    private const string Locked = "locked";

    /// <summary>The longest wait for a load of a few files on the local disk, on a busy CI machine.</summary>
    private static readonly TimeSpan Quick = TimeSpan.FromSeconds(30);

    private readonly TempFolder _folder = new();

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    public void Dispose() => _folder.Dispose();

    [Theory]
    [InlineData(Guest, """{"key":"host","level":1,"kind":"locked","value":null,"levelName":"Team"}""")]
    [InlineData(MadeUpKiler.User, """{"key":"host","level":1,"kind":"value","value":"db01.example.test","levelName":null}""")]
    [InlineData(MadeUpKiler.Admin, """{"key":"host","level":1,"kind":"value","value":"db01.example.test","levelName":null}""")]
    public async Task EveryReader_GetsTheRecordsAtTheirLevels_WithTheTreeAndTheFacets_AndNoSecret(string caller, string host)
    {
        using var data = NewData(guestMode: caller == Guest);
        await using var server = await StartAsync(data, caller == Guest ? MadeUpKiler.User : caller);
        using var client = caller == Guest ? server.CreateSignedInClient() : await SignedInAsync(server);

        var (answer, text) = await GetAsync(client, "/api/records");

        var records = answer["records"]!.AsArray();
        Assert.Equal(["Payments monitoring", "Payments prod database", "Бележки"], records.Select(record => (string?)record!["name"]));
        Assert.Equal(
            "{\"id\":\"k3x9q2mf\",\"template\":\"db\",\"version\":1,"
            + "\"stash\":[{\"id\":\"a1b2c3d4\",\"name\":\"Payments\"},{\"id\":\"e5f6g7h8\",\"name\":\"Prod\"}],"
            + "\"name\":\"Payments prod database\","
            + "\"description\":{\"key\":\"description\",\"level\":0,\"kind\":\"value\",\"value\":\"The main database\",\"levelName\":null},"
            + "\"tags\":[\"payments\"],"
            + $"\"fields\":[{host}],"
            + "\"updatedBy\":{\"account\":\"demo.editor\",\"name\":\"Demo demo.editor\"},\"updatedAt\":\"2026-10-21T11:32:00Z\"}",
            records[1]!.ToJsonString());
        Assert.Equal(
            """[{"key":"url","level":0,"kind":"value","value":"https://monitoring.example.test","levelName":null}]""",
            records[0]!["fields"]!.ToJsonString());
        Assert.Equal("""[{"id":"a1b2c3d4","name":"Payments","count":3,"children":[{"id":"e5f6g7h8","name":"Prod","count":2,"children":[]}]}]""", answer["tree"]!.ToJsonString());
        Assert.Equal(
            """{"templates":[{"id":"db","count":1},{"id":"link","count":1},{"id":"note","count":1}],"tags":[{"tag":"monitoring","count":1},{"tag":"payments","count":1}],"fields":[{"key":"env","values":[{"value":"prod","count":1}]},{"key":"kind","values":[{"value":"monitoring","count":1}]}]}""",
            answer["facets"]!.ToJsonString());

        // Not even the admin, who reads its level, gets a secret value from the list.
        Assert.True(!text.Contains(MadeUpKiler.SecretValue, StringComparison.Ordinal), "The list gives a secret value.");
    }

    [Theory]
    [InlineData(MadeUpKiler.User, "q=месечен", new[] { "Бележки" }, 1)]
    [InlineData(Guest, "q=месечен", new string[0], 0)]
    [InlineData(MadeUpKiler.User, "q=PAYMENTS%20Prod", new[] { "Payments prod database", "Бележки" }, 2)]
    [InlineData(MadeUpKiler.User, "stash=a1b2c3d4,e5f6g7h8&field.env=PROD", new[] { "Payments prod database" }, 1)]
    [InlineData(MadeUpKiler.User, "tag=MONITORING&tag=&template=link&q=", new[] { "Payments monitoring" }, 1)]
    [InlineData(MadeUpKiler.User, "stash=e5f6g7h8", new string[0], 3)]
    public async Task TheQuery_ChoosesTheRecords_AndTheTreeKeepsEveryBranch(string caller, string query, string[] names, int inTree)
    {
        using var data = NewData(guestMode: caller == Guest);
        await using var server = await StartAsync(data, caller == Guest ? MadeUpKiler.User : caller);
        using var client = caller == Guest ? server.CreateSignedInClient() : await SignedInAsync(server);

        var (answer, _) = await GetAsync(client, $"/api/records?{query}");

        Assert.Equal(names, answer["records"]!.AsArray().Select(record => (string?)record!["name"]));
        Assert.Equal(inTree, answer["tree"]!.AsArray().Sum(node => (int)node!["count"]!));
    }

    [Theory]
    [InlineData("stash=a1b2c3d4,,", "stash is not 1 to 4 ids of stashes.")]
    [InlineData("stash=A1B2C3D4", "stash is not 1 to 4 ids of stashes.")]
    [InlineData("stash=a1b2c3d4,e5f6g7h8,j9k0m1n2,p3q4r5s6,t2v3w4x5", "stash is not 1 to 4 ids of stashes.")]
    [InlineData("q=a&q=b", "q is given more than once.")]
    [InlineData("field.env=prod&field.env=test", "field.env is given more than once.")]
    public async Task AQueryThatDoesNotFit_IsRefused(string query, string error)
    {
        using var data = NewData(guestMode: false);
        await using var server = await StartAsync(data, MadeUpKiler.User);
        using var client = await SignedInAsync(server);

        using var response = await client.GetAsync($"/api/records?{query}", Cancel);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(error, (string?)(await ReadAsync(response))["error"]);
    }

    [Theory]
    [InlineData(NoGuestMode, AccessRefusal.NotSignedIn)]
    [InlineData(Locked, AccessRefusal.NotSignedIn)]
    [InlineData(MadeUpKiler.Temporary, AccessRefusal.MustChangePassword)]
    public async Task TheList_RefusesWhomItMust(string caller, AccessRefusal refusal)
    {
        using var data = NewData(guestMode: false);
        await using var server = await StartAsync(data, caller switch { NoGuestMode => MadeUpKiler.User, Locked => MadeUpKiler.Admin, _ => caller });
        using var client = caller == NoGuestMode ? server.CreateSignedInClient() : await SignedInAsync(server);
        if (caller == Locked)
        {
            (await client.DeleteAsync("/api/session", Cancel)).Dispose();
        }

        using var response = await client.GetAsync("/api/records", Cancel);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(AccessRules.Message(refusal), (string?)(await ReadAsync(response))["error"]);
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

    /// <summary>The answer as JSON, and as the text that came, which a test searches for a secret value.</summary>
    private static async Task<(JsonObject Answer, string Text)> GetAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path, Cancel);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var text = await response.Content.ReadAsStringAsync(Cancel);
        return (Assert.IsType<JsonObject>(JsonNode.Parse(text)), text);
    }

    private static async Task<JsonObject> ReadAsync(HttpResponseMessage response)
    {
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        return Assert.IsType<JsonObject>(JsonNode.Parse(await response.Content.ReadAsStringAsync(Cancel)));
    }

    /// <summary>Starts Kiler with the data and the account of the test, and waits until the copy is open.</summary>
    private static async Task<KilerServer> StartAsync(KilerData data, string account)
    {
        var app = KilerHost.Build(new StartOptions { DataFolder = data.Folder }, data: data, account: new FakeAccount(account));
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
        var dataFolder = Path.Combine(_folder.Path, "data");
        MadeUpKiler.WriteRecords(MadeUpKiler.WriteTo(MadeUpData.Create(dataFolder, Path.Combine(_folder.Path, "writer")), guestMode));
        return new(DataPlace.For(dataFolder, Path.Combine(_folder.Path, "Kiler")), FileOwner.Instance, TimeProvider.System, TextWriter.Null, TextWriter.Null);
    }
}

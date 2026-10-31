using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Kiler.Core;

namespace Kiler.App.Tests;

/// <summary>
/// <c>GET /api/records</c> (M5.6), <c>GET /api/records/{id}</c> and <c>POST /api/records/{id}/secret</c> (M5.7, docs/api.md)
/// on a made-up Kiler with five records (<see cref="MadeUpKiler.WriteRecords"/>): what each kind of caller reads, the query,
/// the value of a secret, and whom each refuses; the writes, the stashes and the tags (M5.9) are in
/// <c>RecordsApiTests.Writes.cs</c>, and the trash (M5.10) in <c>RecordsApiTests.Trash.cs</c>. It runs with reflection-based
/// JSON off, as Native AOT does, so it reads records as the published exe does.
/// </summary>
public sealed partial class RecordsApiTests : IDisposable
{
    /// <summary>A caller without a session, with guest mode.</summary>
    private const string Guest = "guest";

    /// <summary>A caller without a session, without guest mode.</summary>
    private const string NoGuestMode = "no-guest-mode";

    /// <summary>The admin, signed in and then locked.</summary>
    private const string Locked = "locked";

    /// <summary>A record whose file does not fit, which a test writes.</summary>
    private const string NotFitting = "m9n8p7q6";

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
        Assert.Equal(
            ["Payments monitoring", "Payments prod database", "Payments report query", "Report of a gone database", "Бележки"],
            records.Select(record => (string?)record!["name"]));
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
        Assert.Equal("""[{"id":"a1b2c3d4","name":"Payments","count":5,"children":[{"id":"e5f6g7h8","name":"Prod","count":2,"children":[]}]}]""", answer["tree"]!.ToJsonString());
        Assert.Equal(
            """{"templates":[{"id":"db","count":1},{"id":"link","count":1},{"id":"note","count":1},{"id":"sql","count":2}],"tags":[{"tag":"monitoring","count":1},{"tag":"payments","count":1}],"fields":[{"key":"env","values":[{"value":"prod","count":1}]},{"key":"kind","values":[{"value":"monitoring","count":1}]}]}""",
            answer["facets"]!.ToJsonString());

        // Not even the admin, who reads its level, gets a secret value from the list.
        Assert.True(!text.Contains(MadeUpKiler.SecretValue, StringComparison.Ordinal), "The list gives a secret value.");
    }

    [Theory]
    [InlineData(MadeUpKiler.User, "q=месечен", new[] { "Бележки" }, 1)]
    [InlineData(Guest, "q=месечен", new string[0], 0)]
    [InlineData(MadeUpKiler.User, "q=PAYMENTS%20Prod", new[] { "Payments prod database", "Payments report query", "Бележки" }, 3)]
    [InlineData(MadeUpKiler.User, "stash=a1b2c3d4,e5f6g7h8&field.env=PROD", new[] { "Payments prod database" }, 1)]
    [InlineData(MadeUpKiler.User, "tag=MONITORING&tag=&template=link&q=", new[] { "Payments monitoring" }, 1)]
    [InlineData(MadeUpKiler.User, "stash=e5f6g7h8", new string[0], 5)]
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
    [InlineData(Guest, """
        [{"key":"env","level":0,"kind":"value","value":"prod","levelName":null},
        {"key":"host","level":1,"kind":"locked","value":null,"levelName":"Team"},
        {"key":"port","level":1,"kind":"locked","value":null,"levelName":"Team"},
        {"key":"tns","level":2,"kind":"locked","value":null,"levelName":"Prod"},
        {"key":"user","level":2,"kind":"locked","value":null,"levelName":"Prod"},
        {"key":"password","level":2,"kind":"locked","value":null,"levelName":"Prod"}]
        """)]
    [InlineData(MadeUpKiler.User, """
        [{"key":"env","level":0,"kind":"value","value":"prod","levelName":null},
        {"key":"host","level":1,"kind":"value","value":"db01.example.test","levelName":null},
        {"key":"port","level":1,"kind":"value","value":1521,"levelName":null},
        {"key":"tns","level":2,"kind":"locked","value":null,"levelName":"Prod"},
        {"key":"user","level":2,"kind":"locked","value":null,"levelName":"Prod"},
        {"key":"password","level":2,"kind":"locked","value":null,"levelName":"Prod"}]
        """)]
    [InlineData(MadeUpKiler.Admin, """
        [{"key":"env","level":0,"kind":"value","value":"prod","levelName":null},
        {"key":"host","level":1,"kind":"value","value":"db01.example.test","levelName":null},
        {"key":"port","level":1,"kind":"value","value":1521,"levelName":null},
        {"key":"tns","level":2,"kind":"value","value":{"language":"text","code":"PAYDB"},"levelName":null},
        {"key":"user","level":2,"kind":"value","value":"pay_read","levelName":null},
        {"key":"password","level":2,"kind":"secret","value":null,"levelName":null}]
        """)]
    public async Task EveryReader_GetsARecord_WithTheFieldsOfTheirLevels_AndAPadlockOnEachAbove(string caller, string fields)
    {
        using var data = NewData(guestMode: caller == Guest);
        await using var server = await StartAsync(data, caller == Guest ? MadeUpKiler.User : caller);
        using var client = caller == Guest ? server.CreateSignedInClient() : await SignedInAsync(server);

        var (answer, text) = await GetAsync(client, $"/api/records/{MadeUpKiler.Database}");

        // The TNS is raised from 1 to 2, and a padlock names the level of the field, not that of the template.
        Assert.Equal(fields.ReplaceLineEndings(string.Empty), answer["fields"]!.ToJsonString());
        answer.Remove("fields");
        Assert.Equal(
            "{\"id\":\"k3x9q2mf\",\"template\":\"db\",\"version\":1,"
            + "\"stash\":[{\"id\":\"a1b2c3d4\",\"name\":\"Payments\"},{\"id\":\"e5f6g7h8\",\"name\":\"Prod\"}],"
            + "\"name\":\"Payments prod database\","
            + "\"description\":{\"key\":\"description\",\"level\":0,\"kind\":\"value\",\"value\":\"The main database\",\"levelName\":null},"
            + "\"tags\":[\"payments\"],\"levels\":[1,2],"
            + "\"createdBy\":{\"account\":\"demo.admin\",\"name\":\"Demo demo.admin\"},\"createdAt\":\"2026-09-30T08:10:00Z\","
            + "\"updatedBy\":{\"account\":\"demo.editor\",\"name\":\"Demo demo.editor\"},\"updatedAt\":\"2026-10-21T11:32:00Z\"}",
            answer.ToJsonString());
        Assert.True(!text.Contains(MadeUpKiler.SecretValue, StringComparison.Ordinal), "The record gives a secret value.");
    }

    [Theory]
    [InlineData(MadeUpKiler.Query, """
        [{"key":"db","level":1,"kind":"value","value":{"id":"k3x9q2mf","name":"Payments prod database"},"levelName":null},
        {"key":"query","level":1,"kind":"value","value":"select count(*) from payments","levelName":null},
        {"key":"notes","level":1,"kind":"empty","value":null,"levelName":null}]
        """)]
    [InlineData(MadeUpKiler.GoneQuery, """
        [{"key":"db","level":1,"kind":"value","value":{"id":"zzzzzzzz","name":null},"levelName":null},
        {"key":"query","level":1,"kind":"empty","value":null,"levelName":null},
        {"key":"notes","level":1,"kind":"empty","value":null,"levelName":null}]
        """)]
    public async Task ARef_GivesTheNameOfItsRecord_OrNoneWhenThatRecordHasGone(string id, string fields)
    {
        using var data = NewData(guestMode: false);
        await using var server = await StartAsync(data, MadeUpKiler.User);
        using var client = await SignedInAsync(server);

        var (answer, _) = await GetAsync(client, $"/api/records/{id}");

        Assert.Equal(fields.ReplaceLineEndings(string.Empty), answer["fields"]!.ToJsonString());
    }

    [Theory]
    [InlineData("zzzzzzzz")]
    [InlineData("K3X9Q2MF")]
    [InlineData("k3x9")]
    [InlineData(NotFitting)]
    public async Task ARecordThatIsNotThere_OrDoesNotFit_IsNotFound(string id)
    {
        using var data = NewData(guestMode: false, made => made.Create(MadeUpData.Record(NotFitting), new JsonObject { ["template"] = "db" }));
        await using var server = await StartAsync(data, MadeUpKiler.Admin);
        using var client = await SignedInAsync(server);

        using var response = await client.GetAsync($"/api/records/{id}", Cancel);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(RecordsApi.NotThere, (string?)(await ReadAsync(response))["error"]);
    }

    [Theory]
    [InlineData("show")]
    [InlineData("copy")]
    public async Task TheValueOfASecret_IsGivenToWhoHoldsItsLevel_AndNotKeptByTheBrowser(string use)
    {
        using var data = NewData(guestMode: false);
        await using var server = await StartAsync(data, MadeUpKiler.Admin);
        using var client = await SignedInAsync(server);

        using var response = await PostSecretAsync(client, MadeUpKiler.Database, "password", use);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var answer = await ReadAsync(response);
        Assert.True(answer.Count == 1 && (string?)answer["value"] == MadeUpKiler.SecretValue, "The answer is not the value of the secret alone.");
    }

    [Theory]
    [InlineData(MadeUpKiler.User, MadeUpKiler.Database, "password", "show", HttpStatusCode.Forbidden, SecretOutcome.Locked)]
    [InlineData(MadeUpKiler.Admin, MadeUpKiler.Database, "host", "copy", HttpStatusCode.BadRequest, SecretOutcome.NotSecret)]
    [InlineData(MadeUpKiler.Admin, MadeUpKiler.Database, "nope", "show", HttpStatusCode.BadRequest, SecretOutcome.NoField)]
    [InlineData(MadeUpKiler.Admin, MadeUpKiler.Database, null, "show", HttpStatusCode.BadRequest, SecretOutcome.NoField)]
    [InlineData(MadeUpKiler.Admin, MadeUpKiler.Database, "password", "print", HttpStatusCode.BadRequest, null)]
    [InlineData(MadeUpKiler.Admin, MadeUpKiler.Database, "password", null, HttpStatusCode.BadRequest, null)]
    [InlineData(MadeUpKiler.Admin, "zzzzzzzz", "password", "show", HttpStatusCode.NotFound, null)]
    [InlineData(Guest, MadeUpKiler.Database, "password", "show", HttpStatusCode.Forbidden, null)]
    public async Task TheRequestOfASecret_RefusesWhatItMust(string caller, string id, string? field, string? use, HttpStatusCode status, SecretOutcome? outcome)
    {
        using var data = NewData(guestMode: caller == Guest);
        await using var server = await StartAsync(data, caller == Guest ? MadeUpKiler.User : caller);
        using var client = caller == Guest ? server.CreateSignedInClient() : await SignedInAsync(server);

        using var response = await PostSecretAsync(client, id, field, use);

        Assert.Equal(status, response.StatusCode);
        var error = (string?)(await ReadAsync(response))["error"];
        Assert.Equal(
            outcome is { } refused ? RecordsApi.Message(refused)
            : status == HttpStatusCode.NotFound ? RecordsApi.NotThere
            : caller == Guest ? AccessRules.Message(AccessRefusal.NotSignedIn)
            : "use is not show or copy.",
            error);
    }

    [Theory]
    [InlineData(NoGuestMode, AccessRefusal.NotSignedIn, "list")]
    [InlineData(Locked, AccessRefusal.NotSignedIn, "list")]
    [InlineData(MadeUpKiler.Temporary, AccessRefusal.MustChangePassword, "list")]
    [InlineData(NoGuestMode, AccessRefusal.NotSignedIn, "record")]
    [InlineData(Locked, AccessRefusal.NotSignedIn, "record")]
    [InlineData(MadeUpKiler.Temporary, AccessRefusal.MustChangePassword, "record")]
    [InlineData(NoGuestMode, AccessRefusal.NotSignedIn, "secret")]
    [InlineData(Locked, AccessRefusal.NotSignedIn, "secret")]
    [InlineData(MadeUpKiler.Temporary, AccessRefusal.MustChangePassword, "secret")]
    [InlineData(NoGuestMode, AccessRefusal.NotSignedIn, "stashes")]
    [InlineData(Locked, AccessRefusal.NotSignedIn, "stashes")]
    [InlineData(MadeUpKiler.Temporary, AccessRefusal.MustChangePassword, "stashes")]
    [InlineData(NoGuestMode, AccessRefusal.NotSignedIn, "tags")]
    [InlineData(Locked, AccessRefusal.NotSignedIn, "tags")]
    [InlineData(MadeUpKiler.Temporary, AccessRefusal.MustChangePassword, "tags")]
    [InlineData(NoGuestMode, AccessRefusal.NotSignedIn, "trash")]
    [InlineData(Locked, AccessRefusal.NotSignedIn, "trash")]
    [InlineData(MadeUpKiler.Temporary, AccessRefusal.MustChangePassword, "trash")]
    [InlineData(NoGuestMode, AccessRefusal.NotSignedIn, "history")]
    [InlineData(Locked, AccessRefusal.NotSignedIn, "history")]
    [InlineData(MadeUpKiler.Temporary, AccessRefusal.MustChangePassword, "history")]
    public async Task TheEndpoints_RefuseWhomTheyMust(string caller, AccessRefusal refusal, string endpoint)
    {
        using var data = NewData(guestMode: false);
        await using var server = await StartAsync(data, caller switch { NoGuestMode => MadeUpKiler.User, Locked => MadeUpKiler.Admin, _ => caller });
        using var client = caller == NoGuestMode ? server.CreateSignedInClient() : await SignedInAsync(server);
        if (caller == Locked)
        {
            (await client.DeleteAsync("/api/session", Cancel)).Dispose();
        }

        using var response = endpoint switch
        {
            "list" => await client.GetAsync("/api/records", Cancel),
            "record" => await client.GetAsync($"/api/records/{MadeUpKiler.Database}", Cancel),
            "stashes" => await client.GetAsync("/api/stashes", Cancel),
            "tags" => await client.GetAsync("/api/tags", Cancel),
            "trash" => await client.GetAsync("/api/trash", Cancel),
            "history" => await client.GetAsync($"/api/records/{MadeUpKiler.Database}/history", Cancel),
            _ => await PostSecretAsync(client, MadeUpKiler.Database, "password", "show"),
        };

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(AccessRules.Message(refusal), (string?)(await ReadAsync(response))["error"]);
    }

    private static async Task<HttpResponseMessage> PostSecretAsync(HttpClient client, string id, string? field, string? use)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/records/{id}/secret")
        {
            Content = new StringContent(new JsonObject { ["field"] = field, ["use"] = use }.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        return await client.SendAsync(request, Cancel);
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

    /// <summary>
    /// Starts Kiler with the data and the account of the test, and the clock of its writes, by default the system's, and
    /// waits until the copy is open. A fake clock that does not move never ends the first wait of the heartbeat.
    /// </summary>
    private static async Task<KilerServer> StartAsync(KilerData data, string account, TimeProvider? time = null)
    {
        var app = KilerHost.Build(new StartOptions { DataFolder = data.Folder }, data: data, account: new FakeAccount(account), time: time);
        var server = await KilerServer.StartAsync(app, data.Folder);
        for (var waited = Stopwatch.StartNew(); data.State != DataState.Open; await Task.Delay(TimeSpan.FromMilliseconds(20), Cancel))
        {
            Assert.True(waited.Elapsed < Quick, $"The state stayed {data.State}, not open.");
        }

        return server;
    }

    /// <summary>A data folder with the made-up Kiler and its records, and <paramref name="more"/>; and a local folder of the test.</summary>
    private KilerData NewData(bool guestMode, Action<MadeUpData>? more = null)
    {
        var dataFolder = Path.Combine(_folder.Path, "data");
        var made = MadeUpKiler.WriteRecords(MadeUpKiler.WriteTo(MadeUpData.Create(dataFolder, Path.Combine(_folder.Path, "writer")), guestMode));
        more?.Invoke(made);
        return new(DataPlace.For(dataFolder, Path.Combine(_folder.Path, "Kiler")), FileOwner.Instance, TimeProvider.System, TextWriter.Null, TextWriter.Null);
    }
}

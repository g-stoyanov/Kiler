using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Kiler.Core;

namespace Kiler.App.Tests;

/// <summary><c>GET /api/templates</c> and <c>GET /api/levels</c> (M5.2, docs/api.md) on a made-up Kiler with the initial templates, and who they refuse.</summary>
public sealed class TemplatesApiTests : IDisposable
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
    [InlineData(Guest)]
    [InlineData(MadeUpKiler.User)]
    [InlineData(MadeUpKiler.Admin)]
    public async Task EveryReader_GetsTheTemplatesByName_WithTheFilesThatDoNotFit_AndTheNamesOfTheLevels(string caller)
    {
        using var data = NewData(guestMode: caller == Guest);
        await using var server = await StartAsync(data, caller == Guest ? MadeUpKiler.User : caller);
        using var client = caller == Guest ? server.CreateSignedInClient() : await SignedInAsync(server);

        var answer = await GetAsync(client, "/api/templates");

        var templates = answer["templates"]!.AsArray();
        Assert.Equal(["Link", "Note", "Server / database", "SQL query"], templates.Select(template => (string?)template!["name"]));
        var db = Assert.Single(templates, template => (string?)template!["id"] == "db")!;
        Assert.Equal("database", (string?)db["icon"]);
        Assert.Equal(["env", "host", "port", "tns", "user", "password"], db["fields"]!.AsArray().Select(field => (string?)field!["key"]));

        // Every member is there, null when it does not apply (docs/api.md, "Conventions"); the levels stop at the top level, 2.
        Assert.Equal(
            """{"key":"password","label":"Password","type":"secret","level":null,"levelByEnv":{"prod":2,"test":2,"dev":1},"required":false,"filter":false,"list":false,"options":null,"language":null,"dialect":null,"languages":null,"refTemplate":null}""",
            db["fields"]![5]!.ToJsonString());
        Assert.Equal(
            """{"key":"env","label":"Environment","type":"select","level":0,"levelByEnv":null,"required":false,"filter":true,"list":false,"options":["prod","test","dev"],"language":null,"dialect":null,"languages":null,"refTemplate":null}""",
            db["fields"]![0]!.ToJsonString());
        Assert.Equal("""[{"id":"bad","reason":"icon is missing."}]""", answer["invalid"]!.ToJsonString());

        var levels = await GetAsync(client, "/api/levels");
        Assert.Equal("""{"levels":[{"level":0,"name":"Guest"},{"level":1,"name":"Team"},{"level":2,"name":"Prod"}]}""", levels.ToJsonString());
    }

    [Theory]
    [InlineData(NoGuestMode, AccessRefusal.NotSignedIn)]
    [InlineData(Locked, AccessRefusal.NotSignedIn)]
    [InlineData(MadeUpKiler.Temporary, AccessRefusal.MustChangePassword)]
    public async Task TheEndpoints_RefuseWhomTheyMust(string caller, AccessRefusal refusal)
    {
        using var data = NewData(guestMode: false);
        await using var server = await StartAsync(data, caller switch { NoGuestMode => MadeUpKiler.User, Locked => MadeUpKiler.Admin, _ => caller });
        using var client = caller == NoGuestMode ? server.CreateSignedInClient() : await SignedInAsync(server);
        if (caller == Locked)
        {
            (await client.DeleteAsync("/api/session", Cancel)).Dispose();
        }

        foreach (var path in new[] { "/api/templates", "/api/levels" })
        {
            using var response = await client.GetAsync(path, Cancel);

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal(AccessRules.Message(refusal), (string?)(await ReadAsync(response))["error"]);
        }
    }

    [Fact]
    public async Task ALevelsJsonThatDoesNotFit_IsAConflict_ForTheNamesOfTheLevels()
    {
        using var data = NewData(guestMode: true, levels: new JsonObject { ["levels"] = new JsonArray() });
        await using var server = await StartAsync(data, MadeUpKiler.User);
        using var client = server.CreateSignedInClient();

        using var response = await client.GetAsync("/api/levels", Cancel);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(SessionApi.NoData, (string?)(await ReadAsync(response))["error"]);
        Assert.Equal(4, (await GetAsync(client, "/api/templates"))["templates"]!.AsArray().Count);
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

    private static async Task<JsonObject> GetAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path, Cancel);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadAsync(response);
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

    /// <summary>
    /// A data folder with the made-up Kiler, the initial templates of its top level and a template that does not fit, and a
    /// local folder of the test; <paramref name="levels"/> replaces <c>levels.json</c> after the sign-in files are written.
    /// </summary>
    private KilerData NewData(bool guestMode, JsonObject? levels = null)
    {
        var dataFolder = Path.Combine(_folder.Path, "data");
        var writer = MadeUpKiler.WriteTo(MadeUpData.Create(dataFolder, Path.Combine(_folder.Path, "writer")), guestMode);
        foreach (var template in InitialTemplates.For(2))
        {
            writer.Create(template.Key, template.ToContent());
        }

        writer.Create(TemplateFile.KeyOf("bad"), new JsonObject { ["name"] = "Bad", ["fields"] = new JsonArray() });
        if (levels is not null)
        {
            writer.Replace(LevelsFile.Key, 1, levels);
        }

        return new(DataPlace.For(dataFolder, Path.Combine(_folder.Path, "Kiler")), FileOwner.Instance, TimeProvider.System, TextWriter.Null, TextWriter.Null);
    }
}

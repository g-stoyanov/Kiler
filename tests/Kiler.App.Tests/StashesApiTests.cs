using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Kiler.Core;
using Microsoft.Extensions.Time.Testing;

namespace Kiler.App.Tests;

/// <summary>
/// The stashes of an admin (M6.22, docs/api.md): <c>GET /api/admin/stashes</c> and the rename, the merge and the delete of a
/// stash, on a made-up Kiler with records (<see cref="MadeUpKiler.WriteRecords"/>), whose five records are under Payments,
/// two of them under Prod, and a stash Reports that no record has: a rename is seen wherever the path shows, a merge moves
/// the records, a stash that a record has is not deleted, and each refuses what it must. It runs with reflection-based JSON
/// off, as Native AOT does.
/// </summary>
public sealed class StashesApiTests : IDisposable
{
    /// <summary>A caller without a session, with guest mode.</summary>
    private const string Guest = "guest";

    /// <summary>A live stash that no record has.</summary>
    private const string Reports = "j9k0m1n2";

    /// <summary>The longest wait for a load of a few files on the local disk, on a busy CI machine.</summary>
    private static readonly TimeSpan Quick = TimeSpan.FromSeconds(30);

    private readonly TempFolder _folder = new();

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 31, 10, 0, 0, TimeSpan.Zero));

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    public void Dispose() => _folder.Dispose();

    [Fact]
    public async Task AnAdmin_GetsTheLiveStashesByName_WithTheRecordsOfEach()
    {
        using var data = NewData();
        await using var server = await StartAsync(data, MadeUpKiler.Admin);
        using var client = await SignedInAsync(server);

        Assert.Equal(
            """{"stashes":[{"id":"a1b2c3d4","name":"Payments","version":1,"records":5},{"id":"e5f6g7h8","name":"Prod","version":1,"records":2},{"id":"j9k0m1n2","name":"Reports","version":1,"records":0}]}""",
            await GetTextAsync(client, "/api/admin/stashes"));
    }

    [Fact]
    public async Task ARename_IsSeenInTheRecord_TheTree_TheSearch_AndTheChoicesOfTheForm()
    {
        using var data = NewData();
        await using var server = await StartAsync(data, MadeUpKiler.Admin);
        using var client = await SignedInAsync(server);

        using var renamed = await SendAsync(client, HttpMethod.Put, $"/api/admin/stashes/{MadeUpKiler.Prod}", """{"name":" Production ","version":1}""");

        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
        Assert.Equal("""{"id":"e5f6g7h8","name":"Production","version":2,"records":2}""", (await ReadAsync(renamed))["stashes"]![1]!.ToJsonString());
        Assert.Equal("""[{"id":"a1b2c3d4","name":"Payments"},{"id":"e5f6g7h8","name":"Production"}]""", (await GetAsync(client, $"/api/records/{MadeUpKiler.Database}"))["stash"]!.ToJsonString());
        Assert.Equal("Production", (string?)(await GetAsync(client, "/api/records"))["tree"]![0]!["children"]![0]!["name"]);
        Assert.Equal(2, (await GetAsync(client, "/api/records?q=production"))["records"]!.AsArray().Count);
        Assert.Contains("""{"id":"e5f6g7h8","name":"Production","used":true}""", (await GetAsync(client, $"/api/stashes?path={MadeUpKiler.Payments}"))["stashes"]!.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMerge_MovesTheRecordsToTheOtherStash_AndTheirPathsAreReadThroughIt()
    {
        using var data = NewData();
        await using var server = await StartAsync(data, MadeUpKiler.Admin);
        using var client = await SignedInAsync(server);

        using var merged = await SendAsync(client, HttpMethod.Post, $"/api/admin/stashes/{MadeUpKiler.Prod}/merge", $$"""{"into":"{{Reports}}","version":1}""");

        Assert.Equal(HttpStatusCode.OK, merged.StatusCode);
        Assert.Equal(
            """{"stashes":[{"id":"a1b2c3d4","name":"Payments","version":1,"records":5},{"id":"j9k0m1n2","name":"Reports","version":1,"records":2}]}""",
            (await ReadAsync(merged)).ToJsonString());
        Assert.Equal("""[{"id":"a1b2c3d4","name":"Payments"},{"id":"j9k0m1n2","name":"Reports"}]""", (await GetAsync(client, $"/api/records/{MadeUpKiler.Database}"))["stash"]!.ToJsonString());
    }

    [Fact]
    public async Task AStashThatARecordHas_IsNotDeleted_AndOneThatNoneHas_Is()
    {
        using var data = NewData();
        await using var server = await StartAsync(data, MadeUpKiler.Admin);
        using var client = await SignedInAsync(server);

        using var used = await client.DeleteAsync($"/api/admin/stashes/{MadeUpKiler.Prod}?version=1", Cancel);
        await AssertErrorAsync(used, HttpStatusCode.Conflict, "2 records are in this stash, so it cannot be deleted. Merge it into another stash instead.");

        using var deleted = await client.DeleteAsync($"/api/admin/stashes/{Reports}?version=1", Cancel);
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        Assert.DoesNotContain(Reports, (await ReadAsync(deleted)).ToJsonString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Reports, await GetTextAsync(client, "/api/stashes"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("rename", "Payments", null, HttpStatusCode.Conflict, StashesApi.Exists)]
    [InlineData("rename", "PROD", null, HttpStatusCode.OK, null)]
    [InlineData("rename", " ", "name", HttpStatusCode.BadRequest, StashActions.BlankName)]
    [InlineData("merge", MadeUpKiler.Prod, "into", HttpStatusCode.BadRequest, StashActions.IntoItself)]
    [InlineData("merge", "zzzzzzzz", "into", HttpStatusCode.BadRequest, StashActions.TargetGone)]
    [InlineData("merge", null, "into", HttpStatusCode.BadRequest, StashActions.NoTarget)]
    public async Task AValueThatDoesNotFit_IsRefused_WithItsField(string action, string? value, string? field, HttpStatusCode status, string? error)
    {
        using var data = NewData();
        await using var server = await StartAsync(data, MadeUpKiler.Admin);
        using var client = await SignedInAsync(server);

        using var response = action == "rename"
            ? await SendAsync(client, HttpMethod.Put, $"/api/admin/stashes/{MadeUpKiler.Prod}", new JsonObject { ["name"] = value, ["version"] = 1 }.ToJsonString())
            : await SendAsync(client, HttpMethod.Post, $"/api/admin/stashes/{MadeUpKiler.Prod}/merge", new JsonObject { ["into"] = value, ["version"] = 1 }.ToJsonString());

        Assert.Equal(status, response.StatusCode);
        if (error is not null)
        {
            var answer = await ReadAsync(response);
            Assert.Equal((error, field), ((string?)answer["error"], (string?)answer["field"]));
        }
    }

    [Theory]
    [InlineData("rename")]
    [InlineData("merge")]
    [InlineData("delete")]
    public async Task AnOlderVersion_IsAConflict_AStashThatIsNotThereIsNotFound_AndNoVersionIsRefused(string action)
    {
        using var data = NewData();
        await using var server = await StartAsync(data, MadeUpKiler.Admin);
        using var client = await SignedInAsync(server);
        using var renamed = await SendAsync(client, HttpMethod.Put, $"/api/admin/stashes/{Reports}", """{"name":"Reports 2026","version":1}""");
        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
        Task<HttpResponseMessage> ActAsync(string id, int? version) => action switch
        {
            "rename" => SendAsync(client, HttpMethod.Put, $"/api/admin/stashes/{id}", new JsonObject { ["name"] = "Renamed", ["version"] = version }.ToJsonString()),
            "merge" => SendAsync(client, HttpMethod.Post, $"/api/admin/stashes/{id}/merge", new JsonObject { ["into"] = MadeUpKiler.Payments, ["version"] = version }.ToJsonString()),
            _ => client.DeleteAsync($"/api/admin/stashes/{id}{(version is { } over ? $"?version={over}" : "")}", Cancel),
        };

        using var older = await ActAsync(Reports, 1);
        await AssertErrorAsync(older, HttpStatusCode.Conflict, "The stash has been changed meanwhile. Read the list again and try once more.");
        using var missing = await ActAsync("zzzzzzzz", 1);
        await AssertErrorAsync(missing, HttpStatusCode.NotFound, StashesApi.NotThere);
        using var noVersion = await ActAsync(Reports, null);
        await AssertErrorAsync(noVersion, HttpStatusCode.BadRequest, RecordsApi.NoVersion);
    }

    [Theory]
    [InlineData(MadeUpKiler.Editor, AccessRefusal.NotAdmin)]
    [InlineData(MadeUpKiler.User, AccessRefusal.NotAdmin)]
    [InlineData(Guest, AccessRefusal.NotSignedIn)]
    public async Task TheStashesOfAnAdmin_AreOnlyForAnAdmin(string caller, AccessRefusal refusal)
    {
        using var data = NewData(guestMode: caller == Guest);
        await using var server = await StartAsync(data, caller == Guest ? MadeUpKiler.User : caller);
        using var client = caller == Guest ? server.CreateSignedInClient() : await SignedInAsync(server);

        using var list = await client.GetAsync("/api/admin/stashes", Cancel);
        using var rename = await SendAsync(client, HttpMethod.Put, $"/api/admin/stashes/{Reports}", """{"name":"Renamed","version":1}""");
        using var merge = await SendAsync(client, HttpMethod.Post, $"/api/admin/stashes/{Reports}/merge", $$"""{"into":"{{MadeUpKiler.Prod}}","version":1}""");
        using var delete = await client.DeleteAsync($"/api/admin/stashes/{Reports}?version=1", Cancel);

        foreach (var response in new[] { list, rename, merge, delete })
        {
            await AssertErrorAsync(response, HttpStatusCode.Forbidden, AccessRules.Message(refusal));
        }

        Assert.Equal("Reports", data.Records!.Read(null).StashName(Reports));
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string path, string json)
    {
        using var request = new HttpRequestMessage(method, path) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        return await client.SendAsync(request, Cancel);
    }

    private static async Task<HttpClient> SignedInAsync(KilerServer server)
    {
        var client = server.CreateSignedInClient();
        using var signedIn = await SendAsync(client, HttpMethod.Post, "/api/session", new JsonObject { ["password"] = MadeUpKiler.Password }.ToJsonString());
        Assert.Equal(HttpStatusCode.OK, signedIn.StatusCode);
        return client;
    }

    private static async Task<string> GetTextAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path, Cancel);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync(Cancel);
    }

    private static async Task<JsonObject> GetAsync(HttpClient client, string path) => Assert.IsType<JsonObject>(JsonNode.Parse(await GetTextAsync(client, path)));

    private static async Task<JsonObject> ReadAsync(HttpResponseMessage response)
    {
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        return Assert.IsType<JsonObject>(JsonNode.Parse(await response.Content.ReadAsStringAsync(Cancel)));
    }

    private static async Task AssertErrorAsync(HttpResponseMessage response, HttpStatusCode status, string error)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal(error, (string?)(await ReadAsync(response))["error"]);
    }

    /// <summary>
    /// Starts Kiler with the data, the account and the clock of the test, and waits until the copy is open. The clock does
    /// not move, which fixes the dates of the writes and never ends the first wait of the heartbeat.
    /// </summary>
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

    /// <summary>A data folder with the made-up Kiler, its records and the stash <see cref="Reports"/>; and a local folder of the test.</summary>
    private KilerData NewData(bool guestMode = false)
    {
        var dataFolder = Path.Combine(_folder.Path, "data");
        var made = MadeUpKiler.WriteRecords(MadeUpKiler.WriteTo(MadeUpData.Create(dataFolder, Path.Combine(_folder.Path, "writer")), guestMode));
        var at = new DateTime(2026, 10, 30, 8, 0, 0, DateTimeKind.Utc);
        made.Create(StashFile.KeyOf(Reports), new StashFile(Reports, "Reports", new Authorship(MadeUpKiler.Admin, at, MadeUpKiler.Admin, at)).ToContent());
        return new(DataPlace.For(dataFolder, Path.Combine(_folder.Path, "Kiler")), FileOwner.Instance, TimeProvider.System, TextWriter.Null, TextWriter.Null);
    }
}

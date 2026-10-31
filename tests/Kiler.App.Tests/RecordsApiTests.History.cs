using System.Net;
using System.Text.Json.Nodes;
using Kiler.Core;

namespace Kiler.App.Tests;

/// <summary>
/// The history of M5.11 (docs/api.md): <c>GET /api/records/{id}/history</c> and
/// <c>POST /api/records/{id}/history/{version}/restore</c>, on the made-up Kiler of <see cref="RecordsApiTests"/>: who changed a
/// record and when, the newest first; a restore makes an old version the next one, with the date and the author of the
/// restore; only an editor who holds every level of the record and of the old version makes it; what does not fit is
/// refused. Whom they refuse is with the other endpoints. The local copy of each test is new, and its first load takes no
/// history, so the old versions come from the edits of the test.
/// </summary>
public sealed partial class RecordsApiTests
{
    /// <summary>The first version of the database as its history gives it.</summary>
    private const string FirstVersion = """{"version":1,"updatedBy":{"account":"demo.editor","name":"Demo demo.editor"},"updatedAt":"2026-10-21T11:32:00Z","levels":[1,2]}""";

    /// <summary>An edit of <see cref="MadeUpKiler.Database"/> over its first version with the fields of level 0 and 1 only, which an editor of level 1 makes.</summary>
    private const string LevelOneEdit =
        """{"version":1,"stash":["a1b2c3d4","e5f6g7h8"],"name":"Payments prod database","description":"The main database","tags":["payments"],"fields":{"env":"prod","host":"db03.example.test","port":1521},"levels":{}}""";

    [Fact]
    public async Task TheHistory_GivesWhoChangedTheRecordAndWhen_AndARestoreMakesAnOldVersionTheNextOne()
    {
        using var data = NewData(guestMode: false);
        await using var server = await StartAsync(data, MadeUpKiler.Admin, NewClock());
        using var client = await SignedInAsync(server);
        var (first, _) = await GetAsync(client, $"/api/records/{MadeUpKiler.Database}");
        using (var edited = await SendAsync(client, HttpMethod.Put, $"/api/records/{MadeUpKiler.Database}", AdminEdit(1, "db03.example.test")))
        {
            Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        }

        var admin = """{"account":"demo.admin","name":"Demo demo.admin"}""";
        Assert.Equal(
            $$"""{"versions":[{"version":2,"updatedBy":{{admin}},"updatedAt":"{{Now}}","levels":[1,2]},{{FirstVersion}}]}""",
            (await GetAsync(client, $"/api/records/{MadeUpKiler.Database}/history")).Text);

        // Over the version that the editor read, which is no longer the current one: the conflict.
        using (var stale = await SendAsync(client, HttpMethod.Post, $"/api/records/{MadeUpKiler.Database}/history/1/restore", """{"version":1}"""))
        {
            Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
            var conflict = await ReadAsync(stale);
            Assert.Equal((RecordsApi.Message(RecordOutcome.Conflict), 2), ((string?)conflict["error"], (int)conflict["current"]!["version"]!));
        }

        using var restored = await SendAsync(client, HttpMethod.Post, $"/api/records/{MadeUpKiler.Database}/history/1/restore", $$"""{"version":2,{{Forged}}}""");

        // The first version again, as the next one, with the date and the author of the restore; the creation stays.
        Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
        var expected = first.DeepClone().AsObject();
        expected["version"] = 3;
        expected["updatedBy"] = JsonNode.Parse(admin);
        expected["updatedAt"] = Now;
        var text = await restored.Content.ReadAsStringAsync(Cancel);
        Assert.Equal(expected.ToJsonString(), text);
        Assert.Equal(text, (await GetAsync(client, $"/api/records/{MadeUpKiler.Database}")).Text);
        Assert.True(await SecretAsync(client, MadeUpKiler.Database) == MadeUpKiler.SecretValue, "The password is not the one of the first version.");
        var versions = (await GetAsync(client, $"/api/records/{MadeUpKiler.Database}/history")).Answer["versions"]!.AsArray();
        Assert.Equal([3, 2, 1], versions.Select(version => (int)version!["version"]!).ToArray());
    }

    /// <summary>Every reader reads the history, a guest in guest mode too, as the record shows who changed it last.</summary>
    [Theory]
    [InlineData(Guest)]
    [InlineData(MadeUpKiler.User)]
    public async Task EveryReader_GetsTheHistory(string caller)
    {
        using var data = NewData(guestMode: caller == Guest);
        await using var server = await StartAsync(data, MadeUpKiler.User);
        using var client = caller == Guest ? server.CreateSignedInClient() : await SignedInAsync(server);

        Assert.Equal($$"""{"versions":[{{FirstVersion}}]}""", (await GetAsync(client, $"/api/records/{MadeUpKiler.Database}/history")).Text);
    }

    /// <summary>The database has groups at 1 and 2 in both versions: the editor holds 1, the admin both.</summary>
    [Theory]
    [InlineData(MadeUpKiler.Editor, HttpStatusCode.Forbidden)]
    [InlineData(MadeUpKiler.Admin, HttpStatusCode.OK)]
    public async Task OnlyAnEditorWhoHoldsEveryLevelOfTheRecordAndOfTheOldVersion_RestoresIt(string caller, HttpStatusCode status)
    {
        using var data = NewData(guestMode: false);
        await using var server = await StartAsync(data, caller);
        using var client = await SignedInAsync(server);
        using (var edited = await SendAsync(client, HttpMethod.Put, $"/api/records/{MadeUpKiler.Database}", LevelOneEdit))
        {
            Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        }

        using var response = await SendAsync(client, HttpMethod.Post, $"/api/records/{MadeUpKiler.Database}/history/1/restore", """{"version":2}""");

        Assert.Equal(status, response.StatusCode);
        var answer = await ReadAsync(response);
        Assert.Equal(
            status == HttpStatusCode.OK ? "3" : RecordsApi.Message(RecordOutcome.NotHeld),
            status == HttpStatusCode.OK ? answer["version"]!.ToJsonString() : (string?)answer["error"]);
        Assert.Equal(status == HttpStatusCode.OK ? 3 : 2, (int)(await GetAsync(client, $"/api/records/{MadeUpKiler.Database}")).Answer["version"]!);
    }

    [Theory]
    [InlineData("GET", "/api/records/zzzzzzzz/history", null, HttpStatusCode.NotFound, RecordsApi.NotThere)]
    [InlineData("GET", "/api/records/K3X9Q2MF/history", null, HttpStatusCode.NotFound, RecordsApi.NotThere)]
    [InlineData("GET", $"/api/records/{NotFitting}/history", null, HttpStatusCode.NotFound, RecordsApi.NotThere)]
    [InlineData("POST", $"/api/records/{MadeUpKiler.Database}/history/1/restore", "{}", HttpStatusCode.BadRequest, RecordsApi.NoVersion)]
    [InlineData("POST", $"/api/records/{MadeUpKiler.Database}/history/1/restore", """{"version":1.5}""", HttpStatusCode.BadRequest, null)]
    [InlineData("POST", $"/api/records/{MadeUpKiler.Database}/history/1/restore", """{"version":1}""", HttpStatusCode.NotFound, RecordsApi.NotInHistory)]
    [InlineData("POST", $"/api/records/{MadeUpKiler.Database}/history/0/restore", """{"version":1}""", HttpStatusCode.NotFound, RecordsApi.NotInHistory)]
    [InlineData("POST", $"/api/records/{MadeUpKiler.Database}/history/-1/restore", """{"version":1}""", HttpStatusCode.NotFound, RecordsApi.NotInHistory)]
    [InlineData("POST", $"/api/records/{MadeUpKiler.Database}/history/x/restore", """{"version":1}""", HttpStatusCode.NotFound, RecordsApi.NotInHistory)]
    [InlineData("POST", "/api/records/zzzzzzzz/history/1/restore", """{"version":2}""", HttpStatusCode.NotFound, RecordsApi.NotThere)]
    [InlineData("POST", $"/api/records/{NotFitting}/history/1/restore", """{"version":2}""", HttpStatusCode.NotFound, RecordsApi.NotThere)]
    public async Task AHistoryOrARestoreThatDoesNotFit_IsRefused(string method, string path, string? body, HttpStatusCode status, string? error)
    {
        using var data = NewData(guestMode: false, made => made.Create(MadeUpData.Record(NotFitting), new JsonObject { ["template"] = "db" }));
        await using var server = await StartAsync(data, MadeUpKiler.Admin);
        using var client = await SignedInAsync(server);

        using var response = body is null ? await client.GetAsync(path, Cancel) : await SendAsync(client, new HttpMethod(method), path, body);

        Assert.Equal(status, response.StatusCode);
        if (error is null)
        {
            // A version that is not a whole number is the 400 of ASP.NET Core, without a body.
            Assert.Empty(await response.Content.ReadAsByteArrayAsync(Cancel));
        }
        else
        {
            Assert.Equal(error, (string?)(await ReadAsync(response))["error"]);
        }

        Assert.Equal(1, (int)(await GetAsync(client, $"/api/records/{MadeUpKiler.Database}")).Answer["version"]!);
    }
}

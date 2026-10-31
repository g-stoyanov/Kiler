using System.Net;
using System.Text.Json.Nodes;
using Kiler.Core;

namespace Kiler.App.Tests;

/// <summary>
/// The trash of M5.10 (docs/api.md): <c>DELETE /api/records/{id}</c>, <c>GET /api/trash</c> and
/// <c>POST /api/trash/{id}/restore</c>, on the made-up Kiler of <see cref="RecordsApiTests"/>: a delete and a restore keep the
/// version, the dates and the authors, only an editor who holds every level of the record makes them, each reader reads the
/// trash at their levels, and what does not fit is refused. Whom they refuse is with the other endpoints.
/// </summary>
public sealed partial class RecordsApiTests
{
    /// <summary>The database of the made-up Kiler as a trash item; <c>HOST</c> stands for its field <c>host</c>, the one with <c>list</c>.</summary>
    private const string DatabaseInTrash =
        "{\"id\":\"k3x9q2mf\",\"template\":\"db\",\"version\":1,"
        + "\"stash\":[{\"id\":\"a1b2c3d4\",\"name\":\"Payments\"},{\"id\":\"e5f6g7h8\",\"name\":\"Prod\"}],"
        + "\"name\":\"Payments prod database\","
        + "\"description\":{\"key\":\"description\",\"level\":0,\"kind\":\"value\",\"value\":\"The main database\",\"levelName\":null},"
        + "\"tags\":[\"payments\"],\"levels\":[1,2],\"fields\":[HOST],"
        + "\"updatedBy\":{\"account\":\"demo.editor\",\"name\":\"Demo demo.editor\"},\"updatedAt\":\"2026-10-21T11:32:00Z\"}";

    [Fact]
    public async Task AnEditor_DeletesAndRestoresARecord_WhichKeepsItsVersionDatesAndAuthors()
    {
        using var data = NewData(guestMode: false);
        await using var server = await StartAsync(data, MadeUpKiler.Editor, NewClock());
        using var client = await SignedInAsync(server);
        var (_, before) = await GetAsync(client, $"/api/records/{MadeUpKiler.Query}");

        using var deleted = await client.DeleteAsync($"/api/records/{MadeUpKiler.Query}?version=1", Cancel);

        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        var text = await deleted.Content.ReadAsStringAsync(Cancel);
        var item = Assert.IsType<JsonObject>(JsonNode.Parse(text));
        Assert.Equal(["id", "template", "version", "stash", "name", "description", "tags", "levels", "fields", "updatedBy", "updatedAt"], item.Select(member => member.Key));
        Assert.Equal(
            (MadeUpKiler.Query, 1, "[1]", """{"account":"demo.editor","name":"Demo demo.editor"}""", "2026-10-21T11:32:00Z"),
            ((string?)item["id"], (int)item["version"]!, item["levels"]!.ToJsonString(), item["updatedBy"]!.ToJsonString(), (string?)item["updatedAt"]));

        // It is in the trash, and no longer among the records.
        Assert.Equal($"{{\"records\":[{text}]}}", (await GetAsync(client, "/api/trash")).Text);
        using (var gone = await client.GetAsync($"/api/records/{MadeUpKiler.Query}", Cancel))
        {
            Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
        }

        Assert.DoesNotContain(MadeUpKiler.Query, (await GetAsync(client, "/api/records")).Answer["records"]!.AsArray().Select(record => (string?)record!["id"]));

        using var restored = await client.PostAsync($"/api/trash/{MadeUpKiler.Query}/restore", null, Cancel);

        Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
        Assert.Equal(before, await restored.Content.ReadAsStringAsync(Cancel));
        Assert.Equal("""{"records":[]}""", (await GetAsync(client, "/api/trash")).Text);
        Assert.Equal(before, (await GetAsync(client, $"/api/records/{MadeUpKiler.Query}")).Text);
    }

    [Fact]
    public async Task ADeleteOverAnOlderVersion_IsAConflict_WithTheRecordAsItIsNow()
    {
        using var data = NewData(guestMode: false);
        await using var server = await StartAsync(data, MadeUpKiler.Admin, NewClock());
        using var client = await SignedInAsync(server);
        (await SendAsync(client, HttpMethod.Put, $"/api/records/{MadeUpKiler.Database}", AdminEdit(1, "db03.example.test"))).Dispose();

        using var response = await client.DeleteAsync($"/api/records/{MadeUpKiler.Database}?version=1", Cancel);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync(Cancel);
        var answer = Assert.IsType<JsonObject>(JsonNode.Parse(text));
        Assert.Equal(RecordsApi.Message(RecordOutcome.Conflict), (string?)answer["error"]);
        Assert.Equal((2, Now), ((int)answer["current"]!["version"]!, (string?)answer["current"]!["updatedAt"]));
        Assert.True(!text.Contains(MadeUpKiler.SecretValue, StringComparison.Ordinal), "The conflict gives a secret value.");
        Assert.Equal(2, (int)(await GetAsync(client, $"/api/records/{MadeUpKiler.Database}")).Answer["version"]!);
        Assert.Equal("""{"records":[]}""", (await GetAsync(client, "/api/trash")).Text);
    }

    [Theory]
    [InlineData(MadeUpKiler.Editor, "delete", HttpStatusCode.Forbidden)]
    [InlineData(MadeUpKiler.Admin, "delete", HttpStatusCode.OK)]
    [InlineData(MadeUpKiler.Editor, "restore", HttpStatusCode.Forbidden)]
    [InlineData(MadeUpKiler.Admin, "restore", HttpStatusCode.OK)]
    public async Task OnlyAnEditorWhoHoldsEveryLevelOfTheRecord_DeletesOrRestoresIt(string caller, string action, HttpStatusCode status)
    {
        // The database has groups at 1 and 2; the editor holds 1, the admin both. The restore is of the database in the trash.
        using var data = NewData(guestMode: false, made =>
        {
            if (action == "restore")
            {
                made.Delete(MadeUpKiler.Database, 1);
            }
        });
        await using var server = await StartAsync(data, caller);
        using var client = await SignedInAsync(server);

        using var response = action == "delete"
            ? await client.DeleteAsync($"/api/records/{MadeUpKiler.Database}?version=1", Cancel)
            : await client.PostAsync($"/api/trash/{MadeUpKiler.Database}/restore", null, Cancel);

        Assert.Equal(status, response.StatusCode);
        var answer = await ReadAsync(response);
        Assert.Equal(
            status == HttpStatusCode.OK ? "[1,2]" : RecordsApi.Message(RecordOutcome.NotHeld),
            status == HttpStatusCode.OK ? answer["levels"]!.ToJsonString() : (string?)answer["error"]);
        var inTrash = (await GetAsync(client, "/api/trash")).Answer["records"]!.AsArray().Count == 1;
        Assert.Equal(action == "delete" == (status == HttpStatusCode.OK), inTrash);
    }

    [Theory]
    [InlineData(Guest, """{"key":"host","level":1,"kind":"locked","value":null,"levelName":"Team"}""")]
    [InlineData(MadeUpKiler.User, """{"key":"host","level":1,"kind":"value","value":"db01.example.test","levelName":null}""")]
    public async Task EveryReader_GetsTheTrashAtTheirLevels_AndARefToARecordInIt_HasNoName(string caller, string host)
    {
        // The database, the link, whose name comes first and whose id comes after, and a record whose file does not fit.
        using var data = NewData(guestMode: caller == Guest, made =>
        {
            made.Create(MadeUpData.Record(NotFitting), new JsonObject { ["template"] = "db" });
            foreach (var id in new[] { MadeUpKiler.Database, "m4n5p6q7", NotFitting })
            {
                made.Delete(id, 1);
            }
        });
        await using var server = await StartAsync(data, caller == Guest ? MadeUpKiler.User : caller);
        using var client = caller == Guest ? server.CreateSignedInClient() : await SignedInAsync(server);

        var (answer, text) = await GetAsync(client, "/api/trash");

        var records = answer["records"]!.AsArray();
        Assert.Equal(["Payments monitoring", "Payments prod database"], records.Select(record => (string?)record!["name"]));
        Assert.Equal(DatabaseInTrash.Replace("HOST", host, StringComparison.Ordinal), records[1]!.ToJsonString());
        Assert.True(!text.Contains(MadeUpKiler.SecretValue, StringComparison.Ordinal), "The trash gives a secret value.");
        if (caller != Guest)
        {
            var db = (await GetAsync(client, $"/api/records/{MadeUpKiler.Query}")).Answer["fields"]!.AsArray().Single(field => (string?)field!["key"] == "db");
            Assert.Equal($$"""{"id":"{{MadeUpKiler.Database}}","name":null}""", db!["value"]!.ToJsonString());
        }
    }

    [Theory]
    [InlineData("DELETE", $"/api/records/{MadeUpKiler.Database}", HttpStatusCode.BadRequest, RecordsApi.NoVersion)]
    [InlineData("DELETE", $"/api/records/{MadeUpKiler.Database}?version=x", HttpStatusCode.BadRequest, null)]
    [InlineData("DELETE", "/api/records/zzzzzzzz?version=1", HttpStatusCode.NotFound, RecordsApi.NotThere)]
    [InlineData("DELETE", "/api/records/K3X9Q2MF?version=1", HttpStatusCode.NotFound, RecordsApi.NotThere)]
    [InlineData("DELETE", $"/api/records/{NotFitting}?version=1", HttpStatusCode.NotFound, RecordsApi.NotThere)]
    [InlineData("POST", "/api/trash/zzzzzzzz/restore", HttpStatusCode.NotFound, RecordsApi.NotInTrash)]
    [InlineData("POST", $"/api/trash/{MadeUpKiler.Database}/restore", HttpStatusCode.NotFound, RecordsApi.NotInTrash)]
    public async Task ADeleteOrARestoreThatDoesNotFit_IsRefused(string method, string path, HttpStatusCode status, string? error)
    {
        using var data = NewData(guestMode: false, made => made.Create(MadeUpData.Record(NotFitting), new JsonObject { ["template"] = "db" }));
        await using var server = await StartAsync(data, MadeUpKiler.Admin);
        using var client = await SignedInAsync(server);

        using var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), path), Cancel);

        Assert.Equal(status, response.StatusCode);
        if (error is null)
        {
            // A version that is not a number is the 400 of ASP.NET Core, without a body.
            Assert.Empty(await response.Content.ReadAsByteArrayAsync(Cancel));
        }
        else
        {
            Assert.Equal(error, (string?)(await ReadAsync(response))["error"]);
        }

        Assert.Equal(1, (int)(await GetAsync(client, $"/api/records/{MadeUpKiler.Database}")).Answer["version"]!);
    }
}

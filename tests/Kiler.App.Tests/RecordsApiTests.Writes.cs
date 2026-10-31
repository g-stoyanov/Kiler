using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Kiler.Core;
using Microsoft.Extensions.Time.Testing;

namespace Kiler.App.Tests;

/// <summary>
/// The writes of M5.9 (docs/api.md): <c>POST /api/records</c>, <c>PUT /api/records/{id}</c> with its conflict,
/// <c>GET /api/stashes</c>, <c>POST /api/stashes</c> and <c>GET /api/tags</c>, on the made-up Kiler of
/// <see cref="RecordsApiTests"/>: what each kind of caller may do, that the dates and the authors come from Kiler, and what an
/// editor may not write.
/// </summary>
public sealed partial class RecordsApiTests
{
    /// <summary>The time of the fake clock of the writes.</summary>
    private const string Now = "2026-10-31T09:00:00Z";

    /// <summary>The password of the record that <see cref="AnEditor_CreatesARecord_WithTheIdTheDatesAndTheAuthorsOfKiler"/> writes; compared only with <c>Assert.True</c>.</summary>
    private const string NewSecret = "made-up-db-password-4";

    /// <summary>Dates and authors that a request sends, which change nothing (PLAN M5 step 3).</summary>
    private const string Forged =
        "\"id\":\"zzzzzzzz\",\"createdBy\":{\"account\":\"demo.stranger\",\"name\":null},\"createdAt\":\"2020-01-01T00:00:00Z\","
        + "\"updatedBy\":{\"account\":\"demo.stranger\",\"name\":null},\"updatedAt\":\"2020-01-01T00:00:00Z\"";

    /// <summary>
    /// A JSON escape of a lone surrogate, which passes <c>FieldValue.Fits</c> and fails at <c>GetString</c> (M5.4 security
    /// review). Built in code, as the Write and Edit tools turn an escape into its character (SKILL.md, "Tests").
    /// </summary>
    private static readonly string LoneSurrogate = string.Concat("\\", "u", "d800");

    /// <summary>The edit of <see cref="MadeUpKiler.Database"/> by the admin over <paramref name="version"/>, every field of the record but the password, with <paramref name="more"/>.</summary>
    private static string AdminEdit(int version, string host, string more = "") =>
        $$"""
        {"version":{{version}},"template":"note","stash":["a1b2c3d4","e5f6g7h8"],"name":"Payments prod database","description":"The main database","tags":["payments"],
        "fields":{"env":"prod","host":"{{host}}","port":1521,"tns":{"language":"text","code":"PAYDB"},"user":"pay_read"{{more}}},"levels":{},{{Forged}}}
        """;

    [Fact]
    public async Task AnEditor_CreatesARecord_WithTheIdTheDatesAndTheAuthorsOfKiler()
    {
        using var data = NewData(guestMode: false);
        await using var server = await StartAsync(data, MadeUpKiler.Editor, NewClock());
        using var client = await SignedInAsync(server);

        // At dev the user and the password are at level 1, which the editor holds; the description is raised to it.
        using var response = await SendAsync(client, HttpMethod.Post, "/api/records", $$"""
            {"template":"db","stash":["a1b2c3d4","e5f6g7h8"],"name":"  Billing dev database ","description":"A made-up database","tags":["billing"," Billing ",""],
            "fields":{"env":"dev","host":"db02.example.test","port":5432,"tns":null,"user":"bill_app","password":"{{NewSecret}}"},"levels":{"description":1},
            "version":9,{{Forged}}}
            """);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync(Cancel);
        var answer = Assert.IsType<JsonObject>(JsonNode.Parse(text));
        var id = (string)answer["id"]!;
        Assert.True(ItemId.IsValid(id) && id != "zzzzzzzz", "The id is not one that Kiler made.");
        Assert.Equal($"/api/records/{id}", response.Headers.Location?.ToString());
        Assert.Equal(
            $"{{\"id\":\"{id}\",\"template\":\"db\",\"version\":1,"
            + "\"stash\":[{\"id\":\"a1b2c3d4\",\"name\":\"Payments\"},{\"id\":\"e5f6g7h8\",\"name\":\"Prod\"}],"
            + "\"name\":\"Billing dev database\","
            + "\"description\":{\"key\":\"description\",\"level\":1,\"kind\":\"value\",\"value\":\"A made-up database\",\"levelName\":null},"
            + "\"tags\":[\"billing\"],\"levels\":[1],"
            + "\"fields\":[{\"key\":\"env\",\"level\":0,\"kind\":\"value\",\"value\":\"dev\",\"levelName\":null},"
            + "{\"key\":\"host\",\"level\":1,\"kind\":\"value\",\"value\":\"db02.example.test\",\"levelName\":null},"
            + "{\"key\":\"port\",\"level\":1,\"kind\":\"value\",\"value\":5432,\"levelName\":null},"
            + "{\"key\":\"tns\",\"level\":1,\"kind\":\"empty\",\"value\":null,\"levelName\":null},"
            + "{\"key\":\"user\",\"level\":1,\"kind\":\"value\",\"value\":\"bill_app\",\"levelName\":null},"
            + "{\"key\":\"password\",\"level\":1,\"kind\":\"secret\",\"value\":null,\"levelName\":null}],"
            + $"\"createdBy\":{{\"account\":\"demo.editor\",\"name\":\"Demo demo.editor\"}},\"createdAt\":\"{Now}\","
            + $"\"updatedBy\":{{\"account\":\"demo.editor\",\"name\":\"Demo demo.editor\"}},\"updatedAt\":\"{Now}\"}}",
            text);
        Assert.True(!text.Contains(NewSecret, StringComparison.Ordinal), "The answer gives the secret value.");

        // The set has it, with its secret.
        Assert.Equal(text, (await GetAsync(client, $"/api/records/{id}")).Text);
        Assert.True(await SecretAsync(client, id) == NewSecret, "The secret is not the one written.");
    }

    [Fact]
    public async Task AnEdit_KeepsTheCreationAndASecretLeftOut_AndClearsOneGivenAsNull()
    {
        using var data = NewData(guestMode: false);
        await using var server = await StartAsync(data, MadeUpKiler.Admin, NewClock());
        using var client = await SignedInAsync(server);

        // The template of the request is not read, and the TNS keeps its raise to 2.
        using var edited = await SendAsync(client, HttpMethod.Put, $"/api/records/{MadeUpKiler.Database}", AdminEdit(1, "db03.example.test"));

        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        var answer = await ReadAsync(edited);
        Assert.Equal(
            """[{"key":"env","level":0,"kind":"value","value":"prod","levelName":null},"""
            + """{"key":"host","level":1,"kind":"value","value":"db03.example.test","levelName":null},"""
            + """{"key":"port","level":1,"kind":"value","value":1521,"levelName":null},"""
            + """{"key":"tns","level":2,"kind":"value","value":{"language":"text","code":"PAYDB"},"levelName":null},"""
            + """{"key":"user","level":2,"kind":"value","value":"pay_read","levelName":null},"""
            + """{"key":"password","level":2,"kind":"secret","value":null,"levelName":null}]""",
            answer["fields"]!.ToJsonString());
        answer.Remove("fields");
        answer.Remove("description");
        answer.Remove("stash");
        Assert.Equal(
            "{\"id\":\"k3x9q2mf\",\"template\":\"db\",\"version\":2,\"name\":\"Payments prod database\",\"tags\":[\"payments\"],\"levels\":[1,2],"
            + "\"createdBy\":{\"account\":\"demo.admin\",\"name\":\"Demo demo.admin\"},\"createdAt\":\"2026-09-30T08:10:00Z\","
            + $"\"updatedBy\":{{\"account\":\"demo.admin\",\"name\":\"Demo demo.admin\"}},\"updatedAt\":\"{Now}\"}}",
            answer.ToJsonString());
        Assert.True(await SecretAsync(client, MadeUpKiler.Database) == MadeUpKiler.SecretValue, "The secret left out is not kept.");

        using var cleared = await SendAsync(client, HttpMethod.Put, $"/api/records/{MadeUpKiler.Database}", AdminEdit(2, "db03.example.test", ""","password":null"""));

        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        var fields = (await ReadAsync(cleared))["fields"]!.AsArray();
        Assert.Equal("""{"key":"password","level":2,"kind":"empty","value":null,"levelName":null}""", fields[^1]!.ToJsonString());
    }

    [Fact]
    public async Task AnEditOverAnOlderVersion_IsAConflict_WithTheRecordAsItIsNow()
    {
        using var data = NewData(guestMode: false);
        await using var server = await StartAsync(data, MadeUpKiler.Admin, NewClock());
        using var client = await SignedInAsync(server);
        (await SendAsync(client, HttpMethod.Put, $"/api/records/{MadeUpKiler.Database}", AdminEdit(1, "db03.example.test"))).Dispose();

        using var response = await SendAsync(client, HttpMethod.Put, $"/api/records/{MadeUpKiler.Database}", AdminEdit(1, "db04.example.test"));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync(Cancel);
        var answer = Assert.IsType<JsonObject>(JsonNode.Parse(text));
        Assert.Equal(RecordsApi.Message(RecordOutcome.Conflict), (string?)answer["error"]);
        var current = answer["current"]!;
        Assert.Equal(2, (int)current["version"]!);
        Assert.Equal("db03.example.test", (string?)current["fields"]![1]!["value"]);
        Assert.Equal("""{"account":"demo.admin","name":"Demo demo.admin"}""", current["updatedBy"]!.ToJsonString());
        Assert.Equal(Now, (string?)current["updatedAt"]);
        Assert.True(!text.Contains(MadeUpKiler.SecretValue, StringComparison.Ordinal), "The conflict gives a secret value.");

        // "Overwrite" sends the edit again with the version of current.
        using var overwritten = await SendAsync(client, HttpMethod.Put, $"/api/records/{MadeUpKiler.Database}", AdminEdit(2, "db04.example.test"));
        Assert.Equal(HttpStatusCode.OK, overwritten.StatusCode);
    }

    [Theory]
    [InlineData("PUT", """{"version":1,"stash":["a1b2c3d4","e5f6g7h8"],"name":"Payments prod database","fields":{"env":"prod","host":"h","port":1,"user":"x"}}""", "user")]
    [InlineData("PUT", """{"version":1,"stash":["a1b2c3d4","e5f6g7h8"],"name":"Payments prod database","fields":{"env":"prod","host":"h","port":1,"user":null}}""", "user")]
    [InlineData("PUT", """{"version":1,"stash":["a1b2c3d4","e5f6g7h8"],"name":"Payments prod database","fields":{"env":"dev","host":"h","port":1}}""", "user")]
    [InlineData("POST", """{"template":"db","stash":["a1b2c3d4"],"name":"n","fields":{"env":"prod","password":"x"}}""", "password")]
    [InlineData("POST", """{"template":"db","stash":["a1b2c3d4"],"name":"n","fields":{"env":"dev","host":"h"},"levels":{"host":2}}""", "host")]
    public async Task AnEditorWhoWritesAboveTheirLevel_IsRefused_WithTheField(string method, string request, string field)
    {
        using var data = NewData(guestMode: false);
        await using var server = await StartAsync(data, MadeUpKiler.Editor);
        using var client = await SignedInAsync(server);

        using var response = await SendAsync(client, new HttpMethod(method), method == "PUT" ? $"/api/records/{MadeUpKiler.Database}" : "/api/records", request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(field, (string?)(await ReadAsync(response))["field"]);
        Assert.Equal(1, (int)(await GetAsync(client, $"/api/records/{MadeUpKiler.Database}")).Answer["version"]!);
    }

    [Theory]
    [InlineData("POST", """{"template":"db","stash":["a1b2c3d4"],"name":"n","fields":{"port":true}}""", RecordsApi.NoShape, "port")]
    [InlineData("POST", """{"template":"db","stash":["a1b2c3d4"],"name":"n","fields":{"host":[1]}}""", RecordsApi.NoShape, "host")]
    [InlineData("POST", """{"template":"db","stash":["a1b2c3d4"],"name":"n","fields":{"tns":{"language":"text","code":"x","more":"y"}}}""", RecordsApi.NoShape, "tns")]
    [InlineData("POST", """{"template":"db","stash":["a1b2c3d4"],"name":"n","fields":{"host":"LONE"}}""", RecordsApi.NoShape, "host")]
    [InlineData("POST", """{"template":"db","stash":["a1b2c3d4"],"name":"n","fields":{"tns":{"language":"LONE","code":"x"}}}""", RecordsApi.NoShape, "tns")]
    [InlineData("POST", """{"template":"nope","stash":["a1b2c3d4"],"name":"n"}""", "The template is not there, or cannot be used.", null)]
    [InlineData("POST", """{"template":"db","stash":["a1b2c3d4"],"name":"n","fields":{"nope":"x"}}""", "The template has no field of this key.", null)]
    [InlineData("PUT", """{"stash":["a1b2c3d4"],"name":"n"}""", RecordsApi.NoVersion, null)]
    public async Task AWriteThatDoesNotFit_IsRefused_WithItsField(string method, string request, string error, string? field)
    {
        using var data = NewData(guestMode: false);
        await using var server = await StartAsync(data, MadeUpKiler.Editor);
        using var client = await SignedInAsync(server);

        using var response = await SendAsync(
            client, new HttpMethod(method), method == "PUT" ? $"/api/records/{MadeUpKiler.Database}" : "/api/records", request.Replace("LONE", LoneSurrogate, StringComparison.Ordinal));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var answer = await ReadAsync(response);
        Assert.Equal(error, (string?)answer["error"]);
        Assert.True(answer.ContainsKey("field"), "A 400 of a write has no field.");
        Assert.Equal(field, (string?)answer["field"]);
    }

    [Theory]
    [InlineData("""{"template":"db","stash":["a1b2c3d4"],"name":"n","fields":{"env":"dev","host":"h"},"levels":{"host":1.5}}""")]
    [InlineData("""{"template":"db","stash":["a1b2c3d4"],"name":"LONE"}""")]
    [InlineData("""{"template":"db","stash":"a1b2c3d4","name":"n"}""")]
    [InlineData("not JSON")]
    public async Task ARequestThatIsNotTheJsonOfTheContract_IsRefusedWithoutABody(string request)
    {
        using var data = NewData(guestMode: false);
        await using var server = await StartAsync(data, MadeUpKiler.Editor);
        using var client = await SignedInAsync(server);

        using var response = await SendAsync(client, HttpMethod.Post, "/api/records", request.Replace("LONE", LoneSurrogate, StringComparison.Ordinal));

        // A refusal of RecordActions has a body; only the 400 of ASP.NET Core has none.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync(Cancel));
        Assert.Equal(["monitoring", "payments"], await TagsAsync(client));
    }

    [Theory]
    [InlineData("zzzzzzzz")]
    [InlineData("K3X9Q2MF")]
    [InlineData(NotFitting)]
    public async Task AnEditOfARecordThatIsNotThere_OrDoesNotFit_IsNotFound(string id)
    {
        using var data = NewData(guestMode: false, made => made.Create(MadeUpData.Record(NotFitting), new JsonObject { ["template"] = "db" }));
        await using var server = await StartAsync(data, MadeUpKiler.Admin);
        using var client = await SignedInAsync(server);

        using var response = await SendAsync(client, HttpMethod.Put, $"/api/records/{id}", AdminEdit(1, "db03.example.test"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(RecordsApi.NotThere, (string?)(await ReadAsync(response))["error"]);
    }

    [Theory]
    [InlineData(Guest, "", """[{"id":"a1b2c3d4","name":"Payments","used":true},{"id":"e5f6g7h8","name":"Prod","used":false}]""")]
    [InlineData(MadeUpKiler.User, "?path=", """[{"id":"a1b2c3d4","name":"Payments","used":true},{"id":"e5f6g7h8","name":"Prod","used":false}]""")]
    [InlineData(MadeUpKiler.User, "?path=a1b2c3d4", """[{"id":"e5f6g7h8","name":"Prod","used":true},{"id":"a1b2c3d4","name":"Payments","used":false}]""")]
    [InlineData(MadeUpKiler.User, "?path=a1b2c3d4,e5f6g7h8", """[{"id":"a1b2c3d4","name":"Payments","used":false},{"id":"e5f6g7h8","name":"Prod","used":false}]""")]
    [InlineData(MadeUpKiler.User, "?path=e5f6g7h8", """[{"id":"a1b2c3d4","name":"Payments","used":false},{"id":"e5f6g7h8","name":"Prod","used":false}]""")]
    public async Task TheStashesOfAPosition_HaveTheUsedOnesFirst(string caller, string query, string stashes)
    {
        using var data = NewData(guestMode: caller == Guest);
        await using var server = await StartAsync(data, caller == Guest ? MadeUpKiler.User : caller);
        using var client = caller == Guest ? server.CreateSignedInClient() : await SignedInAsync(server);

        var (answer, _) = await GetAsync(client, $"/api/stashes{query}");

        Assert.Equal(stashes, answer["stashes"]!.ToJsonString());
    }

    [Theory]
    [InlineData("path=a1b2c3d4,e5f6g7h8,j9k0m1n2,p3q4r5s6")]
    [InlineData("path=A1B2C3D4")]
    [InlineData("path=a1b2c3d4,")]
    [InlineData("path=a1b2c3d4&path=e5f6g7h8")]
    public async Task APathThatDoesNotFit_IsRefused(string query)
    {
        using var data = NewData(guestMode: false);
        await using var server = await StartAsync(data, MadeUpKiler.User);
        using var client = await SignedInAsync(server);

        using var response = await client.GetAsync($"/api/stashes?{query}", Cancel);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("path is not 0 to 3 ids of stashes.", (string?)(await ReadAsync(response))["error"]);
    }

    [Fact]
    public async Task AnEditor_CreatesAStash_AndANameThatIsThereOrBlankIsRefused()
    {
        using var data = NewData(guestMode: false);
        await using var server = await StartAsync(data, MadeUpKiler.Editor, NewClock());
        using var client = await SignedInAsync(server);

        using var created = await SendAsync(client, HttpMethod.Post, "/api/stashes", $$"""{"name":" Reports ",{{Forged}}}""");

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var stash = await ReadAsync(created);
        var id = (string)stash["id"]!;
        Assert.True(ItemId.IsValid(id) && id != "zzzzzzzz", "The id is not one that Kiler made.");
        Assert.Equal($$"""{"id":"{{id}}","name":"Reports"}""", stash.ToJsonString());
        Assert.Equal(
            $$"""[{"id":"a1b2c3d4","name":"Payments","used":true},{"id":"e5f6g7h8","name":"Prod","used":false},{"id":"{{id}}","name":"Reports","used":false}]""",
            (await GetAsync(client, "/api/stashes")).Answer["stashes"]!.ToJsonString());

        using var twice = await SendAsync(client, HttpMethod.Post, "/api/stashes", """{"name":"REPORTS"}""");
        Assert.Equal(HttpStatusCode.Conflict, twice.StatusCode);
        Assert.Equal(StashesApi.Exists, (string?)(await ReadAsync(twice))["error"]);

        using var blank = await SendAsync(client, HttpMethod.Post, "/api/stashes", """{"name":"  "}""");
        Assert.Equal(HttpStatusCode.BadRequest, blank.StatusCode);
        Assert.Equal("""{"error":"The name of the stash is blank.","field":"name"}""", (await ReadAsync(blank)).ToJsonString());
    }

    [Fact]
    public async Task TheTags_AreEachOnceWithoutRegardToCase_ByName()
    {
        using var data = NewData(guestMode: false);
        await using var server = await StartAsync(data, MadeUpKiler.Editor);
        using var client = await SignedInAsync(server);
        Assert.Equal(["monitoring", "payments"], await TagsAsync(client));

        using var created = await SendAsync(client, HttpMethod.Post, "/api/records", """{"template":"note","stash":["a1b2c3d4"],"name":"n","tags":["Reports","PAYMENTS"]}""");
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        // Which of the two spellings of payments stays depends on the order of the ids, and the new id is random.
        Assert.Equal(["monitoring", "payments", "Reports"], await TagsAsync(client), StringComparer.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(Guest, AccessRefusal.NotSignedIn)]
    [InlineData(NoGuestMode, AccessRefusal.NotSignedIn)]
    [InlineData(Locked, AccessRefusal.NotSignedIn)]
    [InlineData(MadeUpKiler.User, AccessRefusal.CannotEdit)]
    [InlineData(MadeUpKiler.Temporary, AccessRefusal.MustChangePassword)]
    public async Task TheWrites_RefuseWhomTheyMust(string caller, AccessRefusal refusal)
    {
        using var data = NewData(guestMode: caller == Guest);
        await using var server = await StartAsync(data, caller switch { Guest or NoGuestMode => MadeUpKiler.User, Locked => MadeUpKiler.Admin, _ => caller });
        using var client = caller is Guest or NoGuestMode ? server.CreateSignedInClient() : await SignedInAsync(server);
        if (caller == Locked)
        {
            (await client.DeleteAsync("/api/session", Cancel)).Dispose();
        }

        using var create = await SendAsync(client, HttpMethod.Post, "/api/records", """{"template":"note","stash":["a1b2c3d4"],"name":"n"}""");
        using var edit = await SendAsync(client, HttpMethod.Put, $"/api/records/{MadeUpKiler.Database}", AdminEdit(1, "db03.example.test"));
        using var stash = await SendAsync(client, HttpMethod.Post, "/api/stashes", """{"name":"Reports"}""");
        using var delete = await client.DeleteAsync($"/api/records/{MadeUpKiler.Query}?version=1", Cancel);
        using var restore = await client.PostAsync($"/api/trash/{MadeUpKiler.Query}/restore", null, Cancel);
        using var restoreVersion = await SendAsync(client, HttpMethod.Post, $"/api/records/{MadeUpKiler.Query}/history/1/restore", """{"version":2}""");

        foreach (var response in new[] { create, edit, stash, delete, restore, restoreVersion })
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal(AccessRules.Message(refusal), (string?)(await ReadAsync(response))["error"]);
        }
    }

    /// <summary>A fake clock at <see cref="Now"/>, which does not move.</summary>
    private static FakeTimeProvider NewClock() => new(DateTimeOffset.Parse(Now, System.Globalization.CultureInfo.InvariantCulture));

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string path, string json)
    {
        using var request = new HttpRequestMessage(method, path) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        return await client.SendAsync(request, Cancel);
    }

    /// <summary>The value of the password of <paramref name="id"/>, which the caller compares with <c>Assert.True</c>.</summary>
    private static async Task<string?> SecretAsync(HttpClient client, string id)
    {
        using var response = await PostSecretAsync(client, id, "password", "show");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (string?)(await ReadAsync(response))["value"];
    }

    private static async Task<string?[]> TagsAsync(HttpClient client) =>
        [.. (await GetAsync(client, "/api/tags")).Answer["tags"]!.AsArray().Select(tag => (string?)tag)];
}

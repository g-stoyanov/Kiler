using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Kiler.Core;
using Microsoft.AspNetCore.Builder;

namespace Kiler.App.Tests;

/// <summary><c>/api/admin</c> (M4.6): the admin actions on a made-up Kiler (<see cref="MadeUpKiler"/>), and who they refuse.</summary>
public sealed class AdminApiTests : IDisposable
{
    private const string Reader = "/api/test/reader";

    /// <summary>A caller of <see cref="TheAdminEndpoints_RefuseWhomTheyMust"/> without a session, with guest mode.</summary>
    private const string Guest = "guest";

    /// <summary>A caller of <see cref="TheAdminEndpoints_RefuseWhomTheyMust"/>: the admin, signed in and then locked.</summary>
    private const string Locked = "locked";

    /// <summary>Made up, and compared only with <c>Assert.True</c> (SKILL.md, "Tests").</summary>
    private const string NewPassword = "demo-password-2";

    /// <summary>The longest wait for a load of a few files on the local disk, on a busy CI machine.</summary>
    private static readonly TimeSpan Quick = TimeSpan.FromSeconds(30);

    private readonly TempFolder _folder = new();
    private readonly LineWriter _output = new();
    private readonly LineWriter _errors = new();
    private MadeUpData? _writer;

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    private string DataFolder => Path.Combine(_folder.Path, "data");

    public void Dispose()
    {
        _output.Dispose();
        _errors.Dispose();
        _folder.Dispose();
    }

    [Fact]
    public async Task TheAdmin_CreatesAUser_WhoSignsInWithTheTemporaryPassword_AndMustChangeIt_AndThePasswordIsInNoFileAndNoLine()
    {
        using var data = NewData(guestMode: false);
        string temporary;
        await using (var server = await StartAsync(data, MadeUpKiler.Admin))
        {
            using var client = await SignedInAsync(server);
            using var created = await SendAsync(
                client, HttpMethod.Post, "/api/admin/users", new JsonObject { ["account"] = "Demo.New", ["displayName"] = "Demo New", ["level"] = 1, ["canEdit"] = false });
            Assert.Equal(HttpStatusCode.OK, created.StatusCode);
            var answer = await ReadAsync(created);
            temporary = (string?)answer["temporaryPassword"] ?? "";
            Assert.True(temporary.Length == Password.TemporaryLength, "The answer has no temporary password of 16 characters.");
            var user = Assert.IsType<JsonObject>(answer["user"]);
            Assert.Equal(("demo.new", 1, false, false), ((string?)user["account"], (int?)user["level"], (bool?)user["canEdit"], (bool?)user["isAdmin"]));
            Assert.True((bool?)user["mustChangePassword"]);
            Assert.True((bool?)user["keysMatchLevel"]);

            using var list = await client.GetAsync("/api/admin/users", Cancel);
            var text = await list.Content.ReadAsStringAsync(Cancel);
            Assert.Contains("\"demo.new\"", text, StringComparison.Ordinal);
            Assert.True(!text.Contains(temporary, StringComparison.Ordinal), "The list has the temporary password.");
        }

        // The Kiler of the new user, on another machine: a local copy of its own.
        using var theirs = OpenData("theirs");
        await using (var server = await StartAsync(theirs, "demo.new"))
        {
            using var client = server.CreateSignedInClient();
            using (var signedIn = await SignInAsync(client, temporary))
            {
                Assert.Equal(HttpStatusCode.OK, signedIn.StatusCode);
                var session = await ReadAsync(signedIn);
                Assert.True((bool?)session["mustChangePassword"]);
                Assert.Equal("[0,1]", session["levels"]?.ToJsonString());
            }

            using (var refused = await client.GetAsync(Reader, Cancel))
            {
                await AssertErrorAsync(refused, HttpStatusCode.Forbidden, AccessRules.Message(AccessRefusal.MustChangePassword));
            }

            using (var changed = await SendAsync(client, HttpMethod.Post, "/api/session/password", new JsonObject { ["current"] = null, ["password"] = NewPassword }))
            {
                Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
            }

            using var read = await client.GetAsync(Reader, Cancel);
            Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        }

        var lines = _output.Lines.Concat(_errors.Lines).ToList();
        Assert.True(lines.All(line => !line.Contains(temporary, StringComparison.Ordinal)), "The temporary password is in a line of the console.");
        var bytes = Encoding.UTF8.GetBytes(temporary);
        foreach (var file in Directory.EnumerateFiles(_folder.Path, "*", SearchOption.AllDirectories))
        {
            // The lock of a local copy is held while its Kiler data is open; it is empty.
            if (Path.GetFileName(file) != "lock")
            {
                Assert.True(File.ReadAllBytes(file).AsSpan().IndexOf(bytes) < 0, $"The temporary password is in {Path.GetRelativePath(_folder.Path, file)}.");
            }
        }
    }

    [Theory]
    [InlineData(MadeUpKiler.User, AccessRefusal.NotAdmin)]
    [InlineData(MadeUpKiler.Editor, AccessRefusal.NotAdmin)]
    [InlineData(MadeUpKiler.Raised, AccessRefusal.NotAdmin)]
    [InlineData(Guest, AccessRefusal.NotSignedIn)]
    [InlineData(Locked, AccessRefusal.NotSignedIn)]
    public async Task TheAdminEndpoints_RefuseWhomTheyMust(string caller, AccessRefusal refusal)
    {
        using var data = NewData(guestMode: caller == Guest);
        await using var server = await StartAsync(data, caller switch { Guest => MadeUpKiler.User, Locked => MadeUpKiler.Admin, _ => caller });
        using var client = caller == Guest ? server.CreateSignedInClient() : await SignedInAsync(server);
        if (caller == Locked)
        {
            (await client.DeleteAsync("/api/session", Cancel)).Dispose();
        }

        foreach (var (method, path, body) in Endpoints())
        {
            using var response = await SendAsync(client, method, path, body);

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal(AccessRules.Message(refusal), (string?)(await ReadAsync(response))["error"]);
        }
    }

    [Fact]
    public async Task NewUser_WithAnExistingAccount_ABadName_OrALevelOfNoKey_IsRefused()
    {
        using var data = NewData(guestMode: false);
        await using var server = await StartAsync(data, MadeUpKiler.Admin);
        using var client = await SignedInAsync(server);

        using (var existing = await NewUserAsync(client, MadeUpKiler.User, 1))
        {
            await AssertErrorAsync(existing, HttpStatusCode.Conflict, AdminApi.Message(AdminOutcome.Exists));
        }

        using (var bad = await NewUserAsync(client, "demo/new", 1))
        {
            await AssertErrorAsync(bad, HttpStatusCode.BadRequest, AdminActions.AccountInvalid);
        }

        using (var guest = await NewUserAsync(client, "demo.new", 0))
        {
            await AssertErrorAsync(guest, HttpStatusCode.BadRequest, "level is not from 1 to 2.");
        }

        var list = await GetAsync(client, "/api/admin/users");
        Assert.True(list["users"]!.AsArray().All(user => (string?)user!["account"] != "demo.new"), "A refused user is in the list.");
    }

    [Fact]
    public async Task TheActionsOnAUser_AnswerWithTheUser_AndATemporaryPasswordOnlyWhenTheyGiveKeys()
    {
        using var data = NewData(guestMode: false);
        await using var server = await StartAsync(data, MadeUpKiler.Admin);
        using var client = await SignedInAsync(server);

        var raised = await ActAsync(client, HttpMethod.Put, $"/api/admin/users/{MadeUpKiler.User}/level", new JsonObject { ["level"] = 2 });
        Assert.True(raised["temporaryPassword"] is not null, "A change of level gave no temporary password.");
        Assert.Equal((2, true, true), ((int?)raised["user"]!["level"], (bool?)raised["user"]!["mustChangePassword"], (bool?)raised["user"]!["keysMatchLevel"]));

        var password = await ActAsync(client, HttpMethod.Put, $"/api/admin/users/{MadeUpKiler.Editor}/level", new JsonObject { ["level"] = 1 });
        Assert.True(password["temporaryPassword"] is not null, "A new temporary password is not in the answer.");

        var rights = await ActAsync(client, HttpMethod.Put, $"/api/admin/users/{MadeUpKiler.Editor}/rights", Rights(false, false, shownCanEdit: true, shownIsAdmin: false));
        Assert.Null(rights["temporaryPassword"]);
        Assert.False((bool?)rights["user"]!["canEdit"]);
        Assert.True((bool?)rights["user"]!["sealed"]);

        // The isAdmin that Raised wrote into their own file is not sealed, so it does not count, nor are its rights changed (M4.14).
        Assert.True((bool?)(await GetAsync(client, "/api/admin/users"))["fewAdmins"]);
        using (var forged = await SendAsync(client, HttpMethod.Put, $"/api/admin/users/{MadeUpKiler.Raised}/rights", Rights(true, false, shownCanEdit: true, shownIsAdmin: true)))
        {
            await AssertErrorAsync(forged, HttpStatusCode.Conflict, AdminApi.Message(AdminOutcome.UserNotSealed));
        }

        // What the panel sends when the editor right is taken from Raised, as the list shows them: isAdmin stays as it is.
        using (var editor = await SendAsync(client, HttpMethod.Put, $"/api/admin/users/{MadeUpKiler.Raised}/rights", Rights(false, true, shownCanEdit: true, shownIsAdmin: true)))
        {
            await AssertErrorAsync(editor, HttpStatusCode.Conflict, AdminApi.Message(AdminOutcome.UserNotSealed));
        }

        var admin = await ActAsync(client, HttpMethod.Put, $"/api/admin/users/{MadeUpKiler.User}/rights", Rights(true, true, shownCanEdit: false, shownIsAdmin: false));
        Assert.True(admin["temporaryPassword"] is not null, "Making an admin gave no temporary password.");
        Assert.False((bool?)(await GetAsync(client, "/api/admin/users"))["fewAdmins"]);
        await ActAsync(client, HttpMethod.Put, $"/api/admin/users/{MadeUpKiler.User}/rights", Rights(true, false, shownCanEdit: true, shownIsAdmin: true));
        Assert.True((bool?)(await GetAsync(client, "/api/admin/users"))["fewAdmins"]);

        using (var own = await SendAsync(client, HttpMethod.Put, $"/api/admin/users/{MadeUpKiler.Admin}/level", new JsonObject { ["level"] = 1 }))
        {
            await AssertErrorAsync(own, HttpStatusCode.BadRequest, AdminApi.Message(AdminOutcome.OwnAccount));
        }

        using var nobody = await SendAsync(client, HttpMethod.Put, "/api/admin/users/demo.nobody/level", new JsonObject { ["level"] = 1 });
        await AssertErrorAsync(nobody, HttpStatusCode.Conflict, AdminApi.Message(AdminOutcome.NoUser));
    }

    [Fact]
    public async Task TheList_MarksAFileWhoseKeysDoNotMatchItsLevel_AndShowsWhyAFileDoesNotFit()
    {
        using var data = NewData(guestMode: false);
        var user = MadeUpKiler.UserOf(MadeUpKiler.User);
        _writer!.Create(UserFile.KeyOf("demo.gap"), new UserFile("demo.gap", "Demo Gap", 2, false, false, false, user.Keys).ToContent());
        _writer.Create(UserFile.KeyOf("demo.bad"), new JsonObject { ["displayName"] = "Demo Bad" });
        await using var server = await StartAsync(data, MadeUpKiler.Admin);
        using var client = await SignedInAsync(server);

        var list = await GetAsync(client, "/api/admin/users");

        var users = list["users"]!.AsArray().ToDictionary(entry => (string)entry!["account"]!, entry => entry!.AsObject());
        Assert.False((bool?)users["demo.gap"]["keysMatchLevel"]);
        Assert.True((bool?)users[MadeUpKiler.User]["keysMatchLevel"]);
        Assert.Equal("level is missing.", (string?)users["demo.bad"]["invalid"]);
        Assert.Null(users["demo.bad"]["displayName"]);
        Assert.Null(users[MadeUpKiler.User]["invalid"]);

        // The admin is the one whose isAdmin is sealed: that of Raised is their own (M4.14).
        Assert.True((bool?)list["fewAdmins"]);
        Assert.Equal((true, true, false, false, false), ((bool?)users[MadeUpKiler.Admin]["sealed"], (bool?)users[MadeUpKiler.User]["sealed"], (bool?)users[MadeUpKiler.Raised]["sealed"], (bool?)users["demo.gap"]["sealed"], (bool?)users["demo.bad"]["sealed"]));
    }

    [Fact]
    public async Task SetRights_WithoutTheRightsThatTheListShowed_IsAValueThatDoesNotFit_AndWithOthersThanTheFiles_IsAConflict()
    {
        using var data = NewData(guestMode: false);
        await using var server = await StartAsync(data, MadeUpKiler.Admin);
        using var client = await SignedInAsync(server);
        var path = $"/api/admin/users/{MadeUpKiler.User}/rights";

        using (var without = await SendAsync(client, HttpMethod.Put, path, new JsonObject { ["canEdit"] = true, ["isAdmin"] = false }))
        {
            await AssertErrorAsync(without, HttpStatusCode.BadRequest, AdminApi.ShownMissing);
        }

        using (var half = await SendAsync(client, HttpMethod.Put, path, new JsonObject { ["canEdit"] = true, ["isAdmin"] = true, ["shown"] = new JsonObject { ["canEdit"] = false } }))
        {
            await AssertErrorAsync(half, HttpStatusCode.BadRequest, AdminApi.ShownMissing);
        }

        // A list that showed the reader as an admin: the click on the editor right makes no admin (M4.14 security review, pass 2).
        using (var behind = await SendAsync(client, HttpMethod.Put, path, Rights(true, true, shownCanEdit: false, shownIsAdmin: true)))
        {
            await AssertErrorAsync(behind, HttpStatusCode.Conflict, AdminApi.Message(AdminOutcome.Changed));
        }

        var user = (await GetAsync(client, "/api/admin/users"))["users"]!.AsArray().Single(entry => (string?)entry!["account"] == MadeUpKiler.User)!;
        Assert.Equal((false, false, 1), ((bool?)user["canEdit"], (bool?)user["isAdmin"], (int?)user["level"]));
    }

    [Fact]
    public async Task TheAdminEndpoints_RefuseASealedAdminWithoutThePrivateKey_AndTheSessionSaysWhy()
    {
        using var data = NewData(guestMode: false);

        // A sealed isAdmin and the keys of every level, as the admin's, but not the private key of the admin key: the admin
        // right is the private key too (M4.15). The actions refuse it themselves as well (AdminOutcome.NoAdminKey, Core tests).
        var keys = MadeUpKiler.UserOf(MadeUpKiler.Admin).Keys;
        var keyless = new UserFile("demo.keyless", "Demo Keyless", 2, true, true, false, new(keys.Salt, keys.Keys, keys.AdminPublicKey));
        _writer!.Create(keyless.Key, MadeUpKiler.Sealed(keyless).ToContent());
        await using var server = await StartAsync(data, "demo.keyless");
        using var client = await SignedInAsync(server);

        var session = await GetAsync(client, "/api/session");
        Assert.Equal((true, false, "noAdminKey"), ((bool?)session["canEdit"], (bool?)session["isAdmin"], (string?)session["rightsRefusal"]));
        foreach (var (method, path, body) in Endpoints())
        {
            using var response = await SendAsync(client, method, path, body);

            await AssertErrorAsync(response, HttpStatusCode.Forbidden, AccessRules.Message(AccessRefusal.NotAdmin));
        }

        Assert.False(File.Exists(Path.Combine(DataFolder, "users", "demo.new.json")), "A refused new user was written.");
    }

    [Fact]
    public async Task RenameLevel_KeepsTheChecks_AndTheAdminStaysAnAdmin()
    {
        using var data = NewData(guestMode: false);
        await using var server = await StartAsync(data, MadeUpKiler.Admin);
        using var client = await SignedInAsync(server);
        Assert.Equal("Guest,Team,Prod", Names(await GetAsync(client, "/api/admin/levels")));

        var renamed = await ActAsync(client, HttpMethod.Put, "/api/admin/levels/1", new JsonObject { ["name"] = "Staff" });

        Assert.Equal("Guest,Staff,Prod", Names(renamed));
        var levels = Assert.IsType<JsonObject>(JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(DataFolder, "levels.json"), Cancel)));
        Assert.Equal(2, (int?)levels["version"]);
        Assert.True((string?)levels["levels"]![1]!["check"] == MadeUpKiler.CheckOfLevel1, "The rename changed the check of the level.");
        Assert.Equal("Guest,Staff,Prod", Names(await GetAsync(client, "/api/admin/levels")));

        using (var same = await SendAsync(client, HttpMethod.Put, "/api/admin/levels/2", new JsonObject { ["name"] = "staff" }))
        {
            await AssertErrorAsync(same, HttpStatusCode.BadRequest, "Level 2 has the name of a level below it, without regard to case.");
        }

        using var above = await SendAsync(client, HttpMethod.Put, "/api/admin/levels/3", new JsonObject { ["name"] = "Demo" });
        await AssertErrorAsync(above, HttpStatusCode.BadRequest, "level is not from 0 to 2.");
    }

    /// <summary>Every admin endpoint, with a body that would be done.</summary>
    private static IEnumerable<(HttpMethod Method, string Path, JsonObject? Body)> Endpoints() =>
    [
        (HttpMethod.Get, "/api/admin/users", null),
        (HttpMethod.Post, "/api/admin/users", new JsonObject { ["account"] = "demo.new", ["displayName"] = "Demo New", ["level"] = 1, ["canEdit"] = false }),
        (HttpMethod.Put, $"/api/admin/users/{MadeUpKiler.User}/level", new JsonObject { ["level"] = 2 }),
        (HttpMethod.Put, $"/api/admin/users/{MadeUpKiler.User}/rights", Rights(true, true, shownCanEdit: false, shownIsAdmin: false)),
        (HttpMethod.Get, "/api/admin/levels", null),
        (HttpMethod.Put, "/api/admin/levels/1", new JsonObject { ["name"] = "Staff" }),
    ];

    /// <summary>The body of a change of the rights, with the rights that the list showed (M4.14).</summary>
    private static JsonObject Rights(bool canEdit, bool isAdmin, bool shownCanEdit, bool shownIsAdmin) =>
        new() { ["canEdit"] = canEdit, ["isAdmin"] = isAdmin, ["shown"] = new JsonObject { ["canEdit"] = shownCanEdit, ["isAdmin"] = shownIsAdmin } };

    private static string Names(JsonObject levels) => string.Join(",", levels["levels"]!.AsArray().Select(level => (string?)level!["name"]));

    private static async Task<HttpClient> SignedInAsync(KilerServer server)
    {
        var client = server.CreateSignedInClient();
        using var signedIn = await SignInAsync(client, MadeUpKiler.Password);
        Assert.Equal(HttpStatusCode.OK, signedIn.StatusCode);
        return client;
    }

    private static Task<HttpResponseMessage> SignInAsync(HttpClient client, string password) =>
        SendAsync(client, HttpMethod.Post, "/api/session", new JsonObject { ["password"] = password });

    private static Task<HttpResponseMessage> NewUserAsync(HttpClient client, string account, int level) =>
        SendAsync(client, HttpMethod.Post, "/api/admin/users", new JsonObject { ["account"] = account, ["displayName"] = "Demo New", ["level"] = level, ["canEdit"] = false });

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string path, JsonObject? body)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        }

        return await client.SendAsync(request, Cancel);
    }

    /// <summary>An action that is done: its answer.</summary>
    private static async Task<JsonObject> ActAsync(HttpClient client, HttpMethod method, string path, JsonObject? body)
    {
        using var response = await SendAsync(client, method, path, body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadAsync(response);
    }

    private static async Task<JsonObject> GetAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path, Cancel);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadAsync(response);
    }

    private static async Task AssertErrorAsync(HttpResponseMessage response, HttpStatusCode status, string? error)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal(error, (string?)(await ReadAsync(response))["error"]);
    }

    private static async Task<JsonObject> ReadAsync(HttpResponseMessage response)
    {
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        return Assert.IsType<JsonObject>(JsonNode.Parse(await response.Content.ReadAsStringAsync(Cancel)));
    }

    /// <summary>A data folder with the made-up Kiler, and a local folder of the test; the writer is another Kiler on it.</summary>
    private KilerData NewData(bool guestMode)
    {
        _writer = MadeUpKiler.WriteTo(MadeUpData.Create(DataFolder, Path.Combine(_folder.Path, "writer")), guestMode);
        return OpenData("Kiler");
    }

    /// <summary>The data folder of the test with a local folder of its own, as the Kiler of another machine has.</summary>
    private KilerData OpenData(string local) =>
        new(DataPlace.For(DataFolder, Path.Combine(_folder.Path, local)), FileOwner.Instance, TimeProvider.System, _output, _errors);

    /// <summary>Starts Kiler with the data and the account of the test, with a test endpoint of a reader, and waits until the copy is open.</summary>
    private static async Task<KilerServer> StartAsync(KilerData data, string account)
    {
        var app = KilerHost.Build(new StartOptions { DataFolder = data.Folder }, data: data, account: new FakeAccount(account));
        app.MapGet(Reader, () => "read").Needs(Need.Reader);
        var server = await KilerServer.StartAsync(app, data.Folder);
        for (var waited = Stopwatch.StartNew(); data.State != DataState.Open; await Task.Delay(TimeSpan.FromMilliseconds(20), Cancel))
        {
            Assert.True(waited.Elapsed < Quick, $"The state stayed {data.State}, not open.");
        }

        return server;
    }
}

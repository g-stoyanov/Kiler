using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Kiler.Core;
using Kiler.Crypto;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Kiler.App.Tests;

/// <summary>
/// <c>/api/session</c> and the rights on every request (M4.4): the sign-in, guest mode, the lock, and what each kind of
/// endpoint needs, with test endpoints of each kind on a made-up Kiler (<see cref="MadeUpKiler"/>).
/// </summary>
public sealed class SessionApiTests : IDisposable
{
    private const string Reader = "/api/test/reader";
    private const string Editor = "/api/test/editor";
    private const string Admin = "/api/test/admin";

    /// <summary>The longest wait for a load of a few files on the local disk, on a busy CI machine.</summary>
    private static readonly TimeSpan Quick = TimeSpan.FromSeconds(30);

    private readonly TempFolder _folder = new();
    private readonly LineWriter _output = new();
    private readonly LineWriter _errors = new();

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        _output.Dispose();
        _errors.Dispose();
        _folder.Dispose();
    }

    [Fact]
    public async Task SignIn_WithTheRightPassword_GivesTheSession_AndTheLockEndsIt()
    {
        using var data = NewData(guestMode: false);
        await using var server = await StartAsync(data, MadeUpKiler.Admin);
        using var client = server.CreateSignedInClient();

        using (var response = await SignInAsync(client, MadeUpKiler.Password))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var session = await ReadAsync(response);
            Assert.Equal(MadeUpKiler.Admin, (string?)session["account"]);
            Assert.True((bool?)session["signedIn"]);
            Assert.Equal($"Demo {MadeUpKiler.Admin}", (string?)session["displayName"]);
            Assert.Equal([0, 1, 2], Levels(session));
            Assert.True((bool?)session["canEdit"]);
            Assert.True((bool?)session["isAdmin"]);
            Assert.False((bool?)session["mustChangePassword"]);
            Assert.False((bool?)session["guestMode"]);
        }

        Assert.True((bool?)(await GetSessionAsync(client))["signedIn"]);
        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.OK], await StatusesAsync(client));

        using (var locked = await client.DeleteAsync("/api/session", Cancel))
        {
            Assert.Equal(HttpStatusCode.OK, locked.StatusCode);
            var session = await ReadAsync(locked);
            Assert.False((bool?)session["signedIn"]);
            Assert.Null((string?)session["displayName"]);
            Assert.Empty(Levels(session));
        }

        await AssertRefusedAsync(client, Reader, AccessRules.Message(AccessRefusal.NotSignedIn));
        Assert.True(_output.Lines.Concat(_errors.Lines).All(line => !line.Contains(MadeUpKiler.Password, StringComparison.Ordinal)), "The password is in a line of the console.");
    }

    [Fact]
    public async Task SignIn_WithAWrongPassword_IsRefused_AndGivesNoSession()
    {
        using var data = NewData(guestMode: false);
        await using var server = await StartAsync(data, MadeUpKiler.Admin);
        using var client = server.CreateSignedInClient();

        using var response = await SignInAsync(client, "demo-password-2");

        await AssertErrorAsync(response, HttpStatusCode.Forbidden, SessionApi.Message(SignInRefusal.WrongPassword));
        Assert.False((bool?)(await GetSessionAsync(client))["signedIn"]);
        await AssertRefusedAsync(client, Reader, AccessRules.Message(AccessRefusal.NotSignedIn));
    }

    [Theory]
    [InlineData(MadeUpKiler.Stranger, SignInRefusal.NoAccess)]
    [InlineData("demo.nobody", SignInRefusal.NoUser)]
    public async Task SignIn_ForAUserWhoAddedThemselves_OrWithoutAUserFile_IsRefused(string account, SignInRefusal refusal)
    {
        using var data = NewData(guestMode: false);
        await using var server = await StartAsync(data, account);
        using var client = server.CreateSignedInClient();

        using var response = await SignInAsync(client, MadeUpKiler.Password);

        await AssertErrorAsync(response, HttpStatusCode.Forbidden, SessionApi.Message(refusal));
        Assert.False((bool?)(await GetSessionAsync(client))["signedIn"]);
    }

    [Fact]
    public async Task SignIn_ForAUserWhoRaisedTheirLevelAndIsAdmin_ReadsOnlyTheLevelsOfTheirKeys_AndIsRefusedAsAnAdmin()
    {
        using var data = NewData(guestMode: false);
        await using var server = await StartAsync(data, MadeUpKiler.Raised);
        using var client = server.CreateSignedInClient();

        using var response = await SignInAsync(client, MadeUpKiler.Password);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var session = await ReadAsync(response);
        Assert.Equal([0, 1], Levels(session));
        Assert.True((bool?)session["isAdmin"], "The page does not get the screens of the file.");
        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.Forbidden], await StatusesAsync(client));
        await AssertRefusedAsync(client, Admin, AccessRules.Message(AccessRefusal.NotAdmin));
    }

    [Theory]
    [InlineData(MadeUpKiler.User, null, AccessRefusal.CannotEdit, AccessRefusal.NotAdmin)]
    [InlineData(MadeUpKiler.Editor, null, null, AccessRefusal.NotAdmin)]
    [InlineData(MadeUpKiler.Temporary, AccessRefusal.MustChangePassword, AccessRefusal.MustChangePassword, AccessRefusal.MustChangePassword)]
    public async Task EachKindOfEndpoint_RefusesTheSessionsThatItMust(string account, AccessRefusal? reader, AccessRefusal? editor, AccessRefusal? admin)
    {
        using var data = NewData(guestMode: true);
        await using var server = await StartAsync(data, account);
        using var client = server.CreateSignedInClient();
        using (var response = await SignInAsync(client, MadeUpKiler.Password))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(account == MadeUpKiler.Temporary, (bool?)(await ReadAsync(response))["mustChangePassword"]);
        }

        foreach (var (address, refusal) in new[] { (Reader, reader), (Editor, editor), (Admin, admin) })
        {
            if (refusal is { } refused)
            {
                await AssertRefusedAsync(client, address, AccessRules.Message(refused));
            }
            else
            {
                using var response = await client.GetAsync(address, Cancel);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            }
        }
    }

    [Fact]
    public async Task WithoutGuestMode_OnlyWhatNeedsNothingAnswers_BeforeASignIn()
    {
        using var data = NewData(guestMode: false);
        await using var server = await StartAsync(data, MadeUpKiler.Admin);
        using var client = server.CreateSignedInClient();

        var session = await GetSessionAsync(client);

        Assert.Equal(MadeUpKiler.Admin, (string?)session["account"]);
        Assert.False((bool?)session["signedIn"]);
        Assert.False((bool?)session["guestMode"]);
        Assert.Empty(Levels(session));
        using (var state = await client.GetAsync("/api/data", Cancel))
        {
            Assert.Equal(HttpStatusCode.OK, state.StatusCode);
        }

        foreach (var address in new[] { Reader, Editor, Admin })
        {
            await AssertRefusedAsync(client, address, AccessRules.Message(AccessRefusal.NotSignedIn));
        }
    }

    [Fact]
    public async Task WithGuestMode_AGuestReadsLevel0Only_AndWritesNothing()
    {
        using var data = NewData(guestMode: true);
        await using var server = await StartAsync(data, MadeUpKiler.Admin);
        using var client = server.CreateSignedInClient();

        var session = await GetSessionAsync(client);

        Assert.False((bool?)session["signedIn"]);
        Assert.True((bool?)session["guestMode"]);
        Assert.Equal([0], Levels(session));
        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.Forbidden, HttpStatusCode.Forbidden], await StatusesAsync(client));
        await AssertRefusedAsync(client, Editor, AccessRules.Message(AccessRefusal.NotSignedIn));
    }

    [Fact]
    public async Task AKeyInUse_WhenALockComes_AnswersAsLocked()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        using var data = NewData(guestMode: false);
        await using var server = await StartAsync(data, MadeUpKiler.User, app =>
            app.MapGet("/api/test/key", (HttpContext context) =>
            {
                var session = Assert.IsType<Session>(AccessRules.SessionOf(context));

                // The lock comes while the request has the key, between two uses of it.
                return session.Use(1, key =>
                {
                    entered.SetResult();
                    Assert.True(release.Wait(Quick), "The test did not lock the session.");
                    return LevelCheck.Verify(key, MadeUpKiler.CheckOfLevel1, 1);
                }) ? "verified" : "not verified";
            }).Needs(Need.Reader));
        using var client = server.CreateSignedInClient();
        using (var response = await SignInAsync(client, MadeUpKiler.Password))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        var request = client.GetAsync("/api/test/key", Cancel);
        await entered.Task.WaitAsync(Quick, Cancel);
        using (var locked = await client.DeleteAsync("/api/session", Cancel))
        {
            Assert.Equal(HttpStatusCode.OK, locked.StatusCode);
        }

        release.Set();

        using var answer = await request;
        await AssertErrorAsync(answer, HttpStatusCode.Forbidden, AccessRules.Locked);
    }

    [Fact]
    public async Task TheStop_LocksTheSession()
    {
        using var data = NewData(guestMode: false);
        var server = await StartAsync(data, MadeUpKiler.User);
        CurrentSession sessions;
        Session session;
        try
        {
            sessions = server.Services.GetRequiredService<CurrentSession>();
            using var client = server.CreateSignedInClient();
            using var response = await SignInAsync(client, MadeUpKiler.Password);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            session = Assert.IsType<Session>(sessions.Session);
        }
        finally
        {
            await server.DisposeAsync();
        }

        Assert.True(session.IsLocked, "The stop left the keys of the session.");
        Assert.Null(sessions.Session);
    }

    [Fact]
    public async Task SignIn_WithAPasswordThatDoesNotFit_IsABadRequest()
    {
        using var data = NewData(guestMode: false);
        await using var server = await StartAsync(data, MadeUpKiler.Admin);
        using var client = server.CreateSignedInClient();

        using var response = await SignInAsync(client, "demo-pw");

        await AssertErrorAsync(response, HttpStatusCode.BadRequest, "A password has 8 to 1024 characters.");
    }

    [Fact]
    public async Task SignIn_ForAnAccountThatCannotSignIn_IsAConflict()
    {
        using var data = NewData(guestMode: false);
        await using var server = await StartAsync(data, account: null);
        using var client = server.CreateSignedInClient();

        using var response = await SignInAsync(client, MadeUpKiler.Password);

        await AssertErrorAsync(response, HttpStatusCode.Conflict, WizardApi.NoAccount);
        Assert.Null((string?)(await GetSessionAsync(client))["account"]);
    }

    [Fact]
    public async Task SignIn_WithoutData_IsAConflict()
    {
        await using var server = await KilerServer.StartAsync(KilerHost.Build(new StartOptions(), account: new FakeAccount(MadeUpKiler.Admin)));
        using var client = server.CreateSignedInClient();

        using var response = await SignInAsync(client, MadeUpKiler.Password);

        await AssertErrorAsync(response, HttpStatusCode.Conflict, SessionApi.NoData);
    }

    [Fact]
    public async Task EveryEndpointOfKiler_StatesWhatItNeeds()
    {
        await using var app = KilerHost.Build(new StartOptions(), account: new FakeAccount(MadeUpKiler.Admin));

        var endpoints = ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints).ToList();

        Assert.True(endpoints.Count >= 7, "Kiler has fewer endpoints than it had in M4.4.");
        foreach (var endpoint in endpoints)
        {
            Assert.True(endpoint.Metadata.GetMetadata<NeedMetadata>() is not null, $"{endpoint.DisplayName} does not state what it needs.");

            // A short-circuit endpoint runs right after routing, so the check of the rights never sees it. Its metadata is internal.
            Assert.True(endpoint.Metadata.All(item => item.GetType().Name != "ShortCircuitMetadata"), $"{endpoint.DisplayName} is short-circuit.");
        }
    }

    [Fact]
    public async Task AnEndpointThatStatesNothing_IsAFaultOfTheCode_NotAnEndpointThatNeedsNothing()
    {
        var app = KilerHost.Build(new StartOptions(), account: new FakeAccount(MadeUpKiler.Admin));
        app.MapGet("/api/test/unstated", () => "answered");
        await using var server = await KilerServer.StartAsync(app);
        using var client = server.CreateSignedInClient();

        using var response = await client.GetAsync("/api/test/unstated", Cancel);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.NotEqual("answered", await response.Content.ReadAsStringAsync(Cancel));
    }

    // Routing answers them with endpoints of its own, which state no need (code and security review).
    [Fact]
    public async Task AWrongMethodOrContentType_IsStillAnsweredAsBefore()
    {
        await using var server = await KilerServer.StartAsync(KilerHost.Build(new StartOptions(), account: new FakeAccount(MadeUpKiler.Admin)));
        using var client = server.CreateSignedInClient();

        using var put = await client.PutAsync("/api/session", new StringContent("{}", Encoding.UTF8, "application/json"), Cancel);
        using var text = await client.PostAsync("/api/session", new StringContent("{}", Encoding.UTF8, "text/plain"), Cancel);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, put.StatusCode);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, text.StatusCode);
    }

    private static Task<HttpResponseMessage> SignInAsync(HttpClient client, string password) =>
        client.PostAsync("/api/session", new StringContent(new JsonObject { ["password"] = password }.ToJsonString(), Encoding.UTF8, "application/json"), Cancel);

    private static async Task<JsonObject> GetSessionAsync(HttpClient client)
    {
        using var response = await client.GetAsync("/api/session", Cancel);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadAsync(response);
    }

    /// <summary>The answers of the test endpoints that need a reader, an editor and an admin.</summary>
    private static async Task<HttpStatusCode[]> StatusesAsync(HttpClient client)
    {
        var statuses = new List<HttpStatusCode>();
        foreach (var address in new[] { Reader, Editor, Admin })
        {
            using var response = await client.GetAsync(address, Cancel);
            statuses.Add(response.StatusCode);
        }

        return [.. statuses];
    }

    private static async Task AssertRefusedAsync(HttpClient client, string address, string error)
    {
        using var response = await client.GetAsync(address, Cancel);
        await AssertErrorAsync(response, HttpStatusCode.Forbidden, error);
    }

    private static async Task AssertErrorAsync(HttpResponseMessage response, HttpStatusCode status, string error)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal(error, (string?)(await ReadAsync(response))["error"]);
    }

    private static async Task<JsonObject> ReadAsync(HttpResponseMessage response)
    {
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        return Assert.IsType<JsonObject>(JsonNode.Parse(await response.Content.ReadAsStringAsync(Cancel)));
    }

    private static int[] Levels(JsonObject session) => [.. Assert.IsType<JsonArray>(session["levels"]).Select(level => (int)level!)];

    /// <summary>A data folder with the made-up Kiler, and a local folder of the test.</summary>
    private KilerData NewData(bool guestMode)
    {
        var place = DataPlace.For(Path.Combine(_folder.Path, "data"), Path.Combine(_folder.Path, "Kiler"));
        MadeUpKiler.WriteTo(MadeUpData.Create(place.DataFolder, Path.Combine(_folder.Path, "writer")), guestMode);
        return new KilerData(place, FileOwner.Instance, TimeProvider.System, _output, _errors);
    }

    /// <summary>
    /// Starts Kiler with the data and the account of the test, with the test endpoints of a reader, an editor and an admin and
    /// those of <paramref name="map"/>, and waits until the copy is open.
    /// </summary>
    private static async Task<KilerServer> StartAsync(KilerData data, string? account, Action<WebApplication>? map = null)
    {
        var app = KilerHost.Build(new StartOptions { DataFolder = data.Folder }, data: data, account: new FakeAccount(account));
        app.MapGet(Reader, () => "read").Needs(Need.Reader);
        app.MapGet(Editor, () => "edited").Needs(Need.Editor);
        app.MapGet(Admin, () => "administered").Needs(Need.Admin);
        map?.Invoke(app);
        var server = await KilerServer.StartAsync(app, data.Folder);
        for (var waited = Stopwatch.StartNew(); data.State != DataState.Open; await Task.Delay(TimeSpan.FromMilliseconds(20), Cancel))
        {
            Assert.True(waited.Elapsed < Quick, $"The state stayed {data.State}, not open.");
        }

        return server;
    }
}

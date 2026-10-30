using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Kiler.Core;
using Microsoft.AspNetCore.Builder;

namespace Kiler.App.Tests;

/// <summary><c>POST /api/session/password</c> (M4.5a): the change of password on a made-up Kiler (<see cref="MadeUpKiler"/>).</summary>
public sealed class PasswordApiTests : IDisposable
{
    private const string Reader = "/api/test/reader";

    /// <summary>Made up, and compared only with <c>Assert.True</c> (SKILL.md, "Tests").</summary>
    private const string NewPassword = "demo-password-2";

    /// <summary>The longest wait for a load of a few files on the local disk, on a busy CI machine.</summary>
    private static readonly TimeSpan Quick = TimeSpan.FromSeconds(30);

    private readonly TempFolder _folder = new();
    private readonly LineWriter _output = new();
    private readonly LineWriter _errors = new();
    private MadeUpData? _writer;

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        _output.Dispose();
        _errors.Dispose();
        _folder.Dispose();
    }

    [Fact]
    public async Task ForcedChange_AfterATemporaryPassword_GivesTheRights_AndOnlyTheNewPasswordSignsIn()
    {
        using var data = NewData();
        await using var server = await StartAsync(data, MadeUpKiler.Temporary);
        using var client = server.CreateSignedInClient();
        using (var signedIn = await SignInAsync(client, MadeUpKiler.Password))
        {
            Assert.True((bool?)(await ReadAsync(signedIn))["mustChangePassword"]);
        }

        using (var refused = await client.GetAsync(Reader, Cancel))
        {
            await AssertErrorAsync(refused, HttpStatusCode.Forbidden, AccessRules.Message(AccessRefusal.MustChangePassword));
        }

        using (var changed = await ChangeAsync(client, null, NewPassword))
        {
            Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
            var session = await ReadAsync(changed);
            Assert.True((bool?)session["signedIn"]);
            Assert.False((bool?)session["mustChangePassword"]);
        }

        using (var read = await client.GetAsync(Reader, Cancel))
        {
            Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        }

        using (await client.DeleteAsync("/api/session", Cancel))
        using (var old = await SignInAsync(client, MadeUpKiler.Password))
        {
            await AssertErrorAsync(old, HttpStatusCode.Forbidden, SessionApi.Message(SignInRefusal.WrongPassword));
        }

        using (var again = await SignInAsync(client, NewPassword))
        {
            Assert.Equal(HttpStatusCode.OK, again.StatusCode);
            Assert.False((bool?)(await ReadAsync(again))["mustChangePassword"]);
        }

        var lines = _output.Lines.Concat(_errors.Lines).ToList();
        Assert.True(lines.All(line => !line.Contains(MadeUpKiler.Password, StringComparison.Ordinal) && !line.Contains(NewPassword, StringComparison.Ordinal)), "A password is in a line of the console.");
    }

    [Fact]
    public async Task VoluntaryChange_WithTheCurrentPassword_ChangesIt()
    {
        using var data = NewData();
        await using var server = await StartAsync(data, MadeUpKiler.User);
        using var client = server.CreateSignedInClient();
        (await SignInAsync(client, MadeUpKiler.Password)).Dispose();

        using (var changed = await ChangeAsync(client, MadeUpKiler.Password, NewPassword))
        {
            Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        }

        using (await client.DeleteAsync("/api/session", Cancel))
        using (var again = await SignInAsync(client, NewPassword))
        {
            Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        }
    }

    [Fact]
    public async Task VoluntaryChange_WithAWrongCurrentPassword_IsRefused_AndTheOldPasswordStillSignsIn()
    {
        using var data = NewData();
        await using var server = await StartAsync(data, MadeUpKiler.User);
        using var client = server.CreateSignedInClient();
        (await SignInAsync(client, MadeUpKiler.Password)).Dispose();

        using (var refused = await ChangeAsync(client, "demo-password-3", NewPassword))
        {
            await AssertErrorAsync(refused, HttpStatusCode.Forbidden, PasswordApi.Message(PasswordChangeOutcome.WrongPassword));
        }

        using (var missing = await ChangeAsync(client, null, NewPassword))
        {
            await AssertErrorAsync(missing, HttpStatusCode.BadRequest, PasswordApi.CurrentMissing);
        }

        using (await client.DeleteAsync("/api/session", Cancel))
        using (var again = await SignInAsync(client, MadeUpKiler.Password))
        {
            Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        }
    }

    [Theory]
    [InlineData(7, 8)]
    [InlineData(1025, 8)]
    [InlineData(8, 7)]
    [InlineData(8, 1025)]
    public async Task Change_WithAPasswordThatDoesNotFit_IsABadRequest(int newLength, int currentLength)
    {
        using var data = NewData();
        await using var server = await StartAsync(data, MadeUpKiler.User);
        using var client = server.CreateSignedInClient();
        (await SignInAsync(client, MadeUpKiler.Password)).Dispose();

        using var response = await ChangeAsync(client, new string('c', currentLength), new string('n', newLength));

        await AssertErrorAsync(response, HttpStatusCode.BadRequest, Password.Invalid(new string('c', 7)));
    }

    [Fact]
    public async Task Change_WithoutASession_IsRefused()
    {
        using var data = NewData();
        await using var server = await StartAsync(data, MadeUpKiler.User);
        using var client = server.CreateSignedInClient();

        using var response = await ChangeAsync(client, MadeUpKiler.Password, NewPassword);

        await AssertErrorAsync(response, HttpStatusCode.Forbidden, AccessRules.Message(AccessRefusal.NotSignedIn));
    }

    [Fact]
    public async Task Change_WhereALevelOfTheUserFileDidNotVerify_IsRefused()
    {
        // The key of the top level in the file of this user is a key of their own, which unlocks and does not verify.
        using var data = NewData();
        await using var server = await StartAsync(data, MadeUpKiler.Raised);
        using var client = server.CreateSignedInClient();
        (await SignInAsync(client, MadeUpKiler.Password)).Dispose();

        using var response = await ChangeAsync(client, MadeUpKiler.Password, NewPassword);

        await AssertErrorAsync(response, HttpStatusCode.Forbidden, PasswordApi.Message(PasswordChangeOutcome.LevelNotUnlocked));
    }

    [Fact]
    public async Task Change_WhereTheUserFileHasChangedSinceTheSignIn_AsksForANewSignIn()
    {
        using var data = NewData();
        await using var server = await StartAsync(data, MadeUpKiler.Temporary);
        using var client = server.CreateSignedInClient();
        (await SignInAsync(client, MadeUpKiler.Password)).Dispose();

        // As an admin who gives a new temporary password meanwhile: the share has a version that the team has not followed.
        var user = MadeUpKiler.UserOf(MadeUpKiler.Temporary);
        _writer!.Replace(user.Key, 1, user.ToContent());
        using var response = await ChangeAsync(client, null, NewPassword);

        await AssertErrorAsync(response, HttpStatusCode.Conflict, PasswordApi.Message(PasswordChangeOutcome.UserChanged));
        Assert.True((bool?)(await GetSessionAsync(client))["mustChangePassword"]);
    }

    private static Task<HttpResponseMessage> SignInAsync(HttpClient client, string password) =>
        client.PostAsync("/api/session", new StringContent(new JsonObject { ["password"] = password }.ToJsonString(), Encoding.UTF8, "application/json"), Cancel);

    private static Task<HttpResponseMessage> ChangeAsync(HttpClient client, string? current, string password) =>
        client.PostAsync(
            "/api/session/password",
            new StringContent(new JsonObject { ["current"] = current, ["password"] = password }.ToJsonString(), Encoding.UTF8, "application/json"),
            Cancel);

    private static async Task<JsonObject> GetSessionAsync(HttpClient client)
    {
        using var response = await client.GetAsync("/api/session", Cancel);
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
    private KilerData NewData()
    {
        var place = DataPlace.For(Path.Combine(_folder.Path, "data"), Path.Combine(_folder.Path, "Kiler"));
        _writer = MadeUpKiler.WriteTo(MadeUpData.Create(place.DataFolder, Path.Combine(_folder.Path, "writer")), guestMode: false);
        return new KilerData(place, FileOwner.Instance, TimeProvider.System, _output, _errors);
    }

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

using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Kiler.Core;
using Kiler.Crypto;
using Kiler.Storage;
using Microsoft.Extensions.Time.Testing;

namespace Kiler.App.Tests;

/// <summary><c>POST /api/wizard</c> (M4.3): a new Kiler in the data folder of the start, only where one may be made.</summary>
public sealed partial class WizardApiTests : IDisposable
{
    private const string Account = "demo.admin";

    // Made up, and compared only with Assert.True (SKILL.md, "Tests").
    private const string DemoPassword = "demo-password-1";

    /// <summary>The line of a wizard refused by a local copy that has had documents.</summary>
    private const string HadDocuments = "The wizard cannot make a new Kiler in the data folder: its local copy has had the documents of a Kiler.";

    /// <summary>The longest wait for a load of a few files on the local disk, on a busy CI machine.</summary>
    private static readonly TimeSpan Quick = TimeSpan.FromSeconds(30);

    private readonly TempFolder _folder = new();
    private readonly LineWriter _output = new();
    private readonly LineWriter _errors = new();
    private readonly DataPlace _place;

    public WizardApiTests()
    {
        _place = DataPlace.For(Path.Combine(_folder.Path, "data"), Path.Combine(_folder.Path, "Kiler"));
    }

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        _output.Dispose();
        _errors.Dispose();
        _folder.Dispose();
    }

    [Fact]
    public async Task Wizard_WhereTheDataFolderIsNotThere_MakesAKiler_OpensIt_AndGivesTheRecoveryKeyOnce()
    {
        string? recoveryKey;
        using (var data = NewData(TimeProvider.System))
        {
            await using var server = await StartAsync(data, Account);
            Assert.Equal(DataState.Missing, data.State);
            using var client = server.CreateSignedInClient();

            using var response = await PostAsync(client, Form());

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            recoveryKey = (string?)(await ReadAsync(response))["recoveryKey"];
            Assert.NotNull(recoveryKey);
            Assert.True(ShownRecoveryKey().IsMatch(recoveryKey), "The recovery key of the answer is not in the form that is shown.");

            // The new data folder is read and watched as at a start.
            await WaitForStateAsync(data, DataState.Open);
            var files = Assert.IsType<Team>(data.Team).Files;
            Assert.Equal(["Guest", "Team", "Prod"], Valid(files.Levels).Levels.Select(level => level.Name));
            Assert.Equal(45, Valid(files.Settings).AutoLockMinutes);
            Assert.Equal([Account], files.Users.Keys);
            Assert.True(Valid(files.Users[Account]).IsAdmin, "The first user is not an admin.");
            Assert.Equal(["db", "link", "note", "sql"], files.Templates.Keys);
            Assert.All(files.Templates.Values, file => Assert.True(file.IsValid, file.Invalid));
            Assert.False((await client.GetStringAsync("/api/data", Cancel)).Contains(recoveryKey, StringComparison.Ordinal), "The state of the data gives the recovery key.");
        }

        Assert.Equal(
            [
                $"The wizard has made a new Kiler in the data folder {_place.DataFolder}.",
                $"Kiler opens the data folder {_place.DataFolder}, with its local copy in {_place.CopyFolder}.",
            ],
            _output.Lines.Take(2));
        Assert.Equal([$"Kiler runs without data: the data folder does not exist: {_place.DataFolder}"], _errors.Lines);
        AssertInNoFileAndNoLine(recoveryKey, "The recovery key");
        AssertInNoFileAndNoLine(recoveryKey.Replace("-", "", StringComparison.Ordinal), "The recovery key without its hyphens");
        AssertInNoFileAndNoLine(DemoPassword, "The password");
    }

    [Fact]
    public async Task Wizard_InAnOpenDataFolderOfEmptyFolders_MakesAKiler_ThatTheTeamHasAtOnce()
    {
        DataFolder.Create(_place.DataFolder, FileOwner.Instance);
        using var data = NewData(TimeProvider.System);
        await using var server = await StartAsync(data, Account);
        Assert.Equal(DataState.Open, data.State);
        var team = Assert.IsType<Team>(data.Team);
        using var client = server.CreateSignedInClient();

        using var response = await PostAsync(client, Form());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // No load gives what this Kiler wrote through the copy, so the team has read the copy again.
        Assert.Equal([Account], team.Files.Users.Keys);
        Assert.Equal(2, Valid(team.Files.Levels).Top);
        Assert.Equal(["db", "link", "note", "sql"], team.Files.Templates.Keys);
        var state = Assert.IsType<JsonObject>(JsonNode.Parse(await client.GetStringAsync("/api/data", Cancel)));
        Assert.Equal("open", (string?)state["state"]);
        Assert.Null(state["levels"]);
        Assert.Null(state["settings"]);
        Assert.Equal($"The wizard has made a new Kiler in the data folder {_place.DataFolder}.", _output.Lines[^1]);
        Assert.Empty(_errors.Lines);
    }

    [Fact]
    public async Task Wizard_InAnOpenDataFolderWithAFileThatIsNotADocument_IsRefused_AndWritesNothing()
    {
        // The copy never takes it, so only the check of the data folder sees it.
        DataFolder.Create(_place.DataFolder, FileOwner.Instance);
        File.WriteAllText(Path.Combine(_place.DataFolder, "notes.txt"), "Made up.");
        using var data = NewData(TimeProvider.System);
        await using var server = await StartAsync(data, Account);
        using var client = server.CreateSignedInClient();

        using var response = await PostAsync(client, Form());

        await AssertRefusedAsync(response, WizardRefusal.NotNew);
        Assert.False(File.Exists(Path.Combine(_place.DataFolder, "levels.json")), "The wizard wrote levels.json.");
        Assert.StartsWith("The wizard cannot make a new Kiler in the data folder: The folder holds more than the empty folders of a new data folder: ", Assert.Single(_errors.Lines), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Wizard_WhereLevelsJsonIsThere_IsRefused_AndLeavesTheKilerAsItIs()
    {
        MadeUp().Create(LevelsFile.Key, Levels());
        var levels = Path.Combine(_place.DataFolder, "levels.json");
        var before = File.ReadAllBytes(levels);
        using var data = NewData(TimeProvider.System);
        await using var server = await StartAsync(data, Account);
        using var client = server.CreateSignedInClient();

        using var response = await PostAsync(client, Form());

        await AssertRefusedAsync(response, WizardRefusal.NotNew);
        Assert.Equal(before, File.ReadAllBytes(levels));
        Assert.Empty(Directory.GetFiles(Path.Combine(_place.DataFolder, DataFolder.Users)));
    }

    [Fact]
    public async Task Wizard_WhenTheDataFolderOfAKilerHasGone_BeforeALoadSaysSo_IsRefused_AndMakesNothing()
    {
        MadeUp().Create(SettingsFile.Key, new SettingsFile(guestMode: false, autoLockMinutes: 30).ToContent());
        using var data = NewData(new FakeTimeProvider(DateTimeOffset.UtcNow));
        await using var server = await StartAsync(data, Account);
        Directory.Delete(_place.DataFolder, recursive: true);
        using var client = server.CreateSignedInClient();

        using var response = await PostAsync(client, Form());

        await AssertRefusedAsync(response, WizardRefusal.NotNew);
        Assert.False(Directory.Exists(_place.DataFolder), "The wizard made the data folder again.");
        Assert.Equal([HadDocuments], _errors.Lines);
    }

    [Fact]
    public async Task Wizard_WhenADocumentOfTheOpenCopyHasGoneFromTheShare_IsRefused_AndMakesNothing()
    {
        MadeUp().Create(LevelsFile.Key, Levels());
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        using var data = NewData(clock);
        await using var server = await StartAsync(data, Account);
        File.Delete(Path.Combine(_place.DataFolder, "levels.json"));
        clock.Advance(DataWatch.Interval);
        await _output.WaitForLineAsync("Loaded the data folder: 0 new or changed, 1 gone, ", Quick);

        // The team no longer has it, and the copy does not list it for a restore before it has been gone for a while (M2.7a).
        Assert.Null(Assert.IsType<Team>(data.Team).Files.Levels);
        using var client = server.CreateSignedInClient();

        using var response = await PostAsync(client, Form());

        await AssertRefusedAsync(response, WizardRefusal.NotNew);
        Assert.False(File.Exists(Path.Combine(_place.DataFolder, "levels.json")), "The wizard wrote levels.json.");
        Assert.Equal([HadDocuments], _errors.Lines);
    }

    [Fact]
    public async Task Wizard_WhereTheDataFolderOfAKilerHasGone_AfterARestart_IsRefused_AndKeepsTheLocalCopy()
    {
        MadeUp().Create(LevelsFile.Key, Levels());
        using (var first = NewData(TimeProvider.System))
        {
            first.Start();
            await first.Started.WaitAsync(Quick, Cancel);
        }

        var before = File.ReadAllBytes(Path.Combine(_place.CopyFolder, "data", "levels.json"));
        Directory.Delete(_place.DataFolder, recursive: true);

        // On a fake clock, so the first load is the only one.
        using var data = NewData(new FakeTimeProvider(DateTimeOffset.UtcNow));
        await using var server = await StartAsync(data, Account);
        Assert.Equal(DataState.Missing, data.State);
        using var client = server.CreateSignedInClient();

        using var response = await PostAsync(client, Form());

        await AssertRefusedAsync(response, WizardRefusal.NotNew);
        Assert.False(File.Exists(Path.Combine(_place.DataFolder, "levels.json")), "The wizard wrote levels.json.");
        Assert.Equal(HadDocuments, _errors.Lines[^1]);

        // The data folder is there again, empty, and the copy keeps the last good bytes of the old Kiler for a restore, which
        // its first load has moved apart.
        await WaitForStateAsync(data, DataState.Open);
        Assert.False(Assert.IsType<DataCopy>(data.Copy).IsEmpty, "The copy lost the documents of the old Kiler.");
        Assert.Contains(
            Directory.EnumerateFiles(_place.CopyFolder, "*", SearchOption.AllDirectories).Where(file => Path.GetFileName(file) != KilerData.LockFile),
            file => File.ReadAllBytes(file).AsSpan().SequenceEqual(before));
    }

    [Fact]
    public async Task Wizard_WhereADataFolderWithDocumentsHasBeenMadeSinceTheStart_IsRefused_AndOpensIt()
    {
        using var data = NewData(TimeProvider.System);
        await using var server = await StartAsync(data, Account);
        Assert.Equal(DataState.Missing, data.State);
        MadeUp().Create("a1");
        using var client = server.CreateSignedInClient();

        using var response = await PostAsync(client, Form());

        await AssertRefusedAsync(response, WizardRefusal.NotNew);
        Assert.False(File.Exists(Path.Combine(_place.DataFolder, "levels.json")), "The wizard wrote levels.json.");
        Assert.StartsWith("The wizard cannot make a new Kiler in the data folder: The folder holds more than the empty folders of a new data folder: ", _errors.Lines[^1], StringComparison.Ordinal);
        await WaitForStateAsync(data, DataState.Open);
        Assert.Single(Assert.IsType<DataCopy>(data.Copy).Documents());
    }

    [Fact]
    public async Task Wizard_WithoutADataFolderChosen_IsRefused()
    {
        await using var server = await KilerServer.StartAsync(KilerHost.Build(new StartOptions(), account: new FakeAccount(Account)));
        using var client = server.CreateSignedInClient();

        using var response = await PostAsync(client, Form());

        await AssertRefusedAsync(response, WizardRefusal.NoFolder);
    }

    [Fact]
    public async Task Wizard_WhileTheDataFolderCannotBeReached_IsRefused_AndWritesNothing()
    {
        DataFolder.Create(_place.DataFolder, FileOwner.Instance);
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        using var data = NewData(clock);
        await using var server = await StartAsync(data, Account);
        var away = $"{_place.DataFolder}-away";
        Directory.Move(_place.DataFolder, away);
        clock.Advance(DataWatch.Interval);
        await WaitForStateAsync(data, DataState.Offline);
        using var client = server.CreateSignedInClient();

        using var response = await PostAsync(client, Form());

        await AssertRefusedAsync(response, WizardRefusal.Offline);
        Assert.False(Directory.Exists(_place.DataFolder), "The wizard made a data folder.");
        Assert.Empty(Directory.GetFiles(away, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Wizard_ForAnAccountThatCannotSignIn_IsRefused_AndMakesNothing()
    {
        using var data = NewData(TimeProvider.System);
        await using var server = await StartAsync(data, account: null);
        using var client = server.CreateSignedInClient();

        using var response = await PostAsync(client, Form());

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(WizardApi.NoAccount, (string?)(await ReadAsync(response))["error"]);
        Assert.False(Directory.Exists(_place.DataFolder), "The wizard made a data folder.");
    }

    [Theory]
    [InlineData("""["Guest","Team"]""", "demo-pw", "A password has 8 to 1024 characters.")]
    [InlineData("""["Guest","Team"]""", null, "A password has 8 to 1024 characters.")]
    [InlineData("""["Guest",null,"Prod"]""", DemoPassword, "Entry 1 of levels is null.")]
    [InlineData("""["Guest",""]""", DemoPassword, "The name of level 1 is blank or longer than 40 characters.")]
    [InlineData("""["Guest","Team","team"]""", DemoPassword, "Level 2 has the name of a level below it, without regard to case.")]
    [InlineData("""["0","1","2","3","4","5","6","7","8","9","10"]""", DemoPassword, "The levels are not 0 to a top level from 1 to 9.")]
    public async Task Wizard_WithValuesThatDoNotFit_IsABadRequest_AndMakesNothing(string levels, string? password, string error)
    {
        using var data = NewData(TimeProvider.System);
        await using var server = await StartAsync(data, Account);
        using var client = server.CreateSignedInClient();
        var form = Form(levels: Assert.IsType<JsonArray>(JsonNode.Parse(levels)));
        form["password"] = password ?? new string('p', Password.MaxLength + 1);

        using var response = await PostAsync(client, form);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(error, (string?)(await ReadAsync(response))["error"]);
        Assert.False(Directory.Exists(_place.DataFolder), "The wizard made a data folder.");
    }

    [Fact]
    public async Task TwoWizardsAtOnce_OnOneDataFolder_MakeOneKiler_AndTheOtherIsRefused()
    {
        using var otherOutput = new LineWriter();
        using var otherErrors = new LineWriter();
        using var first = NewData(TimeProvider.System);
        using var other = new KilerData(DataPlace.For(_place.DataFolder, Path.Combine(_folder.Path, "Kiler2")), FileOwner.Instance, TimeProvider.System, otherOutput, otherErrors);
        await using var firstServer = await StartAsync(first, Account);
        await using var otherServer = await StartAsync(other, "other.admin");
        using var firstClient = firstServer.CreateSignedInClient();
        using var otherClient = otherServer.CreateSignedInClient();

        var answers = await Task.WhenAll(PostAsync(firstClient, Form()), PostAsync(otherClient, Form()));

        try
        {
            Assert.Equal([HttpStatusCode.OK, HttpStatusCode.Conflict], answers.Select(answer => answer.StatusCode).Order());
            var refused = Assert.Single(answers, answer => answer.StatusCode == HttpStatusCode.Conflict);
            Assert.Contains((string?)(await ReadAsync(refused))["error"], new[] { WizardApi.Message(WizardRefusal.Exists), WizardApi.Message(WizardRefusal.NotNew) });
            var winner = answers[0].StatusCode == HttpStatusCode.OK ? Account : "other.admin";
            Assert.Equal([Path.Combine(_place.DataFolder, DataFolder.Users, $"{winner}.json")], Directory.GetFiles(Path.Combine(_place.DataFolder, DataFolder.Users)));

            // The other opens the Kiler that the first made, as a start would.
            await WaitForStateAsync(first, DataState.Open);
            await WaitForStateAsync(other, DataState.Open);
        }
        finally
        {
            foreach (var answer in answers)
            {
                answer.Dispose();
            }
        }
    }

    [GeneratedRegex("^[0-9A-HJKMNP-TV-Z]{4}(-[0-9A-HJKMNP-TV-Z]{4}){7}$")]
    private static partial Regex ShownRecoveryKey();

    /// <summary>A made-up <c>levels.json</c> of another Kiler: the model does not decrypt its checks or keys.</summary>
    private static JsonObject Levels() =>
        new LevelsFile([new(0, "Guest", null), new(1, "Team", "check-1")], new LockedKeys("salt", new Dictionary<int, string> { [1] = "locked-1" })).ToContent();

    /// <summary>What the wizard of the page sends, with the levels of the test.</summary>
    private static JsonObject Form(JsonArray? levels = null) => new()
    {
        ["levels"] = levels ?? new JsonArray("Guest", "Team", "Prod"),
        ["displayName"] = "Demo Admin",
        ["password"] = DemoPassword,
        ["guestMode"] = true,
        ["autoLockMinutes"] = 45,
    };

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, JsonObject form) =>
        client.PostAsync("/api/wizard", new StringContent(form.ToJsonString(), Encoding.UTF8, "application/json"), Cancel);

    private static async Task<JsonObject> ReadAsync(HttpResponseMessage response)
    {
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        return Assert.IsType<JsonObject>(JsonNode.Parse(await response.Content.ReadAsStringAsync(Cancel)));
    }

    private static async Task AssertRefusedAsync(HttpResponseMessage response, WizardRefusal refusal)
    {
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(WizardApi.Message(refusal), (string?)(await ReadAsync(response))["error"]);
    }

    private static T Valid<T>(TeamFile<T>? file)
        where T : class
    {
        Assert.NotNull(file);
        Assert.True(file.IsValid, file.Invalid);
        return file.Model;
    }

    /// <summary>Waits for a state that a handler of the watch sets after its line.</summary>
    private static async Task WaitForStateAsync(KilerData data, DataState state)
    {
        for (var waited = Stopwatch.StartNew(); data.State != state; await Task.Delay(TimeSpan.FromMilliseconds(20), Cancel))
        {
            Assert.True(waited.Elapsed < Quick, $"The state stayed {data.State}, not {state}.");
        }
    }

    /// <summary>Checks that a secret is in no file of the test, the data folder and the local copy, and in no line of the console.</summary>
    private void AssertInNoFileAndNoLine(string secret, string what)
    {
        var bytes = Encoding.UTF8.GetBytes(secret);
        foreach (var file in Directory.EnumerateFiles(_folder.Path, "*", SearchOption.AllDirectories))
        {
            Assert.True(File.ReadAllBytes(file).AsSpan().IndexOf(bytes) < 0, $"{what} is in {file}.");
        }

        Assert.True(_output.Lines.Concat(_errors.Lines).All(line => !line.Contains(secret, StringComparison.Ordinal)), $"{what} is in a line of the console.");
    }

    private MadeUpData MadeUp() => MadeUpData.Create(_place.DataFolder, Path.Combine(_folder.Path, "writer"));

    private KilerData NewData(TimeProvider time) => new(_place, FileOwner.Instance, time, _output, _errors);

    /// <summary>Starts Kiler with the data and the account of the test, and waits for the start of the data.</summary>
    private static async Task<KilerServer> StartAsync(KilerData data, string? account)
    {
        var server = await KilerServer.StartAsync(KilerHost.Build(new StartOptions { DataFolder = data.Folder }, data: data, account: new FakeAccount(account)), data.Folder);
        await data.Started.WaitAsync(Quick, Cancel);
        return server;
    }
}

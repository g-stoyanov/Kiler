using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kiler.Core;
using Kiler.Crypto;

namespace Kiler.App.Tests;

/// <summary><c>GET /api/data</c> (M4.2): the state of the data, and whether the levels and the settings of the local copy can be used.</summary>
public sealed class DataApiTests : IDisposable
{
    /// <summary>The longest wait for a load of a few files on the local disk, on a busy CI machine.</summary>
    private static readonly TimeSpan Quick = TimeSpan.FromSeconds(30);

    private readonly TempFolder _folder = new();
    private readonly LineWriter _output = new();
    private readonly LineWriter _errors = new();
    private readonly DataPlace _place;

    public DataApiTests()
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
    public async Task Data_WithoutADataFolder_IsNoFolder()
    {
        await using var server = await KilerServer.StartAsync();
        using var client = server.CreateSignedInClient();

        using var response = await client.GetAsync("/api/data", Cancel);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("""{"state":"noFolder","folder":null,"levels":null,"settings":null}""", await response.Content.ReadAsStringAsync(Cancel));
    }

    [Fact]
    public async Task Data_WhenTheDataFolderDoesNotExist_IsMissing_WithTheFolder()
    {
        var data = await GetAsync();

        Assert.Equal("missing", (string?)data["state"]);
        Assert.Equal(_place.DataFolder, (string?)data["folder"]);
        Assert.Null(data["levels"]);
        Assert.Null(data["settings"]);
    }

    [Fact]
    public async Task Data_WithAnOpenCopy_WhoseLevelsAndSettingsFit_IsOpen_WithoutReasons()
    {
        var made = MadeUpData.Create(_place.DataFolder, Path.Combine(_folder.Path, "writer"));
        made.Create(LevelsFile.Key, new LevelsFile([new(0, "Guest", null), new(1, "Team", "check-1")], new LockedKeys("salt", new Dictionary<int, string> { [1] = "locked-1" })).ToContent());
        made.Create(SettingsFile.Key, new SettingsFile(guestMode: false, autoLockMinutes: 30).ToContent());

        var data = await GetAsync();

        Assert.Equal("open", (string?)data["state"]);
        Assert.Null(data["levels"]);
        Assert.Null(data["settings"]);
    }

    [Fact]
    public async Task Data_WithAnOpenCopy_SaysWhyItsLevelsAndSettingsCannotBeUsed()
    {
        MadeUpData.Create(_place.DataFolder, Path.Combine(_folder.Path, "writer"));
        File.WriteAllText(Path.Combine(_place.DataFolder, "levels.json"), "Not JSON.");

        var data = await GetAsync();

        Assert.Equal("open", (string?)data["state"]);
        Assert.Equal("The file is not valid JSON.", (string?)data["levels"]);
        Assert.Equal(DataApi.MissingFile, (string?)data["settings"]);
    }

    [Fact]
    public void EachState_HasItsNameInCamelCase_ForThePage()
    {
        foreach (var state in Enum.GetValues<DataState>())
        {
            Assert.Equal($"\"{JsonNamingPolicy.CamelCase.ConvertName(state.ToString())}\"", JsonSerializer.Serialize(state, KilerJsonContext.Default.DataState));
        }
    }

    /// <summary>Starts Kiler on the data place of the test, waits for the start of the data, and gives what the endpoint answers.</summary>
    private async Task<JsonObject> GetAsync()
    {
        using var data = new KilerData(_place, FileOwner.Instance, TimeProvider.System, _output, _errors);
        await using var server = await KilerServer.StartAsync(KilerHost.Build(new StartOptions { DataFolder = _place.DataFolder }, data: data), _place.DataFolder);
        await data.Started.WaitAsync(Quick, Cancel);
        using var client = server.CreateSignedInClient();
        return Assert.IsType<JsonObject>(JsonNode.Parse(await client.GetStringAsync("/api/data", Cancel)));
    }
}

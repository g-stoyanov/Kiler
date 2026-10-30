using System.Text.Json.Nodes;
using Kiler.Crypto;
using Kiler.Storage;

namespace Kiler.Core.Tests;

/// <summary>The levels, the settings, the users (M4.2) and the templates (M5.2) of the local copy in memory, following its loads.</summary>
public sealed class TeamTests : IDisposable
{
    private readonly TestData _data = new();

    public void Dispose() => _data.Dispose();

    [Fact]
    public void ANewTeam_ReadsTheLevelsTheSettingsAndTheUsers_OfTheCopy()
    {
        Write(LevelsFile.Key, Levels("Team").ToContent());
        Write(SettingsFile.Key, new SettingsFile(guestMode: true, autoLockMinutes: 15).ToContent());
        Write(UserFile.KeyOf("demo.user"), User("demo.user", "Demo User").ToContent());
        Write(UserFile.KeyOf("another.user"), User("another.user", "Another User").ToContent());
        Write(DocumentKey.InFolder(DataFolder.Records, "a1"), new JsonObject { ["name"] = "Demo record" });

        var files = new Team(_data.Copy).Files;

        Assert.Equal("Team", Valid(files.Levels).Levels[1].Name);
        Assert.Equal(1, files.Levels?.Version);
        Assert.Equal(15, Valid(files.Settings).AutoLockMinutes);
        Assert.Equal(["another.user", "demo.user"], files.Users.Keys);
        Assert.Equal("Demo User", Valid(files.Users["demo.user"]).DisplayName);
    }

    [Fact]
    public void ATeamOfACopyWithoutItsFiles_HasNone()
    {
        var files = new Team(_data.Copy).Files;

        Assert.Null(files.Levels);
        Assert.Null(files.Settings);
        Assert.Empty(files.Users);
    }

    [Fact]
    public void Follow_TakesANewUserFile()
    {
        var (copy, team) = Follower();
        Write(UserFile.KeyOf("demo.user"), User("demo.user", "Demo User").ToContent());

        team.Follow(copy.Load());

        var user = Assert.Single(team.Files.Users);
        Assert.Equal("demo.user", user.Key);
        Assert.Equal("Demo User", Valid(user.Value).DisplayName);
        Assert.Equal(1, user.Value.Version);
    }

    [Fact]
    public void Follow_TakesAChangedUserFile_WithItsVersion()
    {
        Write(UserFile.KeyOf("demo.user"), User("demo.user", "Demo User").ToContent());
        var (copy, team) = Follower();
        Assert.Equal(WriteOutcome.Written, _data.Copy.Replace(UserFile.KeyOf("demo.user"), 1, User("demo.user", "Renamed User").ToContent()).Outcome);

        team.Follow(copy.Load());

        var user = team.Files.Users["demo.user"];
        Assert.Equal("Renamed User", Valid(user).DisplayName);
        Assert.Equal(2, user.Version);
    }

    [Fact]
    public void Follow_TakesADamagedUserFile_AsInvalid_WithoutAVersion()
    {
        Write(UserFile.KeyOf("demo.user"), User("demo.user", "Demo User").ToContent());
        var (copy, team) = Follower();
        File.WriteAllText(Path.Combine(_data.Folder, DataFolder.Users, "demo.user.json"), "Not JSON.");

        team.Follow(copy.Load());

        var user = team.Files.Users["demo.user"];
        Assert.False(user.IsValid);
        Assert.Null(user.Model);
        Assert.Equal("The file is not valid JSON.", user.Invalid);
        Assert.Null(user.Version);
    }

    [Fact]
    public void Follow_TakesAUserFileThatDoesNotFit_AsInvalid_WithItsVersion()
    {
        var (copy, team) = Follower();
        var content = User("demo.user", "Demo User").ToContent();
        content.Remove("displayName");
        Write(UserFile.KeyOf("demo.user"), content);

        team.Follow(copy.Load());

        var user = team.Files.Users["demo.user"];
        Assert.False(user.IsValid);
        Assert.Equal("displayName is missing.", user.Invalid);
        Assert.Equal(1, user.Version);
    }

    [Fact]
    public void Follow_DropsAUserFileThatHasGone()
    {
        Write(UserFile.KeyOf("demo.user"), User("demo.user", "Demo User").ToContent());
        Write(UserFile.KeyOf("another.user"), User("another.user", "Another User").ToContent());
        var (copy, team) = Follower();
        Assert.Equal(WriteOutcome.Deleted, _data.Copy.Delete(UserFile.KeyOf("demo.user"), 1).Outcome);

        team.Follow(copy.Load());

        Assert.Equal(["another.user"], team.Files.Users.Keys);
    }

    [Fact]
    public void Follow_TakesTheLevelsAndTheSettings_ChangedAndGone()
    {
        Write(LevelsFile.Key, Levels("Team").ToContent());
        Write(SettingsFile.Key, new SettingsFile(guestMode: false, autoLockMinutes: 30).ToContent());
        var (copy, team) = Follower();
        Assert.Equal(WriteOutcome.Written, _data.Copy.Replace(LevelsFile.Key, 1, Levels("Renamed").ToContent()).Outcome);
        Assert.Equal(WriteOutcome.Deleted, _data.Copy.Delete(SettingsFile.Key, 1).Outcome);

        team.Follow(copy.Load());

        Assert.Equal("Renamed", Valid(team.Files.Levels).Levels[1].Name);
        Assert.Equal(2, team.Files.Levels?.Version);
        Assert.Null(team.Files.Settings);
    }

    [Fact]
    public void Follow_OfALoadThatChangedNoneOfTheTeam_KeepsItsFiles()
    {
        Write(UserFile.KeyOf("demo.user"), User("demo.user", "Demo User").ToContent());
        var (copy, team) = Follower();
        var before = team.Files;
        Write(DocumentKey.InFolder(DataFolder.Records, "a1"), new JsonObject { ["name"] = "Demo record" });

        var load = copy.Load();
        team.Follow(load);

        Assert.Single(load.Changed);
        Assert.Same(before, team.Files);
    }

    [Fact]
    public void ANewTeam_ReadsTheTemplates_ThatFitAndThatDoNot()
    {
        Write(TemplateFile.KeyOf("note"), Note("Note").ToContent());
        Write(TemplateFile.KeyOf("bad"), new JsonObject { ["name"] = "Bad" });

        var files = new Team(_data.Copy).Files;

        Assert.Equal(["bad", "note"], files.Templates.Keys);
        Assert.Equal("Note", Valid(files.Templates["note"]).Name);
        Assert.Equal(("icon is missing.", 1), (files.Templates["bad"].Invalid, files.Templates["bad"].Version));
    }

    [Fact]
    public void Follow_TakesATemplate_New_Changed_Damaged_AndGone()
    {
        Write(TemplateFile.KeyOf("note"), Note("Note").ToContent());
        Write(TemplateFile.KeyOf("link"), Note("Link").ToContent());
        var (copy, team) = Follower();
        Write(TemplateFile.KeyOf("new"), Note("New").ToContent());
        Assert.Equal(WriteOutcome.Written, _data.Copy.Replace(TemplateFile.KeyOf("note"), 1, Note("Renamed").ToContent()).Outcome);
        Assert.Equal(WriteOutcome.Deleted, _data.Copy.Delete(TemplateFile.KeyOf("link"), 1).Outcome);

        team.Follow(copy.Load());

        Assert.Equal(["new", "note"], team.Files.Templates.Keys);
        Assert.Equal("New", Valid(team.Files.Templates["new"]).Name);
        Assert.Equal(("Renamed", 2), (Valid(team.Files.Templates["note"]).Name, team.Files.Templates["note"].Version));

        File.WriteAllText(Path.Combine(_data.Folder, DataFolder.Templates, "note.json"), "Not JSON.");
        team.Follow(copy.Load());

        Assert.Equal(("The file is not valid JSON.", null), (team.Files.Templates["note"].Invalid, team.Files.Templates["note"].Version));
    }

    [Fact]
    public void Reload_ReadsTheCopyAgain_WithWhatNoLoadGaveTheTeam()
    {
        var (copy, team) = Follower();
        Write(UserFile.KeyOf("demo.user"), User("demo.user", "Demo User").ToContent());

        // As a load that failed or went offline after it took the documents (core.md): the team has not seen it.
        copy.Load();
        Assert.Empty(team.Files.Users);

        team.Reload();

        Assert.Equal("Demo User", Valid(team.Files.Users["demo.user"]).DisplayName);
    }

    [Fact]
    public void Replace_TakesTheWriteOfThisKiler_AtItsNewVersion_ThoughNoLoadGivesIt()
    {
        Write(UserFile.KeyOf("demo.user"), User("demo.user", "Demo User").ToContent());
        var (copy, team) = Follower();

        Assert.Equal(WriteOutcome.Written, team.Replace(User("demo.user", "Renamed User"), 1).Outcome);

        var user = team.Files.Users["demo.user"];
        Assert.Equal("Renamed User", Valid(user).DisplayName);
        Assert.Equal(2, user.Version);
        team.Follow(copy.Load());
        Assert.Equal("Renamed User", Valid(team.Files.Users["demo.user"]).DisplayName);
    }

    [Fact]
    public void Replace_ThatIsNotWritten_LeavesTheTeamAsItIs()
    {
        Write(UserFile.KeyOf("demo.user"), User("demo.user", "Demo User").ToContent());
        var (_, team) = Follower();
        Assert.Equal(WriteOutcome.Written, _data.Copy.Replace(UserFile.KeyOf("demo.user"), 1, User("demo.user", "Changed Elsewhere").ToContent()).Outcome);
        var before = team.Files;

        Assert.Equal(WriteOutcome.Conflict, team.Replace(User("demo.user", "Renamed User"), 1).Outcome);

        Assert.Same(before, team.Files);
    }

    [Fact]
    public void Follow_OfALoadThatEndedBeforeAWriteOfThisKiler_KeepsTheWrite()
    {
        Write(UserFile.KeyOf("demo.user"), User("demo.user", "Demo User").ToContent());
        var (copy, team) = Follower();
        Assert.Equal(WriteOutcome.Written, _data.Copy.Replace(UserFile.KeyOf("demo.user"), 1, User("demo.user", "Changed Elsewhere").ToContent()).Outcome);

        // The watch raises a load after it has let go of the copy, so a write can come between its end and the team.
        var before = copy.Load();
        Assert.Equal(WriteOutcome.Written, team.Replace(User("demo.user", "Renamed User"), 2).Outcome);
        team.Follow(before);

        var user = team.Files.Users["demo.user"];
        Assert.Equal("Renamed User", Valid(user).DisplayName);
        Assert.Equal(3, user.Version);
    }

    [Fact]
    public void Follow_AfterTheLoadThatEndedBeforeAWrite_TakesTheChangesOfTheOthers()
    {
        Write(UserFile.KeyOf("demo.user"), User("demo.user", "Demo User").ToContent());
        var (copy, team) = Follower();
        var before = copy.Load();
        Assert.Equal(WriteOutcome.Written, team.Replace(User("demo.user", "Renamed User"), 1).Outcome);
        team.Follow(before);
        _data.Copy.Load();
        Assert.Equal(WriteOutcome.Written, _data.Copy.Replace(UserFile.KeyOf("demo.user"), 2, User("demo.user", "Changed Elsewhere").ToContent()).Outcome);

        team.Follow(copy.Load());

        var user = team.Files.Users["demo.user"];
        Assert.Equal("Changed Elsewhere", Valid(user).DisplayName);
        Assert.Equal(3, user.Version);
    }

    private static LevelsFile Levels(string name) =>
        new([new(0, "Guest", null), new(1, name, "check-1")], new LockedKeys("salt", new Dictionary<int, string> { [1] = "locked-1" }));

    private static UserFile User(string account, string displayName) =>
        new(account, displayName, level: 1, canEdit: true, isAdmin: false, mustChangePassword: false, new LockedKeys("salt", new Dictionary<int, string> { [1] = "locked-1" }));

    private static TemplateFile Note(string name) => new("note", name, "note", [new("text", "Text", FieldType.Markdown, level: 1)]);

    private static T Valid<T>(TeamFile<T>? file)
        where T : class
    {
        Assert.NotNull(file);
        Assert.True(file.IsValid, file.Invalid);
        return file.Model;
    }

    /// <summary>Another local copy of the data folder, loaded once, and a team that follows it.</summary>
    private (DataCopy Copy, Team Team) Follower()
    {
        var copy = _data.OpenCopy();
        copy.Load();
        return (copy, new Team(copy));
    }

    private void Write(DocumentKey key, JsonObject content) => Assert.Equal(WriteOutcome.Written, _data.Copy.Create(key, content).Outcome);
}

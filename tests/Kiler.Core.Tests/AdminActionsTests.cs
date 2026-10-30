using System.Text.Json.Nodes;
using Kiler.Crypto;
using Kiler.Storage;

namespace Kiler.Core.Tests;

/// <summary>
/// The admin actions (M4.6): the keys of the admin locked with a temporary password, written through the team, the rename of a
/// level, the marks of the list, and the refusals.
/// </summary>
public sealed class AdminActionsTests : IDisposable
{
    private const string Admin = "demo.admin";
    private const string Other = "demo.other";
    private const string User = "demo.user";

    private readonly TestKiler _kiler = new(top: 3);
    private readonly TestData _data = new();

    public void Dispose()
    {
        _data.Dispose();
        _kiler.Dispose();
    }

    [Fact]
    public void NewUser_GetsTheKeysUpToItsLevel_LockedWithATemporaryPassword_ThatMustBeChanged()
    {
        var team = TeamOf(AdminFile());
        using var admin = SignInWith(team.Files, Admin, TestKiler.Password);

        var result = AdminActions.NewUser(admin, team, @"DOMAIN\Demo.New", "Demo New", level: 2, canEdit: true);

        Assert.Equal(AdminOutcome.Done, result.Outcome);
        var temporary = result.TemporaryPassword;
        Assert.True(temporary is { Length: Password.TemporaryLength }, "No temporary password of 16 characters.");
        var file = team.Files.Users["demo.new"];
        Assert.Equal(1, file.Version);
        var user = Assert.IsType<UserFile>(file.Model);
        Assert.Equal(("Demo New", 2, true, false, true), (user.DisplayName, user.Level, user.CanEdit, user.IsAdmin, user.MustChangePassword));
        Assert.Equal([1, 2], user.Keys.Keys.Keys.Order());
        Assert.Same(user, result.User);

        // On the share too, as the Kiler of the new user reads it.
        var other = _data.OpenCopy();
        other.Load();
        using var session = SignInWith(new Team(other).Files, "demo.new", temporary!);
        Assert.Equal([0, 1, 2], session.Levels);
        Assert.True(session.MustChangePassword);
        Assert.Equal(AccessRefusal.MustChangePassword, Access.Check(Need.Reader, session, team.Files));
    }

    [Fact]
    public void ChangeLevel_GivesTheKeysUpToTheNewLevelOnly_WithANewTemporaryPassword()
    {
        var team = TeamOf(AdminFile(), _kiler.User(User, level: 3));
        using var admin = SignInWith(team.Files, Admin, TestKiler.Password);

        var lowered = AdminActions.ChangeLevel(admin, team, User, 1);

        Assert.Equal(AdminOutcome.Done, lowered.Outcome);
        Assert.Equal(2, team.Files.Users[User].Version);
        Assert.Equal([1], team.Files.Users[User].Model!.Keys.Keys.Keys);
        using (var session = SignInWith(team.Files, User, lowered.TemporaryPassword!))
        {
            Assert.Equal([0, 1], session.Levels);
            Assert.True(session.MustChangePassword);
        }

        Assert.False(SignIn.TryStart(team.Files, User, TestKiler.Password, out _, out var refusal));
        Assert.Equal(SignInRefusal.WrongPassword, refusal);

        var raised = AdminActions.ChangeLevel(admin, team, User, 3);

        Assert.Equal(AdminOutcome.Done, raised.Outcome);
        Assert.True(raised.TemporaryPassword != lowered.TemporaryPassword, "The change of level gave the same temporary password.");
        using var again = SignInWith(team.Files, User, raised.TemporaryPassword!);
        Assert.Equal([0, 1, 2, 3], again.Levels);
    }

    [Fact]
    public void ANewPasswordOfTheSameLevel_KeepsTheRights_AndOnlyTheNewPasswordSignsIn()
    {
        var team = TeamOf(AdminFile(), _kiler.User(User, level: 2, canEdit: true));
        using var admin = SignInWith(team.Files, Admin, TestKiler.Password);

        var result = AdminActions.ChangeLevel(admin, team, User, 2);

        Assert.Equal(AdminOutcome.Done, result.Outcome);
        var user = team.Files.Users[User].Model!;
        Assert.Equal((2, true, false, true), (user.Level, user.CanEdit, user.IsAdmin, user.MustChangePassword));
        using var session = SignInWith(team.Files, User, result.TemporaryPassword!);
        Assert.Equal([0, 1, 2], session.Levels);
        Assert.False(SignIn.TryStart(team.Files, User, TestKiler.Password, out _, out _));
    }

    [Fact]
    public void ANewPassword_GivesTheLevelThatTheAdminGives_NotALevelThatTheUserWroteIntoTheirFile()
    {
        // A user of level 1 who wrote the top level, isAdmin and keys of their own under 2 and 3 into their file (security review).
        using var own = SecretKey.New();
        var forged = _kiler.User(User, level: 3, canEdit: true, isAdmin: true, keyLevels: [1], own: new Dictionary<int, SecretKey> { [2] = own, [3] = own });
        var team = TeamOf(AdminFile(), forged);
        using var admin = SignInWith(team.Files, Admin, TestKiler.Password);
        Assert.True(AdminActions.KeysMatchLevel(forged));

        var result = AdminActions.ChangeLevel(admin, team, User, 1);

        Assert.Equal(AdminOutcome.AdminLevel, result.Outcome);
        Assert.Equal(AdminOutcome.Done, AdminActions.SetRights(admin, team, User, canEdit: true, isAdmin: false).Outcome);
        result = AdminActions.ChangeLevel(admin, team, User, 1);
        Assert.Equal(AdminOutcome.Done, result.Outcome);
        Assert.Equal([1], team.Files.Users[User].Model!.Keys.Keys.Keys);
        using var session = SignInWith(team.Files, User, result.TemporaryPassword!);
        Assert.Equal([0, 1], session.Levels);
    }

    [Theory]
    [InlineData("")]
    [InlineData("demo..user")]
    [InlineData(".demo")]
    [InlineData("demo user")]
    [InlineData("demo/user")]
    [InlineData("nul")]
    [InlineData("демо")]
    public void NewUser_WithABadAccountName_IsRefused(string account)
    {
        var team = TeamOf(AdminFile());
        using var admin = SignInWith(team.Files, Admin, TestKiler.Password);

        var result = AdminActions.NewUser(admin, team, account, "Demo New", level: 1, canEdit: false);

        Assert.Equal((AdminOutcome.Invalid, AdminActions.AccountInvalid), (result.Outcome, result.Invalid));
        Assert.Null(result.TemporaryPassword);
        Assert.Single(team.Files.Users);
    }

    [Fact]
    public void NewUser_WithAnExistingAccount_OrValuesThatDoNotFit_IsRefused_AndWritesNothing()
    {
        var team = TeamOf(AdminFile(), _kiler.User(User, level: 1));
        using var admin = SignInWith(team.Files, Admin, TestKiler.Password);

        Assert.Equal(AdminOutcome.Exists, AdminActions.NewUser(admin, team, User, "Demo User", level: 1, canEdit: false).Outcome);
        Assert.Equal(AdminOutcome.Exists, AdminActions.NewUser(admin, team, "Demo.Admin", "Demo Admin", level: 1, canEdit: false).Outcome);
        Assert.Equal("level is not from 1 to 3.", AdminActions.NewUser(admin, team, "demo.new", "Demo New", level: 0, canEdit: false).Invalid);
        Assert.Equal("level is not from 1 to 3.", AdminActions.NewUser(admin, team, "demo.new", "Demo New", level: 4, canEdit: false).Invalid);
        Assert.Equal(AdminOutcome.Invalid, AdminActions.NewUser(admin, team, "demo.new", " ", level: 1, canEdit: false).Outcome);
        Assert.Equal("account is missing.", AdminActions.NewUser(admin, team, null, "Demo New", level: 1, canEdit: false).Invalid);
        Assert.Equal("displayName is missing.", AdminActions.NewUser(admin, team, "demo.new", null, level: 1, canEdit: false).Invalid);

        Assert.Equal(2, team.Files.Users.Count);
        Assert.Equal(1, team.Files.Users[User].Version);
    }

    [Fact]
    public void AnAccountThatHadAUserFile_IsNotMadeAgain()
    {
        // A user file deleted by hand leaves its history (M2.5), so its version 1 could never be written again.
        var team = TeamOf(AdminFile(), _kiler.User(User, level: 1));
        Assert.Equal(WriteOutcome.Deleted, _data.Copy.Delete(UserFile.KeyOf(User), 1).Outcome);
        team.Reload();
        using var admin = SignInWith(team.Files, Admin, TestKiler.Password);

        Assert.Equal(AdminOutcome.Exists, AdminActions.NewUser(admin, team, User, "Demo User", level: 1, canEdit: false).Outcome);
    }

    [Fact]
    public void Actions_OnTheAdminsOwnAccount_OrAnAccountWithoutAUserFile_AreRefused()
    {
        var team = TeamOf(AdminFile());
        using var admin = SignInWith(team.Files, Admin, TestKiler.Password);

        Assert.Equal(AdminOutcome.OwnAccount, AdminActions.ChangeLevel(admin, team, Admin, 3).Outcome);
        Assert.Equal(AdminOutcome.OwnAccount, AdminActions.SetRights(admin, team, Admin, canEdit: true, isAdmin: false).Outcome);
        Assert.Equal(AdminOutcome.NoUser, AdminActions.ChangeLevel(admin, team, User, 1).Outcome);
        Assert.Equal(AdminOutcome.NoUser, AdminActions.SetRights(admin, team, User, canEdit: true, isAdmin: false).Outcome);

        Assert.Equal(1, team.Files.Users[Admin].Version);
    }

    [Fact]
    public void Actions_OnAUserFileThatDoesNotFit_AreRefused()
    {
        var team = TeamOf(AdminFile());
        Create(UserFile.KeyOf(User), new JsonObject { ["displayName"] = "Demo User" });
        team.Reload();
        using var admin = SignInWith(team.Files, Admin, TestKiler.Password);

        Assert.Equal(AdminOutcome.Unreadable, AdminActions.ChangeLevel(admin, team, User, 1).Outcome);
        Assert.Equal(AdminOutcome.Unreadable, AdminActions.SetRights(admin, team, User, canEdit: true, isAdmin: false).Outcome);
    }

    [Fact]
    public void ChangeLevel_OfAnAdminBelowTheTopLevel_IsRefused()
    {
        var team = TeamOf(AdminFile(), _kiler.User(Other, level: 3, canEdit: true, isAdmin: true));
        using var admin = SignInWith(team.Files, Admin, TestKiler.Password);

        Assert.Equal(AdminOutcome.AdminLevel, AdminActions.ChangeLevel(admin, team, Other, 2).Outcome);
        Assert.Equal(AdminOutcome.Invalid, AdminActions.ChangeLevel(admin, team, Other, 0).Outcome);

        Assert.Equal(1, team.Files.Users[Other].Version);
    }

    [Fact]
    public void SetRights_MakingAnAdmin_GivesTheTopLevel_WithATemporaryPassword()
    {
        var team = TeamOf(AdminFile(), _kiler.User(User, level: 1));
        using var admin = SignInWith(team.Files, Admin, TestKiler.Password);

        var result = AdminActions.SetRights(admin, team, User, canEdit: true, isAdmin: true);

        Assert.Equal(AdminOutcome.Done, result.Outcome);
        var user = team.Files.Users[User].Model!;
        Assert.Equal((3, true, true, true), (user.Level, user.CanEdit, user.IsAdmin, user.MustChangePassword));
        using var session = SignInWith(team.Files, User, result.TemporaryPassword!);
        Assert.Equal([0, 1, 2, 3], session.Levels);
        Assert.False(AdminActions.FewAdmins(team.Files));
    }

    [Fact]
    public void SetRights_OtherChanges_KeepTheKeysAndThePassword_AndRightsAsTheyAreWriteNothing()
    {
        var team = TeamOf(AdminFile(), _kiler.User(Other, level: 3, canEdit: true, isAdmin: true));
        using var admin = SignInWith(team.Files, Admin, TestKiler.Password);

        var same = AdminActions.SetRights(admin, team, Other, canEdit: true, isAdmin: true);
        Assert.Equal(AdminOutcome.Done, same.Outcome);
        Assert.Equal(1, team.Files.Users[Other].Version);

        var result = AdminActions.SetRights(admin, team, Other, canEdit: false, isAdmin: false);

        Assert.Equal(AdminOutcome.Done, result.Outcome);
        Assert.Null(result.TemporaryPassword);
        var user = team.Files.Users[Other].Model!;
        Assert.Equal((2, 3, false, false, false), (team.Files.Users[Other].Version, user.Level, user.CanEdit, user.IsAdmin, user.MustChangePassword));
        using var session = SignInWith(team.Files, Other, TestKiler.Password);
        Assert.Equal([0, 1, 2, 3], session.Levels);
        Assert.True(AdminActions.FewAdmins(team.Files));
    }

    [Fact]
    public void TheList_MarksAFileWhoseKeysDoNotMatchItsLevel_AndWarnsWhileThereIsOneAdmin()
    {
        UserFile[] users =
        [
            AdminFile(),
            _kiler.User(User, level: 2),
            _kiler.User("demo.raised", level: 3, keyLevels: [1, 2]),
            _kiler.User("demo.above", level: 1, keyLevels: [1, 2]),
            _kiler.User("demo.gap", level: 2, keyLevels: [2]),
        ];

        Assert.Equal([true, true, false, false, false], users.Select(AdminActions.KeysMatchLevel));
        Assert.True(AdminActions.FewAdmins(_kiler.Files(false, users)));
        Assert.False(AdminActions.FewAdmins(_kiler.Files(false, [.. users, _kiler.User(Other, level: 3, isAdmin: true)])));
    }

    [Fact]
    public void RenameLevel_KeepsTheChecksAndTheRecoverySet_SoTheKeysStillVerify()
    {
        var team = TeamOf(AdminFile(), _kiler.User(User, level: 2));
        using var admin = SignInWith(team.Files, Admin, TestKiler.Password);
        var before = team.Files.Levels!.Model!;

        Assert.Equal(AdminOutcome.Done, AdminActions.RenameLevel(admin, team, 2, "Staging").Outcome);

        var levels = team.Files.Levels!;
        Assert.Equal(2, levels.Version);
        Assert.Equal(["Guest", "Level 1", "Staging", "Level 3"], levels.Model!.Levels.Select(level => level.Name));
        Assert.Equal(before.Levels.Select(level => level.Check), levels.Model.Levels.Select(level => level.Check));
        Assert.Equal(before.Recovery.Keys, levels.Model.Recovery.Keys);
        Assert.Null(Access.Check(Need.Admin, admin, team.Files));
        using var session = SignInWith(team.Files, User, TestKiler.Password);
        Assert.Equal([0, 1, 2], session.Levels);

        // As another Kiler reads it.
        var other = _data.OpenCopy();
        other.Load();
        Assert.Equal("Staging", new Team(other).Files.Levels!.Model!.Levels[2].Name);
    }

    [Fact]
    public void RenameLevel_WithANameThatDoesNotFit_IsRefused_AndTheSameNameWritesNothing()
    {
        var team = TeamOf(AdminFile());
        using var admin = SignInWith(team.Files, Admin, TestKiler.Password);

        Assert.Equal("Level 3 has the name of a level below it, without regard to case.", AdminActions.RenameLevel(admin, team, 3, "level 1").Invalid);
        Assert.Equal(AdminOutcome.Invalid, AdminActions.RenameLevel(admin, team, 1, " ").Outcome);
        Assert.Equal(AdminOutcome.Invalid, AdminActions.RenameLevel(admin, team, 1, new string('n', LevelsFile.MaxNameLength + 1)).Outcome);
        Assert.Equal(AdminOutcome.Invalid, AdminActions.RenameLevel(admin, team, 4, "Demo").Outcome);
        Assert.Equal(AdminOutcome.Invalid, AdminActions.RenameLevel(admin, team, 1, null).Outcome);
        Assert.Equal(AdminOutcome.Done, AdminActions.RenameLevel(admin, team, 1, "Level 1").Outcome);

        Assert.Equal(1, team.Files.Levels!.Version);
    }

    [Fact]
    public void Actions_WhereLevelsJsonHasACheckOtherThanTheSignIns_AskForANewSignIn_AndWriteNothing()
    {
        var team = TeamOf(AdminFile(), _kiler.User(User, level: 1));
        using var admin = SignInWith(team.Files, Admin, TestKiler.Password);

        // A check of a key of someone else's, planted after the sign-in (the M4.4 security review).
        using var planted = SecretKey.New();
        var levels = team.Files.Levels!.Model!;
        var entries = levels.Levels.Select(entry => entry.Level == 3 ? entry with { Check = LevelCheck.Create(planted, 3) } : entry).ToList();
        var other = _data.OpenCopy();
        other.Load();
        Assert.Equal(WriteOutcome.Written, other.Replace(LevelsFile.Key, 1, new LevelsFile(entries, levels.Recovery).ToContent()).Outcome);
        team.Follow(_data.Copy.Load());

        Assert.Equal(AccessRefusal.LevelsChanged, Access.Check(Need.Admin, admin, team.Files));
        Assert.Equal(AdminOutcome.LevelsChanged, AdminActions.RenameLevel(admin, team, 1, "Team").Outcome);
        Assert.Equal(AdminOutcome.LevelsChanged, AdminActions.ChangeLevel(admin, team, User, 1).Outcome);
        Assert.Equal(AdminOutcome.LevelsChanged, AdminActions.NewUser(admin, team, "demo.new", "Demo New", level: 1, canEdit: false).Outcome);

        Assert.Equal(2, team.Files.Levels!.Version);
        Assert.Equal(1, team.Files.Users[User].Version);
    }

    [Fact]
    public void Actions_WhereTheShareHasANewerFileThanTheTeam_AreRefused_AndWriteNothing()
    {
        var team = TeamOf(AdminFile(), _kiler.User(User, level: 1));
        using var admin = SignInWith(team.Files, Admin, TestKiler.Password);
        var other = _data.OpenCopy();
        other.Load();
        Assert.Equal(WriteOutcome.Written, other.Replace(UserFile.KeyOf(User), 1, _kiler.User(User, level: 2).ToContent()).Outcome);

        // The team has not followed the share yet, so the write meets the version of the other Kiler.
        var result = AdminActions.ChangeLevel(admin, team, User, 1);

        Assert.Equal(AdminOutcome.Changed, result.Outcome);
        Assert.Null(result.TemporaryPassword);
        team.Follow(_data.Copy.Load());
        Assert.Equal(2, team.Files.Users[User].Model!.Level);
    }

    [Fact]
    public void Actions_OfALockedAdmin_AreLocked_AndWriteNothing()
    {
        var team = TeamOf(AdminFile(), _kiler.User(User, level: 1));
        var admin = SignInWith(team.Files, Admin, TestKiler.Password);
        admin.Dispose();

        Assert.Throws<SessionLockedException>(() => AdminActions.NewUser(admin, team, "demo.new", "Demo New", level: 1, canEdit: false));
        Assert.Throws<SessionLockedException>(() => AdminActions.SetRights(admin, team, User, canEdit: true, isAdmin: false));
        Assert.Throws<SessionLockedException>(() => AdminActions.RenameLevel(admin, team, 1, "Team"));

        Assert.Equal(2, team.Files.Users.Count);
        Assert.Equal(1, team.Files.Users[User].Version);
        Assert.Equal(1, team.Files.Levels!.Version);
    }

    private UserFile AdminFile() => _kiler.User(Admin, level: 3, canEdit: true, isAdmin: true);

    /// <summary>A team of a data folder with <c>levels.json</c>, settings and <paramref name="users"/>.</summary>
    private Team TeamOf(params UserFile[] users)
    {
        Create(LevelsFile.Key, _kiler.Levels.ToContent());
        Create(SettingsFile.Key, new SettingsFile(guestMode: false, autoLockMinutes: 30).ToContent());
        foreach (var user in users)
        {
            Create(user.Key, user.ToContent());
        }

        return new Team(_data.Copy);
    }

    private static Session SignInWith(TeamFiles files, string account, string password)
    {
        Assert.True(SignIn.TryStart(files, account, password, out var session, out var refusal), $"The sign-in was refused: {refusal}.");
        return session;
    }

    private void Create(DocumentKey key, JsonObject content) =>
        Assert.Equal(WriteOutcome.Written, _data.Copy.Create(key, content).Outcome);
}

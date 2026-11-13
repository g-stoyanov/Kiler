using System.Text.Json.Nodes;
using Kiler.Crypto;
using Kiler.Storage;

namespace Kiler.Core.Tests;

/// <summary>
/// The admin actions (M4.6): the keys of the admin locked with a temporary password, written through the team, the rename of a
/// level, the marks of the list, and the refusals; and the seal (M4.14): each action seals what it writes with the admin key
/// of the session, gives the public key with a temporary password and the private key to an admin only, and keeps no right
/// of a file that the admin key did not seal.
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

    private string PublicKey => _kiler.AdminKey.PublicKey;

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
        Assert.True(user.IsSealedBy(PublicKey), "The new user file is not sealed.");
        Assert.Null(user.Keys.AdminPrivateKey);
        Assert.Same(user, result.User);

        // On the share too, as the Kiler of the new user reads it, with the public key of the admin key and not its private key.
        var other = _data.OpenCopy();
        other.Load();
        var theirs = new Team(other).Files;
        Assert.True(theirs.Users["demo.new"].Model!.IsSealedBy(PublicKey), "The new user file is not sealed on the share.");
        using var session = SignInWith(theirs, "demo.new", temporary!);
        Assert.Equal([0, 1, 2], session.Levels);
        Assert.Equal(PublicKey, session.AdminPublicKey);
        Assert.False(session.HoldsAdminKey);
        Assert.True(session.MustChangePassword);
        Assert.Equal(AccessRefusal.MustChangePassword, Access.Check(Need.Reader, session, team.Files));
    }

    [Fact]
    public void ChangeLevel_GivesTheKeysUpToTheNewLevelOnly_WithANewTemporaryPassword()
    {
        var team = TeamOf(AdminFile(), SealedUser(User, level: 3));
        using var admin = SignInWith(team.Files, Admin, TestKiler.Password);

        var lowered = AdminActions.ChangeLevel(admin, team, User, 1);

        Assert.Equal(AdminOutcome.Done, lowered.Outcome);
        Assert.Equal(2, team.Files.Users[User].Version);
        Assert.Equal([1], team.Files.Users[User].Model!.Keys.Keys.Keys);
        Assert.True(team.Files.Users[User].Model!.IsSealedBy(PublicKey), "The changed user file is not sealed.");
        using (var session = SignInWith(team.Files, User, lowered.TemporaryPassword!))
        {
            Assert.Equal([0, 1], session.Levels);
            Assert.Equal((PublicKey, false), (session.AdminPublicKey, session.HoldsAdminKey));
            Assert.True(session.MustChangePassword);
        }

        Assert.False(SignIn.TryStart(team.Files, User, TestKiler.Password, out _, out var refusal));
        Assert.Equal(SignInRefusal.WrongPassword, refusal);

        var raised = AdminActions.ChangeLevel(admin, team, User, 3);

        Assert.Equal(AdminOutcome.Done, raised.Outcome);
        Assert.True(raised.TemporaryPassword != lowered.TemporaryPassword, "The change of level gave the same temporary password.");
        using var again = SignInWith(team.Files, User, raised.TemporaryPassword!);
        Assert.Equal([0, 1, 2, 3], again.Levels);
        Assert.False(again.HoldsAdminKey);
    }

    [Fact]
    public void ANewPasswordOfTheSameLevel_KeepsTheSealedRights_AndOnlyTheNewPasswordSignsIn()
    {
        var team = TeamOf(AdminFile(), SealedUser(User, level: 2, canEdit: true));
        using var admin = SignInWith(team.Files, Admin, TestKiler.Password);

        var result = AdminActions.ChangeLevel(admin, team, User, 2);

        Assert.Equal(AdminOutcome.Done, result.Outcome);
        var user = team.Files.Users[User].Model!;
        Assert.Equal((2, true, false, true), (user.Level, user.CanEdit, user.IsAdmin, user.MustChangePassword));
        Assert.True(user.IsSealedBy(PublicKey), "The user file is not sealed.");
        using var session = SignInWith(team.Files, User, result.TemporaryPassword!);
        Assert.Equal([0, 1, 2], session.Levels);
        Assert.False(SignIn.TryStart(team.Files, User, TestKiler.Password, out _, out _));
    }

    [Fact]
    public void ANewPassword_GivesTheLevelThatTheAdminGives_AndNoRightThatTheUserWroteIntoTheirFile()
    {
        // A user of level 1 who wrote the top level, isAdmin, canEdit and keys of their own under 2 and 3 into their file
        // (security review), and sealed it with a key pair of their own (docs/tasks/M3.md, "The accepted limits of the seal", 3).
        using var own = SecretKey.New();
        using var theirs = AdminKey.New();
        var forged = _kiler.User(User, level: 3, canEdit: true, isAdmin: true, keyLevels: [1], own: new Dictionary<int, SecretKey> { [2] = own, [3] = own }, adminKey: AdminKeyLock.PublicKey)
            .Sealed(theirs);
        var team = TeamOf(AdminFile(), forged);
        using var admin = SignInWith(team.Files, Admin, TestKiler.Password);
        Assert.True(AdminActions.KeysMatchLevel(forged));

        // A change of the rights would keep the level and the keys that the user wrote, under the admin key; and isAdmin, as the
        // list shows it, makes no admin.
        Assert.Equal(AdminOutcome.UserNotSealed, SetRights(admin, team, User, canEdit: true, isAdmin: false).Outcome);
        Assert.Equal(AdminOutcome.UserNotSealed, SetRights(admin, team, User, canEdit: false, isAdmin: false).Outcome);
        Assert.Equal(AdminOutcome.UserNotSealed, SetRights(admin, team, User, canEdit: false, isAdmin: true).Outcome);
        Assert.Equal(1, team.Files.Users[User].Version);

        // The forged isAdmin neither keeps the level up nor gets the private key: the user becomes a reader of level 1.
        var result = AdminActions.ChangeLevel(admin, team, User, 1);

        Assert.Equal(AdminOutcome.Done, result.Outcome);
        var user = team.Files.Users[User].Model!;
        Assert.Equal((1, false, false, true), (user.Level, user.CanEdit, user.IsAdmin, user.MustChangePassword));
        Assert.Equal([1], user.Keys.Keys.Keys);
        Assert.Null(user.Keys.AdminPrivateKey);
        Assert.True(user.IsSealedBy(PublicKey), "The user file is not sealed by the admin key.");
        using (var session = SignInWith(team.Files, User, result.TemporaryPassword!))
        {
            Assert.Equal([0, 1], session.Levels);
            Assert.False(session.HoldsAdminKey);
        }

        // Sealed now, so its rights change as those of any user.
        Assert.Equal(AdminOutcome.Done, SetRights(admin, team, User, canEdit: true, isAdmin: false).Outcome);
        Assert.True(team.Files.Users[User].Model!.CanEdit);
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
        Assert.Equal(AdminOutcome.OwnAccount, SetRights(admin, team, Admin, canEdit: true, isAdmin: false).Outcome);
        Assert.Equal(AdminOutcome.NoUser, AdminActions.ChangeLevel(admin, team, User, 1).Outcome);
        Assert.Equal(AdminOutcome.NoUser, SetRights(admin, team, User, canEdit: true, isAdmin: false).Outcome);

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
        Assert.Equal(AdminOutcome.Unreadable, SetRights(admin, team, User, canEdit: true, isAdmin: false).Outcome);
    }

    [Fact]
    public void ChangeLevel_OfAnAdmin_IsRefusedBelowTheTopLevel_AndGivesThePrivateKeyAtIt()
    {
        var team = TeamOf(AdminFile(), SealedUser(Other, level: 3, canEdit: true, isAdmin: true));
        using var admin = SignInWith(team.Files, Admin, TestKiler.Password);

        Assert.Equal(AdminOutcome.AdminLevel, AdminActions.ChangeLevel(admin, team, Other, 2).Outcome);
        Assert.Equal(AdminOutcome.Invalid, AdminActions.ChangeLevel(admin, team, Other, 0).Outcome);
        Assert.Equal(1, team.Files.Users[Other].Version);

        // A forgotten password of an admin.
        var result = AdminActions.ChangeLevel(admin, team, Other, 3);

        Assert.Equal(AdminOutcome.Done, result.Outcome);
        var user = team.Files.Users[Other].Model!;
        Assert.Equal((3, true, true, true), (user.Level, user.CanEdit, user.IsAdmin, user.MustChangePassword));
        Assert.True(user.IsSealedBy(PublicKey), "The admin's user file is not sealed.");
        using var session = SignInWith(team.Files, Other, result.TemporaryPassword!);
        Assert.True(session.HoldsAdminKey, "The temporary password of an admin does not unlock the admin key.");
    }

    [Fact]
    public void SetRights_MakingAnAdmin_GivesTheTopLevel_AndTheAdminKey_WithATemporaryPassword()
    {
        var team = TeamOf(AdminFile(), SealedUser(User, level: 1));
        using var admin = SignInWith(team.Files, Admin, TestKiler.Password);
        Assert.True(AdminActions.FewAdmins(team.Files, PublicKey));

        var result = SetRights(admin, team, User, canEdit: true, isAdmin: true);

        Assert.Equal(AdminOutcome.Done, result.Outcome);
        var user = team.Files.Users[User].Model!;
        Assert.Equal((3, true, true, true), (user.Level, user.CanEdit, user.IsAdmin, user.MustChangePassword));
        Assert.True(user.IsSealedBy(PublicKey), "The new admin's user file is not sealed.");
        using var session = SignInWith(team.Files, User, result.TemporaryPassword!);
        Assert.Equal([0, 1, 2, 3], session.Levels);
        Assert.Equal(PublicKey, session.AdminPublicKey);
        Assert.True(session.HoldsAdminKey, "The new admin's temporary password does not unlock the admin key.");
        Assert.False(AdminActions.FewAdmins(team.Files, PublicKey));
    }

    [Fact]
    public void SetRights_OfAFileThatSaysIsAdminWithoutASeal_IsRefused_EvenWithIsAdmin_UntilANewTemporaryPasswordSealsIt()
    {
        // A reader who wrote isAdmin and canEdit into their own file. The page sends the rights whole, so a change of the editor
        // right sends isAdmin as the list shows it: that is no choice of the admin to make an admin (M4.14 security review).
        var team = TeamOf(AdminFile(), _kiler.User(Other, level: 1, canEdit: true, isAdmin: true, adminKey: AdminKeyLock.PublicKey));
        using var admin = SignInWith(team.Files, Admin, TestKiler.Password);

        var result = SetRights(admin, team, Other, canEdit: false, isAdmin: true);

        Assert.Equal(AdminOutcome.UserNotSealed, result.Outcome);
        Assert.Null(result.TemporaryPassword);
        Assert.Equal(AdminOutcome.UserNotSealed, SetRights(admin, team, Other, canEdit: true, isAdmin: true).Outcome);
        Assert.Equal(1, team.Files.Users[Other].Version);

        // A new temporary password makes them a sealed reader; then the admin right is a choice that the admin makes.
        Assert.Equal(AdminOutcome.Done, AdminActions.ChangeLevel(admin, team, Other, 1).Outcome);
        Assert.False(team.Files.Users[Other].Model!.IsAdmin);
        var raised = SetRights(admin, team, Other, canEdit: false, isAdmin: true);

        Assert.Equal(AdminOutcome.Done, raised.Outcome);
        var user = team.Files.Users[Other].Model!;
        Assert.Equal((3, false, true, true), (user.Level, user.CanEdit, user.IsAdmin, user.MustChangePassword));
        Assert.True(user.IsSealedBy(PublicKey), "The new admin's user file is not sealed.");
        using var session = SignInWith(team.Files, Other, raised.TemporaryPassword!);
        Assert.True(session.HoldsAdminKey, "The new admin's temporary password does not unlock the admin key.");
    }

    [Fact]
    public void SetRights_ChosenOnAListThatIsBehindTheFile_IsAConflict_AndWritesNothing()
    {
        // The list showed a reader with the isAdmin that they wrote into their file; before the admin's click they wrote their
        // sealed file back, and the click on the editor right sends isAdmin as the list showed it (M4.14 security review, pass 2).
        var team = TeamOf(AdminFile(), SealedUser(User, level: 1), SealedUser(Other, level: 3, canEdit: true));
        using var admin = SignInWith(team.Files, Admin, TestKiler.Password);

        var result = AdminActions.SetRights(admin, team, User, canEdit: true, isAdmin: true, shownCanEdit: false, shownIsAdmin: true);

        Assert.Equal(AdminOutcome.Changed, result.Outcome);
        Assert.Null(result.TemporaryPassword);
        Assert.Equal(1, team.Files.Users[User].Version);

        // Another admin took the admin right away after the list was read: a change of the editor right does not give it back.
        Assert.Equal(AdminOutcome.Changed, AdminActions.SetRights(admin, team, Other, canEdit: false, isAdmin: true, shownCanEdit: true, shownIsAdmin: true).Outcome);
        Assert.Equal(AdminOutcome.Changed, AdminActions.SetRights(admin, team, Other, canEdit: true, isAdmin: true, shownCanEdit: false, shownIsAdmin: false).Outcome);
        Assert.Equal(1, team.Files.Users[Other].Version);

        // The list read again shows the reader, and the click gives the editor right only.
        var editor = AdminActions.SetRights(admin, team, User, canEdit: true, isAdmin: false, shownCanEdit: false, shownIsAdmin: false);

        Assert.Equal(AdminOutcome.Done, editor.Outcome);
        Assert.Null(editor.TemporaryPassword);
        var user = team.Files.Users[User].Model!;
        Assert.Equal((1, true, false), (user.Level, user.CanEdit, user.IsAdmin));
    }

    [Fact]
    public void SetRights_MakingAnAdmin_OfAFileThatIsNotSealed_ButDoesNotSayIsAdmin_SetsEveryValue()
    {
        // A level and canEdit written by the user, not sealed: making them an admin is the admin's choice, and sets every value.
        var team = TeamOf(AdminFile(), _kiler.User(Other, level: 2, canEdit: true, keyLevels: [1], adminKey: AdminKeyLock.PublicKey));
        using var admin = SignInWith(team.Files, Admin, TestKiler.Password);

        var result = SetRights(admin, team, Other, canEdit: false, isAdmin: true);

        Assert.Equal(AdminOutcome.Done, result.Outcome);
        var user = team.Files.Users[Other].Model!;
        Assert.Equal((3, false, true, true), (user.Level, user.CanEdit, user.IsAdmin, user.MustChangePassword));
        Assert.True(user.IsSealedBy(PublicKey), "The new admin's user file is not sealed.");
        using var session = SignInWith(team.Files, Other, result.TemporaryPassword!);
        Assert.Equal([0, 1, 2, 3], session.Levels);
        Assert.True(session.HoldsAdminKey, "The new admin's temporary password does not unlock the admin key.");
    }

    [Fact]
    public void SetRights_OtherChanges_KeepTheKeysAndThePassword_TakingTheAdminRightDropsThePrivateKey_AndRightsAsTheyAreWriteNothing()
    {
        var team = TeamOf(AdminFile(), SealedUser(Other, level: 3, canEdit: true, isAdmin: true));
        using var admin = SignInWith(team.Files, Admin, TestKiler.Password);

        var same = SetRights(admin, team, Other, canEdit: true, isAdmin: true);
        Assert.Equal(AdminOutcome.Done, same.Outcome);
        Assert.Equal(1, team.Files.Users[Other].Version);
        Assert.False(AdminActions.FewAdmins(team.Files, PublicKey));

        var result = SetRights(admin, team, Other, canEdit: false, isAdmin: false);

        Assert.Equal(AdminOutcome.Done, result.Outcome);
        Assert.Null(result.TemporaryPassword);
        var user = team.Files.Users[Other].Model!;
        Assert.Equal((2, 3, false, false, false), (team.Files.Users[Other].Version, user.Level, user.CanEdit, user.IsAdmin, user.MustChangePassword));
        Assert.True(user.IsSealedBy(PublicKey), "The former admin's user file is not sealed.");
        Assert.Null(user.Keys.AdminPrivateKey);
        Assert.NotNull(user.Keys.AdminPublicKey);
        using (var session = SignInWith(team.Files, Other, TestKiler.Password))
        {
            Assert.Equal([0, 1, 2, 3], session.Levels);
            Assert.Equal((PublicKey, false), (session.AdminPublicKey, session.HoldsAdminKey));
        }

        Assert.True(AdminActions.FewAdmins(team.Files, PublicKey));

        // Back as an editor: the keys and the password stay, and the seal follows the rights.
        Assert.Equal(AdminOutcome.Done, SetRights(admin, team, Other, canEdit: true, isAdmin: false).Outcome);
        user = team.Files.Users[Other].Model!;
        Assert.True(user.CanEdit && user.IsSealedBy(PublicKey), "The editor's user file is not sealed with its rights.");
        using var editor = SignInWith(team.Files, Other, TestKiler.Password);
        Assert.Equal([0, 1, 2, 3], editor.Levels);
    }

    [Fact]
    public void TheList_MarksAFileWhoseKeysDoNotMatchItsLevel()
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
    }

    [Fact]
    public void TheList_WarnsWhileThereIsOneSealedAdmin_AndAForgedIsAdminDoesNotCount()
    {
        using var theirs = AdminKey.New();
        var adminFile = AdminFile();
        var files = new Dictionary<string, TeamFiles>
        {
            ["one"] = _kiler.Files(false, adminFile, SealedUser(User, level: 2)),
            ["forged"] = _kiler.Files(false, adminFile, _kiler.User(Other, level: 3, isAdmin: true)),
            ["their seal"] = _kiler.Files(false, adminFile, _kiler.User(Other, level: 3, isAdmin: true).Sealed(theirs)),
            ["two"] = _kiler.Files(false, adminFile, SealedUser(Other, level: 3, isAdmin: true)),
        };

        Assert.True(AdminActions.FewAdmins(files["one"], PublicKey));
        Assert.True(AdminActions.FewAdmins(files["forged"], PublicKey));
        Assert.True(AdminActions.FewAdmins(files["their seal"], PublicKey));
        Assert.False(AdminActions.FewAdmins(files["two"], PublicKey));

        // A session without the public key verifies no seal.
        Assert.True(AdminActions.FewAdmins(files["two"], null));
    }

    [Fact]
    public void RenameLevel_KeepsTheChecksAndTheRecoverySet_SoTheKeysStillVerify_AndSealsTheNextVersion()
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
        Assert.True(levels.Model.IsSealedBy(PublicKey), "The renamed levels.json is not sealed for its version.");
        Assert.Null(Access.Check(Need.Admin, admin, team.Files));
        using var session = SignInWith(team.Files, User, TestKiler.Password);
        Assert.Equal([0, 1, 2], session.Levels);

        // As another Kiler reads it.
        var other = _data.OpenCopy();
        other.Load();
        var theirs = new Team(other).Files.Levels!.Model!;
        Assert.Equal("Staging", theirs.Levels[2].Name);
        Assert.True(theirs.IsSealedBy(PublicKey), "The renamed levels.json is not sealed on the share.");

        // The team took it as read at its version, so the next rename seals the one after.
        Assert.Equal(AdminOutcome.Done, AdminActions.RenameLevel(admin, team, 1, "Team").Outcome);
        Assert.Equal(3, team.Files.Levels!.Version);
        Assert.True(team.Files.Levels.Model!.IsSealedBy(PublicKey), "The second rename is not sealed.");
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
    public void RenameLevel_OfALevelsJsonThatTheAdminKeyDidNotSeal_IsRefused_AndWritesNothing()
    {
        var team = TeamOf(AdminFile(), _kiler.User(User, level: 1));
        using var admin = SignInWith(team.Files, Admin, TestKiler.Password);

        // The top level cut away after the sign-in, with the other checks as they were: the admin signs in again, and the sign-in
        // refuses it (M4.15, the M4.6 finding on Session.HoldsEvery).
        var levels = team.Files.Levels!.Model!;
        var other = _data.OpenCopy();
        other.Load();
        Assert.Equal(WriteOutcome.Written, other.Replace(LevelsFile.Key, 1, new LevelsFile([.. levels.Levels.Take(3)], levels.Recovery).ToContent()).Outcome);
        team.Follow(_data.Copy.Load());
        Assert.False(admin.HoldsEvery(team.Files.Levels!.Model!));

        Assert.Equal(AdminOutcome.LevelsChanged, AdminActions.RenameLevel(admin, team, 1, "Team").Outcome);
        Assert.Equal(2, team.Files.Levels!.Version);

        // Every level and check as they were, but not sealed: the rename does not seal it under the admin's name.
        Assert.Equal(WriteOutcome.Written, other.Replace(LevelsFile.Key, 2, new LevelsFile(levels.Levels, levels.Recovery).ToContent()).Outcome);
        team.Follow(_data.Copy.Load());
        Assert.True(admin.HoldsEvery(team.Files.Levels!.Model!));

        Assert.Equal(AdminOutcome.LevelsNotSealed, AdminActions.RenameLevel(admin, team, 1, "Team").Outcome);
        Assert.Equal(3, team.Files.Levels!.Version);

        // A seal by a key of someone's own is no better.
        using var theirs = AdminKey.New();
        Assert.Equal(WriteOutcome.Written, other.Replace(LevelsFile.Key, 3, levels.Sealed(theirs, 4).ToContent()).Outcome);
        team.Follow(_data.Copy.Load());

        Assert.Equal(AdminOutcome.LevelsNotSealed, AdminActions.RenameLevel(admin, team, 1, "Team").Outcome);
        Assert.Equal(4, team.Files.Levels!.Version);
    }

    [Fact]
    public void Actions_OfASessionWithoutTheAdminKey_AreRefused_AndWriteNothing()
    {
        // isAdmin and every level key, but no private key of the admin key to seal with: Need.Admin refuses it first (M4.15),
        // and the actions refuse it too. A file without the public key does not sign in at all (M4.15, SignInTests).
        var team = TeamOf(_kiler.User(Admin, level: 3, canEdit: true, isAdmin: true, adminKey: AdminKeyLock.PublicKey, seal: true), SealedUser(User, level: 1));
        using var admin = SignInWith(team.Files, Admin, TestKiler.Password);
        Assert.False(admin.HoldsAdminKey);
        Assert.Equal(AccessRefusal.NotAdmin, Access.Check(Need.Admin, admin, team.Files));

        Assert.Equal(AdminOutcome.NoAdminKey, AdminActions.NewUser(admin, team, "demo.new", "Demo New", level: 1, canEdit: false).Outcome);
        Assert.Equal(AdminOutcome.NoAdminKey, AdminActions.ChangeLevel(admin, team, User, 2).Outcome);
        Assert.Equal(AdminOutcome.NoAdminKey, SetRights(admin, team, User, canEdit: true, isAdmin: false).Outcome);
        Assert.Equal(AdminOutcome.NoAdminKey, SetRights(admin, team, User, canEdit: true, isAdmin: true).Outcome);
        Assert.Equal(AdminOutcome.NoAdminKey, AdminActions.RenameLevel(admin, team, 1, "Team").Outcome);

        Assert.Equal(2, team.Files.Users.Count);
        Assert.Equal(1, team.Files.Users[User].Version);
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
        Assert.Throws<SessionLockedException>(() => SetRights(admin, team, User, canEdit: true, isAdmin: false));
        Assert.Throws<SessionLockedException>(() => AdminActions.RenameLevel(admin, team, 1, "Team"));

        Assert.Equal(2, team.Files.Users.Count);
        Assert.Equal(1, team.Files.Users[User].Version);
        Assert.Equal(1, team.Files.Levels!.Version);
    }

    /// <summary>The admin's user file, as the wizard makes it: the admin key with its private key, and sealed.</summary>
    private UserFile AdminFile() => SealedUser(Admin, level: 3, canEdit: true, isAdmin: true);

    /// <summary>A user file as an admin action makes it: the public key, the private key for an admin, and sealed.</summary>
    private UserFile SealedUser(string account, int level, bool canEdit = false, bool isAdmin = false) =>
        _kiler.User(account, level, canEdit, isAdmin, adminKey: isAdmin ? AdminKeyLock.PublicAndPrivateKey : AdminKeyLock.PublicKey, seal: true);

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

    /// <summary>A change of the rights chosen on a list read now: the rights that it showed are those of the file (M4.14).</summary>
    private static AdminResult SetRights(Session admin, Team team, string account, bool canEdit, bool isAdmin)
    {
        var shown = team.Files.Users.TryGetValue(account, out var file) ? file.Model : null;
        return AdminActions.SetRights(admin, team, account, canEdit, isAdmin, shown?.CanEdit ?? false, shown?.IsAdmin ?? false);
    }

    private static Session SignInWith(TeamFiles files, string account, string password)
    {
        Assert.True(SignIn.TryStart(files, account, password, out var session, out var refusal), $"The sign-in was refused: {refusal}.");
        return session;
    }

    private void Create(DocumentKey key, JsonObject content) =>
        Assert.Equal(WriteOutcome.Written, _data.Copy.Create(key, content).Outcome);
}

using System.Text;
using System.Text.Json.Nodes;
using Kiler.Crypto;
using Kiler.Storage;

namespace Kiler.Core.Tests;

/// <summary>
/// The change of password (M4.5a): the keys of the session locked again with the new password, into the user file through the
/// copy, the forced change after a temporary password and the voluntary one with the current password, and its refusals.
/// </summary>
public sealed class PasswordChangeTests : IDisposable
{
    private const string Account = "demo.user";

    /// <summary>Made up, and compared only with <c>Assert.True</c> (SKILL.md, "Tests").</summary>
    private const string NewPassword = "demo-password-2";

    private readonly TestKiler _kiler = new();
    private readonly TestData _data = new();

    public void Dispose()
    {
        _data.Dispose();
        _kiler.Dispose();
    }

    [Fact]
    public void Run_AfterATemporaryPassword_ChangesItWithoutTheCurrentOne_AndGivesTheSessionItsRights()
    {
        var team = TeamOf(_kiler.User(Account, level: 2, mustChangePassword: true));
        using var session = SignInWith(team, TestKiler.Password);
        Assert.Equal(AccessRefusal.MustChangePassword, Access.Check(Need.Reader, session, team.Files));

        Assert.Equal(PasswordChangeOutcome.Changed, PasswordChange.Run(session, team, null, NewPassword));

        Assert.False(session.MustChangePassword);
        Assert.Equal(2, session.UserVersion);
        Assert.Null(Access.Check(Need.Reader, session, team.Files));
        var file = team.Files.Users[Account];
        Assert.Equal(2, file.Version);
        Assert.False(file.Model?.MustChangePassword);
        AssertSignsIn(team.Files, NewPassword);
        Assert.False(SignIn.TryStart(team.Files, Account, TestKiler.Password, out _, out var refusal));
        Assert.Equal(SignInRefusal.WrongPassword, refusal);

        // On the share too, as another Kiler reads it.
        var other = _data.OpenCopy();
        other.Load();
        var shared = new Team(other).Files;
        Assert.Equal(2, shared.Users[Account].Version);
        AssertSignsIn(shared, NewPassword);
    }

    [Fact]
    public void Run_Voluntary_WithTheCurrentPassword_ChangesIt()
    {
        var team = TeamOf(_kiler.User(Account, level: 2));
        using var session = SignInWith(team, TestKiler.Password);

        Assert.Equal(PasswordChangeOutcome.Changed, PasswordChange.Run(session, team, TestKiler.Password, NewPassword));

        Assert.Equal(2, team.Files.Users[Account].Version);
        AssertSignsIn(team.Files, NewPassword);
    }

    [Fact]
    public void Run_Voluntary_WithAWrongCurrentPassword_IsRefused_AndChangesNothing()
    {
        var team = TeamOf(_kiler.User(Account, level: 2));
        using var session = SignInWith(team, TestKiler.Password);

        Assert.Equal(PasswordChangeOutcome.WrongPassword, PasswordChange.Run(session, team, "demo-password-3", NewPassword));
        Assert.Equal(PasswordChangeOutcome.WrongPassword, PasswordChange.Run(session, team, null, NewPassword));

        Assert.Equal(1, team.Files.Users[Account].Version);
        AssertSignsIn(team.Files, TestKiler.Password);
    }

    [Fact]
    public void Run_KeepsTheKeys_SoAGroupMadeBeforeReadsAfter()
    {
        var team = TeamOf(_kiler.User(Account, level: 2));
        var fields = Encoding.UTF8.GetBytes("""{"host":"demo-host"}""");
        string group;
        using (var before = SignInWith(team, TestKiler.Password))
        {
            group = before.Use(2, key => FieldGroup.Encrypt(key, fields, "a1", 2));
            Assert.Equal(PasswordChangeOutcome.Changed, PasswordChange.Run(before, team, TestKiler.Password, NewPassword));
        }

        using var after = SignInWith(team, NewPassword);

        Assert.Equal(fields, after.Use(2, key => FieldGroup.Decrypt(key, group, "a1", 2)));
    }

    [Fact]
    public void Run_WhereALevelOfTheFileDidNotVerify_IsRefused_AsTheLevelWouldDropOut()
    {
        // The key of level 2 unlocks with the password, but it is not the key of the level.
        using var own = SecretKey.New();
        var team = TeamOf(_kiler.User(Account, level: 2, keyLevels: [1], own: new Dictionary<int, SecretKey> { [2] = own }));
        using var session = SignInWith(team, TestKiler.Password);
        Assert.Equal([0, 1], session.Levels);

        Assert.Equal(PasswordChangeOutcome.LevelNotUnlocked, PasswordChange.Run(session, team, TestKiler.Password, NewPassword));

        Assert.Equal(1, team.Files.Users[Account].Version);
    }

    [Fact]
    public void Run_WhereTheTeamHasTheUserFileAtAnotherVersionThanTheSignIn_IsRefused()
    {
        var team = TeamOf(_kiler.User(Account, level: 2, mustChangePassword: true));
        using var session = SignInWith(team, TestKiler.Password);

        // As an admin who raises the level with a new temporary password: the file has a key that the session does not hold.
        ReplaceElsewhere(_kiler.User(Account, level: 3, mustChangePassword: true));
        team.Follow(_data.Copy.Load());

        Assert.Equal(PasswordChangeOutcome.UserChanged, PasswordChange.Run(session, team, null, NewPassword));

        Assert.Equal(2, team.Files.Users[Account].Version);
        Assert.True(session.MustChangePassword);
    }

    [Fact]
    public void Run_WhereTheShareHasANewerUserFileThanTheTeam_IsRefused_AndWritesNothing()
    {
        var team = TeamOf(_kiler.User(Account, level: 2, mustChangePassword: true));
        using var session = SignInWith(team, TestKiler.Password);
        ReplaceElsewhere(_kiler.User(Account, level: 1, mustChangePassword: true));

        // The team has not followed the share yet, so the write meets the version of the other Kiler.
        Assert.Equal(PasswordChangeOutcome.UserChanged, PasswordChange.Run(session, team, null, NewPassword));

        team.Follow(_data.Copy.Load());
        Assert.Equal(1, team.Files.Users[Account].Model?.Level);
        Assert.Equal(2, team.Files.Users[Account].Version);
        Assert.True(session.MustChangePassword);
    }

    [Theory]
    [InlineData(7, 8)]
    [InlineData(1025, 8)]
    [InlineData(8, 7)]
    [InlineData(8, 1025)]
    public void Run_WithAPasswordThatDoesNotFit_Throws(int newLength, int currentLength)
    {
        // The API answers 400 before; the check comes before the user file and any key.
        using var session = SessionTests.NewSession(top: 1);

        Assert.Throws<ArgumentException>(() => PasswordChange.Run(session, new Team(_data.Copy), new string('c', currentLength), new string('n', newLength)));
    }

    [Fact]
    public void Run_OfALockedSession_IsLocked_AndWritesNothing()
    {
        var team = TeamOf(_kiler.User(Account, level: 1, mustChangePassword: true));
        var session = SignInWith(team, TestKiler.Password);
        session.Dispose();

        Assert.Throws<SessionLockedException>(() => PasswordChange.Run(session, team, null, NewPassword));

        Assert.Equal(1, team.Files.Users[Account].Version);
    }

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

    private static Session SignInWith(Team team, string password)
    {
        Assert.True(SignIn.TryStart(team.Files, Account, password, out var session, out var refusal), $"The sign-in was refused: {refusal}.");
        return session;
    }

    private static void AssertSignsIn(TeamFiles files, string password)
    {
        Assert.True(SignIn.TryStart(files, Account, password, out var session, out var refusal), $"The sign-in was refused: {refusal}.");
        session.Dispose();
    }

    /// <summary>Replaces the user file at version 1 through the copy of another Kiler, as an admin does.</summary>
    private void ReplaceElsewhere(UserFile user)
    {
        var other = _data.OpenCopy();
        other.Load();
        Assert.Equal(WriteOutcome.Written, other.Replace(user.Key, 1, user.ToContent()).Outcome);
    }

    private void Create(DocumentKey key, JsonObject content) =>
        Assert.Equal(WriteOutcome.Written, _data.Copy.Create(key, content).Outcome);
}

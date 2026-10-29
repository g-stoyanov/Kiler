using System.Text.Json.Nodes;
using Kiler.Crypto;

namespace Kiler.Core.Tests;

/// <summary>The sign-in (M4.4): only the keys of the user file that unlock with the password and verify against <c>levels.json</c>.</summary>
public sealed class SignInTests : IDisposable
{
    private const string Account = "demo.user";

    private readonly TestKiler _kiler = new();

    public void Dispose() => _kiler.Dispose();

    [Fact]
    public void TryStart_WithTheRightPassword_GivesASession_WithTheKeysAndTheRightsOfTheFile()
    {
        var files = _kiler.Files(guestMode: false, _kiler.User(Account, level: 2, canEdit: true));

        Assert.True(SignIn.TryStart(files, Account, TestKiler.Password, out var session, out _));

        using (session)
        {
            Assert.Equal(Account, session.Account);
            Assert.Equal($"Demo {Account}", session.DisplayName);
            Assert.Equal([0, 1, 2], session.Levels);
            Assert.True(session.CanEdit);
            Assert.False(session.IsAdmin);
            Assert.False(session.MustChangePassword);
            Assert.False(session.Reads(3));
            Assert.True(session.Use(2, key => LevelCheck.Verify(key, _kiler.Check(2), 2)), "The key of level 2 is not the key of the level.");
        }
    }

    [Fact]
    public void TryStart_WithAWrongPassword_GivesNoSession()
    {
        var files = _kiler.Files(guestMode: false, _kiler.User(Account, level: 2));

        Assert.False(SignIn.TryStart(files, Account, "demo-password-2", out var session, out var refusal));

        Assert.Null(session);
        Assert.Equal(SignInRefusal.WrongPassword, refusal);
    }

    [Fact]
    public void TryStart_ForAnAccountWithoutAUserFile_GivesNoSession()
    {
        var files = _kiler.Files(guestMode: false, _kiler.User("other.user", level: 1));

        Assert.False(SignIn.TryStart(files, Account, TestKiler.Password, out _, out var refusal));

        Assert.Equal(SignInRefusal.NoUser, refusal);
    }

    [Fact]
    public void TryStart_ForAUserFileThatDoesNotFit_GivesNoSession()
    {
        var files = TestKiler.Files(_kiler.Levels.ToContent(), null, [(UserFile.KeyOf(Account), new JsonObject { ["displayName"] = "Demo User" })]);

        Assert.False(SignIn.TryStart(files, Account, TestKiler.Password, out _, out var refusal));

        Assert.Equal(SignInRefusal.UserInvalid, refusal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TryStart_WithoutLevelsJsonThatFits_GivesNoSession(bool damaged)
    {
        var user = _kiler.User(Account, level: 1);
        var levels = damaged ? new JsonObject { ["levels"] = "Guest" } : null;
        var files = TestKiler.Files(levels, null, [(user.Key, user.ToContent())]);

        Assert.False(SignIn.TryStart(files, Account, TestKiler.Password, out _, out var refusal));

        Assert.Equal(SignInRefusal.LevelsInvalid, refusal);
    }

    [Fact]
    public void TryStart_ForAUserWhoAddedThemselvesWithoutKeys_GivesNoAccess()
    {
        var self = new UserFile(Account, "Demo User", level: 3, canEdit: true, isAdmin: true, mustChangePassword: false, new LockedKeys("salt", new Dictionary<int, string>()));
        var files = _kiler.Files(guestMode: false, self);

        Assert.False(SignIn.TryStart(files, Account, TestKiler.Password, out _, out var refusal));

        Assert.Equal(SignInRefusal.NoAccess, refusal);
    }

    [Fact]
    public void TryStart_ForAUserWhoAddedThemselvesWithKeysOfTheirOwn_GivesNoAccess()
    {
        // Keys that unlock with their password, but are not the keys of the levels.
        using var one = SecretKey.New();
        using var two = SecretKey.New();
        var keys = LevelKeys.Lock(new Dictionary<int, SecretKey> { [1] = one, [2] = two }, TestKiler.Password);
        var self = new UserFile(Account, "Demo User", level: 2, canEdit: true, isAdmin: true, mustChangePassword: false, keys);
        var files = _kiler.Files(guestMode: false, self);

        Assert.False(SignIn.TryStart(files, Account, TestKiler.Password, out _, out var refusal));

        Assert.Equal(SignInRefusal.NoAccess, refusal);
    }

    [Fact]
    public void TryStart_ForAUserWhoRaisedTheirLevelAndIsAdmin_ReadsOnlyTheLevelsOfTheirKeys_AndIsNoAdmin()
    {
        // Their own file, with the top level, isAdmin, and a key of their own for the top level beside the real key of level
        // 1, all locked with their password: the key of their own unlocks, and its check refuses it.
        using var own = SecretKey.New();
        var raised = _kiler.User(Account, level: 3, canEdit: true, isAdmin: true, keyLevels: [1], own: new Dictionary<int, SecretKey> { [3] = own });
        var files = _kiler.Files(guestMode: false, raised);

        Assert.True(SignIn.TryStart(files, Account, TestKiler.Password, out var session, out _));

        using (session)
        {
            Assert.Equal([0, 1], session.Levels);
            Assert.True(session.IsAdmin, "The session does not show the screens of the file.");
            Assert.Equal(AccessRefusal.NotAdmin, Access.Check(Need.Admin, session, files));
        }
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("of another level")]
    [InlineData("damaged")]
    public void TryStart_WhereTheCheckOfALevelIsMissingOrDamaged_LeavesThatLevelOut(string check)
    {
        var entries = _kiler.Levels.Levels.ToList();
        entries[2] = entries[2] with
        {
            Check = check switch
            {
                "missing" => null,
                "of another level" => _kiler.Check(1),
                _ => "not a check",
            },
        };
        var user = _kiler.User(Account, level: 3);
        var files = TestKiler.Files(new LevelsFile(entries, _kiler.Levels.Recovery).ToContent(), null, [(user.Key, user.ToContent())]);

        Assert.True(SignIn.TryStart(files, Account, TestKiler.Password, out var session, out _));

        using (session)
        {
            Assert.Equal([0, 1, 3], session.Levels);
        }
    }

    [Fact]
    public void TryStart_WithAKeyAboveTheTopLevel_LeavesItOut()
    {
        // levels.json has the levels 0 to 2 only, and the user file a key of level 3.
        var user = _kiler.User(Account, level: 3);
        var levels = new LevelsFile(_kiler.Levels.Levels.Take(3).ToList(), _kiler.Levels.Recovery);
        var files = TestKiler.Files(levels.ToContent(), null, [(user.Key, user.ToContent())]);

        Assert.True(SignIn.TryStart(files, Account, TestKiler.Password, out var session, out _));

        using (session)
        {
            Assert.Equal([0, 1, 2], session.Levels);
        }
    }

    [Fact]
    public void TryStart_WithATemporaryPassword_GivesASessionThatCanOnlyChangeIt()
    {
        var files = _kiler.Files(guestMode: true, _kiler.User(Account, level: 1, canEdit: true, mustChangePassword: true));

        Assert.True(SignIn.TryStart(files, Account, TestKiler.Password, out var session, out _));

        using (session)
        {
            Assert.True(session.MustChangePassword);
            Assert.Equal([0, 1], session.Levels);
            Assert.Equal(AccessRefusal.MustChangePassword, Access.Check(Need.Reader, session, files));
        }
    }

    [Theory]
    [InlineData(Password.MinLength - 1)]
    [InlineData(Password.MaxLength + 1)]
    public void TryStart_WithAPasswordThatDoesNotFit_Throws(int length)
    {
        var files = _kiler.Files(guestMode: false);

        Assert.Throws<ArgumentException>(() => SignIn.TryStart(files, Account, new string('p', length), out _, out _));
    }
}

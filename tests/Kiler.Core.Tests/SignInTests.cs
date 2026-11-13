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
    public void TryStart_WithTheRightPassword_GivesASession_WithTheKeysAndTheSealedRightsOfTheFile()
    {
        var files = _kiler.Files(guestMode: false, _kiler.User(Account, level: 2, canEdit: true, seal: true));

        Assert.True(SignIn.TryStart(files, Account, TestKiler.Password, out var session, out _));

        using (session)
        {
            Assert.Equal(Account, session.Account);
            Assert.Equal($"Demo {Account}", session.DisplayName);
            Assert.Equal([0, 1, 2], session.Levels);
            Assert.True(session.CanEdit);
            Assert.False(session.IsAdmin);
            Assert.Null(session.RightsRefused);
            Assert.False(session.MustChangePassword);
            Assert.False(session.Reads(3));
            Assert.True(session.Use(2, key => LevelCheck.Verify(key, _kiler.Check(2), 2)), "The key of level 2 is not the key of the level.");

            // A user's file has only the public key of the admin key (M4.13).
            Assert.Equal(_kiler.AdminKey.PublicKey, session.AdminPublicKey);
            Assert.False(session.HoldsAdminKey);
        }
    }

    [Fact]
    public void TryStart_OfAnAdmin_HoldsThePublicKeyAndThePrivateKeyOfTheAdminKey_AndIsAnAdmin()
    {
        var admin = _kiler.User(Account, level: 3, canEdit: true, isAdmin: true, adminKey: AdminKeyLock.PublicAndPrivateKey, seal: true);
        var files = _kiler.Files(guestMode: false, admin);

        Assert.True(SignIn.TryStart(files, Account, TestKiler.Password, out var session, out _));

        using (session)
        {
            Assert.Equal(_kiler.AdminKey.PublicKey, session.AdminPublicKey);
            Assert.True(session.HoldsAdminKey);
            Assert.Equal((true, true, null), (session.CanEdit, session.IsAdmin, session.RightsRefused));
            Assert.Null(Access.Check(Need.Admin, session, files));
            var seal = session.UseAdminKey(key => key.Seal(SealPurpose.UserFile, "made-up text"u8));
            Assert.True(AdminSeal.Verify(_kiler.AdminKey.PublicKey, SealPurpose.UserFile, "made-up text"u8, seal), "The admin key of the session is not the one of the file.");
        }
    }

    [Theory]
    [InlineData("canEdit")]
    [InlineData("isAdmin")]
    [InlineData("level")]
    [InlineData("their own seal")]
    public void TryStart_OfAFileWhoseRightsTheUserWrote_IsAReader_WithTheReason(string written)
    {
        // M4.15: a user of the top level who wrote isAdmin into their own file holds every key, and gets no admin right.
        using var theirs = AdminKey.New();
        var level = written == "isAdmin" ? 3 : 2;
        var content = written == "their own seal"
            ? _kiler.User(Account, level, canEdit: true, isAdmin: true).Sealed(theirs).ToContent()
            : _kiler.User(Account, level, seal: true).ToContent();
        if (written != "their own seal")
        {
            content[written] = written == "level" ? 3 : true;
        }

        var files = TestKiler.Files(_kiler.Levels.ToContent(), null, [(UserFile.KeyOf(Account), content)]);

        Assert.True(SignIn.TryStart(files, Account, TestKiler.Password, out var session, out _));

        using (session)
        {
            Assert.Equal([0, .. Enumerable.Range(1, level)], session.Levels);
            Assert.Equal((false, false, RightsRefusal.NotSealed), (session.CanEdit, session.IsAdmin, session.RightsRefused));
            Assert.Null(Access.Check(Need.Reader, session, files));
            Assert.Equal(AccessRefusal.CannotEdit, Access.Check(Need.Editor, session, files));
            Assert.Equal(AccessRefusal.NotAdmin, Access.Check(Need.Admin, session, files));
        }
    }

    [Fact]
    public void TryStart_OfASealedAdminWithoutThePrivateKey_IsNoAdmin_WithTheReason()
    {
        // As a former admin whose private key someone took out of the file, or one whose value is damaged (M4.15).
        var admin = _kiler.User(Account, level: 3, canEdit: true, isAdmin: true, adminKey: AdminKeyLock.PublicKey, seal: true);
        var files = _kiler.Files(guestMode: false, admin);

        Assert.True(SignIn.TryStart(files, Account, TestKiler.Password, out var session, out _));

        using (session)
        {
            Assert.Equal((true, false, RightsRefusal.NoAdminKey), (session.CanEdit, session.IsAdmin, session.RightsRefused));
            Assert.Null(Access.Check(Need.Editor, session, files));
            Assert.Equal(AccessRefusal.NotAdmin, Access.Check(Need.Admin, session, files));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TryStart_WithoutThePublicKeyOfTheAdminKey_IsRefused(bool damaged)
    {
        // A public key from another lock with the same password does not decrypt under this salt, as a damaged value.
        var user = _kiler.User(Account, level: 2, canEdit: true, adminKey: damaged ? AdminKeyLock.PublicKey : AdminKeyLock.None, seal: true);
        if (damaged)
        {
            var other = _kiler.User(Account, level: 2).Keys;
            user = user.WithKeys(new LockedKeys(user.Keys.Salt, user.Keys.Keys, other.AdminPublicKey), mustChangePassword: false);
        }

        var files = _kiler.Files(guestMode: false, user);

        Assert.False(SignIn.TryStart(files, Account, TestKiler.Password, out var session, out var refusal));

        Assert.Null(session);
        Assert.Equal(SignInRefusal.NoAdminPublicKey, refusal);
    }

    [Theory]
    [InlineData("a check planted")]
    [InlineData("a level cut away")]
    [InlineData("no seal")]
    [InlineData("the seal of another admin key")]
    public void TryStart_WhereLevelsJsonIsNotSealedByTheAdminKey_IsRefused_AnAdminToo(string change)
    {
        // M4.15: the checks come only from a sealed levels.json, so a check of a key of someone else's never verifies.
        using var planted = SecretKey.New();
        using var theirs = AdminKey.New();
        var entries = _kiler.Levels.Levels;
        var recovery = _kiler.Levels.Recovery;
        var levels = change switch
        {
            "a check planted" => new LevelsFile([.. entries.Take(3), entries[3] with { Check = LevelCheck.Create(planted, 3) }], recovery),
            "a level cut away" => new LevelsFile([.. entries.Take(3)], recovery),
            "no seal" => new LevelsFile(entries, recovery),
            _ => new LevelsFile(entries, recovery).Sealed(theirs, 1),
        };
        var admin = _kiler.User(Account, level: 3, canEdit: true, isAdmin: true, adminKey: AdminKeyLock.PublicAndPrivateKey, seal: true);
        var files = TestKiler.Files(levels.ToContent(), null, [(admin.Key, admin.ToContent())]);

        Assert.False(SignIn.TryStart(files, Account, TestKiler.Password, out var session, out var refusal));

        Assert.Null(session);
        Assert.Equal(SignInRefusal.LevelsNotSealed, refusal);
    }

    [Fact]
    public void TryStart_WhereLevelsJsonIsSealedForAnotherVersion_IsRefused()
    {
        // The seal binds the version, so a sealed levels.json does not verify at the next version that someone writes.
        var user = _kiler.User(Account, level: 1);
        using var data = new TestData();
        data.WriteAndRead(LevelsFile.Key, new LevelsFile(_kiler.Levels.Levels, _kiler.Levels.Recovery).ToContent());
        data.ReplaceAndRead(LevelsFile.Key, 1, _kiler.Levels.ToContent());
        data.WriteAndRead(user.Key, user.ToContent());
        var files = new Team(data.Copy).Files;
        Assert.Equal(2, files.Levels?.Version);

        Assert.False(SignIn.TryStart(files, Account, TestKiler.Password, out _, out var refusal));

        Assert.Equal(SignInRefusal.LevelsNotSealed, refusal);
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
        // Keys that unlock with their password, but are not the keys of the levels; with the public key of the admin key, as a
        // former user knows it, so the sign-in gets to the check of the keys (M4.15).
        using var one = SecretKey.New();
        using var two = SecretKey.New();
        var keys = LevelKeys.Lock(new Dictionary<int, SecretKey> { [1] = one, [2] = two }, _kiler.AdminKey.PublicKey, null, TestKiler.Password);
        var self = new UserFile(Account, "Demo User", level: 2, canEdit: true, isAdmin: true, mustChangePassword: false, keys);
        var files = _kiler.Files(guestMode: false, self);

        Assert.False(SignIn.TryStart(files, Account, TestKiler.Password, out _, out var refusal));

        Assert.Equal(SignInRefusal.NoAccess, refusal);
    }

    [Fact]
    public void TryStart_ForAUserWhoRaisedTheirLevelAndIsAdmin_ReadsOnlyTheLevelsOfTheirKeys_AsAReader()
    {
        // Their own file, with the top level, isAdmin, and a key of their own for the top level beside the real key of level
        // 1, all locked with their password: the key of their own unlocks, and its check refuses it; the file is not sealed.
        using var own = SecretKey.New();
        var raised = _kiler.User(Account, level: 3, canEdit: true, isAdmin: true, keyLevels: [1], own: new Dictionary<int, SecretKey> { [3] = own });
        var files = _kiler.Files(guestMode: false, raised);

        Assert.True(SignIn.TryStart(files, Account, TestKiler.Password, out var session, out _));

        using (session)
        {
            Assert.Equal([0, 1], session.Levels);
            Assert.Equal((false, false, RightsRefusal.NotSealed), (session.CanEdit, session.IsAdmin, session.RightsRefused));
            Assert.Equal(AccessRefusal.NotAdmin, Access.Check(Need.Admin, session, files));
        }
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("of another level")]
    [InlineData("damaged")]
    public void TryStart_WhereTheCheckOfALevelIsMissingOrDamaged_LeavesThatLevelOut(string check)
    {
        // Sealed by the admin key, as a mistake of a Kiler would leave it: only a check that verifies counts.
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
        var files = TestKiler.Files(new LevelsFile(entries, _kiler.Levels.Recovery).Sealed(_kiler.AdminKey, 1).ToContent(), null, [(user.Key, user.ToContent())]);

        Assert.True(SignIn.TryStart(files, Account, TestKiler.Password, out var session, out _));

        using (session)
        {
            Assert.Equal([0, 1, 3], session.Levels);
        }
    }

    [Fact]
    public void TryStart_WithAKeyAboveTheTopLevel_LeavesItOut()
    {
        // levels.json has the levels 0 to 2 only, sealed so, and the user file a key of level 3.
        var user = _kiler.User(Account, level: 3);
        var levels = new LevelsFile(_kiler.Levels.Levels.Take(3).ToList(), _kiler.Levels.Recovery).Sealed(_kiler.AdminKey, 1);
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

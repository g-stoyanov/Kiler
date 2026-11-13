using Kiler.Crypto;

namespace Kiler.Core.Tests;

/// <summary>The rights of a request (M4.4): each need refuses whom it must, and only a sealed user file gives a right (M4.15).</summary>
public sealed class AccessTests
{
    private const AccessRefusal NotSignedIn = AccessRefusal.NotSignedIn;
    private const AccessRefusal MustChange = AccessRefusal.MustChangePassword;
    private const AccessRefusal CannotEdit = AccessRefusal.CannotEdit;
    private const AccessRefusal NotAdmin = AccessRefusal.NotAdmin;

    /// <summary>The files of a team of the levels 0 to 3, without and with guest mode.</summary>
    private static readonly Lazy<(TeamFiles Closed, TeamFiles Guests)> Teams = new(() =>
    {
        using var kiler = new TestKiler(top: 3);
        return (Team(kiler, guestMode: false), Team(kiler, guestMode: true));
    });

    public enum Caller
    {
        /// <summary>No session, without guest mode.</summary>
        Nobody,

        /// <summary>No session, with guest mode.</summary>
        Guest,

        /// <summary>A session that a lock has disposed, without guest mode.</summary>
        Locked,

        /// <summary>A session that a lock has disposed, with guest mode: a guest.</summary>
        LockedWithGuests,

        /// <summary>A session of level 3, an editor and an admin, with a temporary password.</summary>
        Temporary,

        /// <summary>A session of level 1.</summary>
        User,

        /// <summary>A session of level 2 that can edit.</summary>
        Editor,

        /// <summary>A session of level 3, an editor and an admin, with every key.</summary>
        Admin,

        /// <summary>A session of a file that says level 3, an editor and an admin, not sealed, with the key of level 1 only.</summary>
        Raised,

        /// <summary>A session of a file that says level 3, an editor and an admin, not sealed, with every key (M4.15).</summary>
        NotSealed,

        /// <summary>A session of level 3, sealed as an editor and an admin, with every key and without the private key (M4.15).</summary>
        NoAdminKey,
    }

    public static TheoryData<Caller, Need, AccessRefusal?> Matrix()
    {
        var rows = new (Caller Caller, AccessRefusal?[] Refusals)[]
        {
            // Nothing, SignedIn, Reader, Editor, Admin
            (Caller.Nobody, [null, NotSignedIn, NotSignedIn, NotSignedIn, NotSignedIn]),
            (Caller.Guest, [null, NotSignedIn, null, NotSignedIn, NotSignedIn]),
            (Caller.Locked, [null, NotSignedIn, NotSignedIn, NotSignedIn, NotSignedIn]),
            (Caller.LockedWithGuests, [null, NotSignedIn, null, NotSignedIn, NotSignedIn]),
            (Caller.Temporary, [null, null, MustChange, MustChange, MustChange]),
            (Caller.User, [null, null, null, CannotEdit, NotAdmin]),
            (Caller.Editor, [null, null, null, null, NotAdmin]),
            (Caller.Admin, [null, null, null, null, null]),
            (Caller.Raised, [null, null, null, CannotEdit, NotAdmin]),
            (Caller.NotSealed, [null, null, null, CannotEdit, NotAdmin]),
            (Caller.NoAdminKey, [null, null, null, null, NotAdmin]),
        };
        var data = new TheoryData<Caller, Need, AccessRefusal?>();
        foreach (var (caller, refusals) in rows)
        {
            foreach (var need in Enum.GetValues<Need>())
            {
                data.Add(caller, need, refusals[(int)need]);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Matrix))]
    public void Check_GivesEachNeedOnlyToWhomItMust(Caller caller, Need need, AccessRefusal? expected)
    {
        var files = caller is Caller.Guest or Caller.LockedWithGuests ? Teams.Value.Guests : Teams.Value.Closed;
        var levels = Levels;
        using var noAdminKey = AdminKey.New();
        using var session = caller switch
        {
            Caller.Nobody or Caller.Guest => null,
            Caller.Locked or Caller.LockedWithGuests => SessionTests.NewSealedSession(top: 3, canEdit: true, isAdmin: true, levels: levels),
            Caller.Temporary => SessionTests.NewSealedSession(top: 3, canEdit: true, isAdmin: true, mustChangePassword: true, levels: levels),
            Caller.User => SessionTests.NewSealedSession(top: 1, levels: levels),
            Caller.Editor => SessionTests.NewSealedSession(top: 2, canEdit: true, levels: levels),
            Caller.Admin => SessionTests.NewSealedSession(top: 3, canEdit: true, isAdmin: true, levels: levels),
            Caller.Raised => SessionTests.NewSession(top: 1, level: 3, canEdit: true, isAdmin: true, levels: levels),
            Caller.NotSealed => SessionTests.NewSession(top: 3, canEdit: true, isAdmin: true, levels: levels, adminPublicKey: noAdminKey.PublicKey),
            _ => SessionTests.NewSession(top: 3, canEdit: true, isAdmin: true, levels: levels, adminPublicKey: noAdminKey.PublicKey, sealedBy: noAdminKey),
        };
        if (caller is Caller.Locked or Caller.LockedWithGuests)
        {
            session!.Dispose();
        }

        // An admin signs in only with a levels.json that its admin key sealed (M4.15), and Need.Admin takes that one (M4.16a).
        if (caller == Caller.Admin)
        {
            files = SealedBy(session!, levels);
        }

        Assert.Equal(expected, Access.Check(need, session, files));
    }

    [Fact]
    public void Check_WithoutTheFilesOfATeam_GivesNoGuestAndNoAdmin()
    {
        using var admin = SessionTests.NewSealedSession(top: 3, canEdit: true, isAdmin: true);

        Assert.Equal(NotSignedIn, Access.Check(Need.Reader, null, null));
        Assert.Equal(NotAdmin, Access.Check(Need.Admin, admin, null));
        Assert.Null(Access.Check(Need.Editor, admin, null));
    }

    [Fact]
    public void Check_WhereLevelsJsonHasALevelAboveTheKeysOfTheAdmin_IsNoAdmin()
    {
        using var admin = SessionTests.NewSealedSession(top: 2, canEdit: true, isAdmin: true);

        Assert.Equal(NotAdmin, Access.Check(Need.Admin, admin, SealedBy(admin, Levels)));
    }

    [Fact]
    public void Check_WhereACheckOfLevelsJsonIsNotTheOneThatTheKeysOfTheAdminVerifiedAgainst_AsksForANewSignIn()
    {
        // As a check planted after the sign-in (M4.4 security review): the keys of the session verified against others.
        using var admin = SessionTests.NewSealedSession(top: 3, canEdit: true, isAdmin: true);
        using var user = SessionTests.NewSealedSession(top: 1);
        var files = SealedBy(admin, Levels);

        Assert.Equal(AccessRefusal.LevelsChanged, Access.Check(Need.Admin, admin, files));
        Assert.Null(Access.Check(Need.Editor, admin, files));
        Assert.Equal(NotAdmin, Access.Check(Need.Admin, user, files));
    }

    [Fact]
    public void Check_WhereLevelsJsonHasLostALevelSinceTheSignInOfTheAdmin_AsksForANewSignIn()
    {
        // M4.15, the M4.6 security review: the top level and its check cut away after the sign-in, the other checks equal.
        using var admin = SessionTests.NewSealedSession(top: 3, canEdit: true, isAdmin: true, levels: Levels);
        var cut = new LevelsFile([.. Levels.Levels.Take(3)], Levels.Recovery);

        Assert.Null(Access.Check(Need.Admin, admin, SealedBy(admin, Levels)));
        Assert.Equal(AccessRefusal.LevelsChanged, Access.Check(Need.Admin, admin, SealedBy(admin, cut)));

        // Cut and not sealed, with no sealed version in the copy, as a Kiler that never saw one: there are no levels to hold.
        Assert.Equal(NotAdmin, Access.Check(Need.Admin, admin, TestKiler.Files(cut.ToContent(), null, [])));
    }

    private static LevelsFile Levels => Teams.Value.Closed.Levels!.Model!;

    /// <summary>The files of a team whose <c>levels.json</c> has the levels and checks of <paramref name="levels"/>, sealed by the admin key of <paramref name="admin"/> for version 1.</summary>
    private static TeamFiles SealedBy(Session admin, LevelsFile levels) =>
        admin.UseAdminKey(adminKey => TestKiler.Files(new LevelsFile(levels.Levels, levels.Recovery).Sealed(adminKey, 1).ToContent(), null, []));

    private static TeamFiles Team(TestKiler kiler, bool guestMode) =>
        TestKiler.Files(kiler.Levels.ToContent(), new SettingsFile(guestMode, autoLockMinutes: 30).ToContent(), []);
}

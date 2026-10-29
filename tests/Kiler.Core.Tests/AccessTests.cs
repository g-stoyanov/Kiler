namespace Kiler.Core.Tests;

/// <summary>The rights of a request (M4.4): each need refuses whom it must.</summary>
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

        /// <summary>A session of a file that says level 3, an editor and an admin, with the key of level 1 only.</summary>
        Raised,
    }

    public static TheoryData<Caller, Need, AccessRefusal?> Matrix()
    {
        var rows = new (Caller Caller, AccessRefusal?[] Refusals)[]
        {
            // Nothing, Reader, Editor, Admin
            (Caller.Nobody, [null, NotSignedIn, NotSignedIn, NotSignedIn]),
            (Caller.Guest, [null, null, NotSignedIn, NotSignedIn]),
            (Caller.Locked, [null, NotSignedIn, NotSignedIn, NotSignedIn]),
            (Caller.LockedWithGuests, [null, null, NotSignedIn, NotSignedIn]),
            (Caller.Temporary, [null, MustChange, MustChange, MustChange]),
            (Caller.User, [null, null, CannotEdit, NotAdmin]),
            (Caller.Editor, [null, null, null, NotAdmin]),
            (Caller.Admin, [null, null, null, null]),
            (Caller.Raised, [null, null, null, NotAdmin]),
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
        using var session = caller switch
        {
            Caller.Nobody or Caller.Guest => null,
            Caller.Locked or Caller.LockedWithGuests => SessionTests.NewSession(top: 3, canEdit: true, isAdmin: true),
            Caller.Temporary => SessionTests.NewSession(top: 3, canEdit: true, isAdmin: true, mustChangePassword: true),
            Caller.User => SessionTests.NewSession(top: 1),
            Caller.Editor => SessionTests.NewSession(top: 2, canEdit: true),
            Caller.Admin => SessionTests.NewSession(top: 3, canEdit: true, isAdmin: true),
            _ => SessionTests.NewSession(top: 1, level: 3, canEdit: true, isAdmin: true),
        };
        if (caller is Caller.Locked or Caller.LockedWithGuests)
        {
            session!.Dispose();
        }

        Assert.Equal(expected, Access.Check(need, session, files));
    }

    [Fact]
    public void Check_WithoutTheFilesOfATeam_GivesNoGuestAndNoAdmin()
    {
        using var admin = SessionTests.NewSession(top: 3, canEdit: true, isAdmin: true);

        Assert.Equal(NotSignedIn, Access.Check(Need.Reader, null, null));
        Assert.Equal(NotAdmin, Access.Check(Need.Admin, admin, null));
        Assert.Null(Access.Check(Need.Editor, admin, null));
    }

    [Fact]
    public void Check_WhereLevelsJsonHasALevelAboveTheKeysOfTheAdmin_IsNoAdmin()
    {
        using var admin = SessionTests.NewSession(top: 2, canEdit: true, isAdmin: true);

        Assert.Equal(NotAdmin, Access.Check(Need.Admin, admin, Teams.Value.Closed));
    }

    private static TeamFiles Team(TestKiler kiler, bool guestMode) =>
        TestKiler.Files(kiler.Levels.ToContent(), new SettingsFile(guestMode, autoLockMinutes: 30).ToContent(), []);
}

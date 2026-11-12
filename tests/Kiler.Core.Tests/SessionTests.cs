using Kiler.Crypto;

namespace Kiler.Core.Tests;

/// <summary>
/// The session (M4.4): its keys until a lock, a sign-out or the stop, and a key in use after a lock answers as locked; the
/// admin key too (M4.13).
/// </summary>
public sealed class SessionTests
{
    [Fact]
    public void Use_AfterALock_IsLocked()
    {
        var session = NewSession(1);

        session.Dispose();

        Assert.True(session.IsLocked);
        Assert.Throws<SessionLockedException>(() => session.Use(1, key => LevelCheck.Create(key, 1)));
    }

    [Fact]
    public void Use_WhenALockComesWhileTheKeyIsInUse_IsLocked()
    {
        using var session = NewSession(1);

        Assert.Throws<SessionLockedException>(() => session.Use(1, key =>
        {
            session.Dispose();
            return LevelCheck.Create(key, 1);
        }));
    }

    [Fact]
    public void Use_WhereSomethingElseHasBeenDisposed_IsNotALock()
    {
        using var session = NewSession(1);

        Assert.Throws<ObjectDisposedException>(() => session.Use<string>(1, _ => throw new ObjectDisposedException("Made up")));
        Assert.False(session.IsLocked);
    }

    [Fact]
    public void Use_OfALevelWithoutAKey_Throws()
    {
        using var session = NewSession(1);

        Assert.Throws<ArgumentOutOfRangeException>(() => session.Use(2, key => LevelCheck.Create(key, 2)));
    }

    [Fact]
    public void Lock_DisposesTheAdminKey_AndKeepsThePublicKey()
    {
        var adminKey = AdminKey.New();
        var session = NewSession(1, adminPublicKey: adminKey.PublicKey, adminKey: adminKey);
        Assert.True(session.HoldsAdminKey);

        session.Dispose();

        // A use that would not touch the disposed key is locked too.
        Assert.Throws<SessionLockedException>(() => session.UseAdminKey(key => key.PublicKey));
        Assert.Throws<ObjectDisposedException>(() => adminKey.Seal(SealPurpose.UserFile, "made-up text"u8));
        Assert.Equal(adminKey.PublicKey, session.AdminPublicKey);
    }

    [Fact]
    public void UseAdminKey_WhenALockComesWhileTheKeyIsInUse_IsLocked()
    {
        var adminKey = AdminKey.New();
        using var session = NewSession(1, adminPublicKey: adminKey.PublicKey, adminKey: adminKey);

        Assert.Throws<SessionLockedException>(() => session.UseAdminKey(key =>
        {
            session.Dispose();
            return key.Seal(SealPurpose.UserFile, "made-up text"u8);
        }));
    }

    [Fact]
    public void UseAdminKey_GivesTheKeyOfThePublicKey()
    {
        var adminKey = AdminKey.New();
        using var session = NewSession(1, adminPublicKey: adminKey.PublicKey, adminKey: adminKey);

        var seal = session.UseAdminKey(key => key.Seal(SealPurpose.UserFile, "made-up text"u8));

        Assert.True(AdminSeal.Verify(adminKey.PublicKey, SealPurpose.UserFile, "made-up text"u8, seal), "The seal of the session's key does not verify.");
    }

    [Fact]
    public void UseAdminKey_OfASessionWithoutIt_Throws()
    {
        using var adminKey = AdminKey.New();
        using var session = NewSession(1, adminPublicKey: adminKey.PublicKey);

        Assert.False(session.HoldsAdminKey);
        Assert.Throws<InvalidOperationException>(() => session.UseAdminKey(key => key.PublicKey));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AnAdminKeyThatIsNotOfThePublicKey_IsNotKept_AndIsDisposed(bool otherPublicKey)
    {
        using var other = AdminKey.New();
        var adminKey = AdminKey.New();

        using var session = NewSession(1, adminPublicKey: otherPublicKey ? other.PublicKey : null, adminKey: adminKey);

        Assert.False(session.HoldsAdminKey);
        Assert.Throws<ObjectDisposedException>(() => adminKey.Seal(SealPurpose.UserFile, "made-up text"u8));
    }

    [Fact]
    public void Start_LocksTheSessionBefore()
    {
        using var current = new CurrentSession();
        var first = NewSession(1);
        var second = NewSession(1);

        current.Start(first);
        current.Start(second);

        Assert.True(first.IsLocked);
        Assert.False(second.IsLocked);
        Assert.Same(second, current.Session);
    }

    [Fact]
    public void Lock_DisposesTheKeys_AndLeavesNoSession()
    {
        using var current = new CurrentSession();
        var session = NewSession(1);
        current.Start(session);

        current.Lock();

        Assert.True(session.IsLocked);
        Assert.Null(current.Session);
        Assert.Throws<SessionLockedException>(() => session.Use(1, key => LevelCheck.Create(key, 1)));
    }

    [Fact]
    public void Dispose_LocksTheSession_AndASessionThatASignInBringsAfterIt()
    {
        var current = new CurrentSession();
        var before = NewSession(1);
        var after = NewSession(1);
        current.Start(before);

        current.Dispose();
        current.Start(after);

        Assert.True(before.IsLocked);
        Assert.True(after.IsLocked);
        Assert.Null(current.Session);
    }

    [Fact]
    public void Changed_ComesAfterEachSignInAndEachLock_WithTheChangeMade()
    {
        using var current = new CurrentSession();
        var first = NewSession(1);
        var second = NewSession(1);
        var seen = new List<string>();
        current.Changed += (_, _) => seen.Add($"{current.Session == second}/{first.IsLocked}/{second.IsLocked}");

        current.Start(first);
        current.Start(second);
        current.Lock();
        current.Lock();

        // The second lock has no session, but a page that asks for the lock wants every page on the lock screen.
        Assert.Equal(["False/False/False", "True/True/False", "False/True/True", "False/True/True"], seen);
    }

    [Fact]
    public void LockOfASession_OnlyWhileItIsInPlace_LocksIt()
    {
        using var current = new CurrentSession();
        var idle = NewSession(1);
        var signedIn = NewSession(1);
        current.Start(idle);
        current.Start(signedIn);
        var changes = 0;
        current.Changed += (_, _) => changes++;

        current.Lock(idle);

        Assert.Same(signedIn, current.Session);
        Assert.False(signedIn.IsLocked);
        Assert.Equal(0, changes);

        current.Lock(signedIn);

        Assert.Null(current.Session);
        Assert.True(signedIn.IsLocked);
        Assert.Equal(1, changes);
    }

    [Fact]
    public void Start_AfterTheStop_ChangesNothing()
    {
        var current = new CurrentSession();
        current.Dispose();
        var changes = 0;
        current.Changed += (_, _) => changes++;

        current.Start(NewSession(1));

        Assert.Equal(0, changes);
    }

    /// <summary>
    /// A session with new keys of the levels 1 to <paramref name="top"/>, without PBKDF2, which verified against the checks of
    /// <paramref name="levels"/> (M4.6), or against made-up ones without it, and with the admin key given, which it owns (M4.13).
    /// </summary>
    internal static Session NewSession(
        int top,
        int level = -1,
        bool canEdit = false,
        bool isAdmin = false,
        bool mustChangePassword = false,
        LevelsFile? levels = null,
        string? adminPublicKey = null,
        AdminKey? adminKey = null)
    {
        level = level < 0 ? top : level;
        var user = new UserFile("demo.user", "Demo User", level, canEdit, isAdmin, mustChangePassword, new LockedKeys("salt", new Dictionary<int, string>()));
        var keys = Enumerable.Range(1, top).ToDictionary(key => key, _ => SecretKey.New());
        var checks = keys.Keys.ToDictionary(key => key, key => levels is not null && key <= levels.Top ? levels.Levels[key].Check! : $"made-up check {key}");
        return new Session(user, userVersion: 1, keys, checks, adminPublicKey, adminKey);
    }
}

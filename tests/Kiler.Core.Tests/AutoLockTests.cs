using System.Text.Json.Nodes;
using Kiler.Crypto;
using Microsoft.Extensions.Time.Testing;

namespace Kiler.Core.Tests;

/// <summary>
/// The automatic lock (M4.5b) on a fake clock: the minutes without activity, where they come from (a <c>settings.json</c> that
/// the admin key of the session sealed, M4.15), and what puts the lock off.
/// </summary>
public sealed class AutoLockTests : IDisposable
{
    private static readonly TimeSpan Tick = TimeSpan.FromTicks(1);
    private static readonly TimeSpan Default = TimeSpan.FromMinutes(AutoLock.DefaultMinutes);

    private readonly FakeTimeProvider _time = new();
    private readonly AdminKey _adminKey = AdminKey.New();
    private readonly CurrentSession _sessions = new();
    private readonly AutoLock _autoLock;
    private TeamFiles? _files;

    public AutoLockTests() => _autoLock = new AutoLock(_sessions, () => _files, _time);

    public void Dispose()
    {
        _autoLock.Dispose();
        _sessions.Dispose();
        _adminKey.Dispose();
    }

    [Fact]
    public void WithoutActivity_LocksTheSessionAfterTheMinutes_AsAChange()
    {
        var session = SignIn();
        var changes = 0;
        _sessions.Changed += (_, _) => changes++;

        _time.Advance(Default - Tick);
        Assert.False(session.IsLocked);

        _time.Advance(Tick);
        Assert.True(session.IsLocked);
        Assert.Null(_sessions.Session);
        Assert.Equal(1, changes);
    }

    [Fact]
    public void AnActivity_PutsTheLockOffUntilTheMinutesHavePassedFromIt()
    {
        var session = SignIn();
        _time.Advance(TimeSpan.FromMinutes(20));
        _autoLock.Activity();

        _time.Advance(TimeSpan.FromMinutes(30) - Tick);
        Assert.False(session.IsLocked);

        _time.Advance(Tick);
        Assert.True(session.IsLocked);
    }

    [Fact]
    public void TheMinutesOfTheSettings_AreTheWait()
    {
        _files = Files(Sealed(5));
        var session = SignIn();

        _time.Advance(TimeSpan.FromMinutes(5) - Tick);
        Assert.False(session.IsLocked);

        _time.Advance(Tick);
        Assert.True(session.IsLocked);
    }

    [Fact]
    public void LongerMinutesInTheSettings_CountWhenTheTimerComes()
    {
        var session = SignIn();
        _files = Files(Sealed(60));

        _time.Advance(TimeSpan.FromMinutes(60) - Tick);
        Assert.False(session.IsLocked);

        _time.Advance(Tick);
        Assert.True(session.IsLocked);
    }

    // The first request of a page can come before the copy has opened, or before the wizard has written settings.json.
    [Fact]
    public void ShorterMinutesThatComeAfterTheTimerIsSet_CountFromTheNextActivity()
    {
        _autoLock.Activity();
        _time.Advance(TimeSpan.FromMinutes(1));
        _files = Files(Sealed(5));
        var session = SignIn();

        _time.Advance(TimeSpan.FromMinutes(5) - Tick);
        Assert.False(session.IsLocked);

        _time.Advance(Tick);
        Assert.True(session.IsLocked);
    }

    [Fact]
    public void WithoutASession_TheTimerLocksNothing_AndTheNextActivityStartsItAgain()
    {
        _autoLock.Activity();
        _time.Advance(TimeSpan.FromMinutes(40));

        var session = SignIn();
        _time.Advance(Default - Tick);
        Assert.False(session.IsLocked);

        _time.Advance(Tick);
        Assert.True(session.IsLocked);
    }

    [Fact]
    public void AfterTheStop_NothingLocks()
    {
        var session = SignIn();

        _autoLock.Dispose();
        _autoLock.Activity();
        _time.Advance(TimeSpan.FromHours(1));

        Assert.False(session.IsLocked);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheMinutesOfSettingsThatTheAdminKeyDidNotSeal_AreNotTheWait(bool anotherKey)
    {
        // M4.15, the M4.5b security review: whoever can write the share does not set the lock of every session.
        using var theirs = AdminKey.New();
        var settings = new SettingsFile(guestMode: false, autoLockMinutes: 5);
        _files = Files(anotherKey ? settings.Sealed(theirs, 1).ToContent() : settings.ToContent());
        var session = SignIn();

        _time.Advance(Default - Tick);
        Assert.False(session.IsLocked);

        _time.Advance(Tick);
        Assert.True(session.IsLocked);
    }

    [Fact]
    public void MinutesOf_TheSettings_AreTheirMinutes()
    {
        var key = _adminKey.PublicKey;
        Assert.Equal(1, AutoLock.MinutesOf(Files(new SettingsFile(guestMode: true, autoLockMinutes: 1).Sealed(_adminKey, 1).ToContent()), key));
        Assert.Equal(SettingsFile.MaxAutoLockMinutes, AutoLock.MinutesOf(Files(Sealed(SettingsFile.MaxAutoLockMinutes)), key));
    }

    [Fact]
    public void MinutesOf_WithoutSettingsThatFitAndAreSealed_AreThe30()
    {
        var key = _adminKey.PublicKey;
        Assert.Equal(30, AutoLock.DefaultMinutes);
        Assert.Equal(AutoLock.DefaultMinutes, AutoLock.MinutesOf(null, key));
        Assert.Equal(AutoLock.DefaultMinutes, AutoLock.MinutesOf(Files(null), key));
        Assert.Equal(AutoLock.DefaultMinutes, AutoLock.MinutesOf(Files(new JsonObject { ["guestMode"] = false, ["autoLockMinutes"] = 0 }), key));
        Assert.Equal(AutoLock.DefaultMinutes, AutoLock.MinutesOf(Files(new JsonObject { ["guestMode"] = false, ["autoLockMinutes"] = "5" }), key));

        // M4.15: not sealed, or no public key to check the seal with.
        Assert.Equal(AutoLock.DefaultMinutes, AutoLock.MinutesOf(Files(new SettingsFile(guestMode: false, autoLockMinutes: 5).ToContent()), key));
        Assert.Equal(AutoLock.DefaultMinutes, AutoLock.MinutesOf(Files(Sealed(5)), null));
    }

    /// <summary>A sign-in as the API makes it: its request is an activity, then the session is put in place.</summary>
    private Session SignIn()
    {
        _autoLock.Activity();
        var session = SessionTests.NewSession(1, adminPublicKey: _adminKey.PublicKey);
        _sessions.Start(session);
        return session;
    }

    private static TeamFiles Files(JsonObject? settings) => TestKiler.Files(null, settings, []);

    /// <summary>Settings of <paramref name="minutes"/>, sealed by the admin key of the session for the version of their create.</summary>
    private JsonObject Sealed(int minutes) => new SettingsFile(guestMode: false, minutes).Sealed(_adminKey, 1).ToContent();
}

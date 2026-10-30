using System.Text.Json.Nodes;
using Microsoft.Extensions.Time.Testing;

namespace Kiler.Core.Tests;

/// <summary>The automatic lock (M4.5b) on a fake clock: the minutes without activity, where they come from, and what puts the lock off.</summary>
public sealed class AutoLockTests : IDisposable
{
    private static readonly TimeSpan Tick = TimeSpan.FromTicks(1);
    private static readonly TimeSpan Default = TimeSpan.FromMinutes(AutoLock.DefaultMinutes);

    private readonly FakeTimeProvider _time = new();
    private readonly CurrentSession _sessions = new();
    private readonly AutoLock _autoLock;
    private TeamFiles? _files;

    public AutoLockTests() => _autoLock = new AutoLock(_sessions, () => _files, _time);

    public void Dispose()
    {
        _autoLock.Dispose();
        _sessions.Dispose();
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
        _files = Files(new SettingsFile(guestMode: false, autoLockMinutes: 5).ToContent());
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
        _files = Files(new SettingsFile(guestMode: false, autoLockMinutes: 60).ToContent());

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
        _files = Files(new SettingsFile(guestMode: false, autoLockMinutes: 5).ToContent());
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

    [Fact]
    public void MinutesOf_TheSettings_AreTheirMinutes()
    {
        Assert.Equal(1, AutoLock.MinutesOf(Files(new SettingsFile(guestMode: true, autoLockMinutes: 1).ToContent())));
        Assert.Equal(SettingsFile.MaxAutoLockMinutes, AutoLock.MinutesOf(Files(new SettingsFile(guestMode: false, SettingsFile.MaxAutoLockMinutes).ToContent())));
    }

    [Fact]
    public void MinutesOf_WithoutSettingsThatFit_AreThe30()
    {
        Assert.Equal(30, AutoLock.DefaultMinutes);
        Assert.Equal(AutoLock.DefaultMinutes, AutoLock.MinutesOf(null));
        Assert.Equal(AutoLock.DefaultMinutes, AutoLock.MinutesOf(Files(null)));
        Assert.Equal(AutoLock.DefaultMinutes, AutoLock.MinutesOf(Files(new JsonObject { ["guestMode"] = false, ["autoLockMinutes"] = 0 })));
        Assert.Equal(AutoLock.DefaultMinutes, AutoLock.MinutesOf(Files(new JsonObject { ["guestMode"] = false, ["autoLockMinutes"] = "5" })));
    }

    /// <summary>A sign-in as the API makes it: its request is an activity, then the session is put in place.</summary>
    private Session SignIn()
    {
        _autoLock.Activity();
        var session = SessionTests.NewSession(1);
        _sessions.Start(session);
        return session;
    }

    private static TeamFiles Files(JsonObject? settings) => TestKiler.Files(null, settings, []);
}

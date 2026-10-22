using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;

namespace Kiler.App.Tests;

/// <summary>The counting of the open pages and the waits of the heartbeat, on a fake clock and a fake host lifetime.</summary>
public sealed class HeartbeatTests : IDisposable
{
    private static readonly HeartbeatTimes Times = new(TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(15));
    private static readonly TimeSpan Tick = TimeSpan.FromTicks(1);

    private readonly FakeTimeProvider _time = new();
    private readonly FakeLifetime _lifetime = new();
    private readonly Heartbeat _heartbeat;

    public HeartbeatTests() => _heartbeat = new Heartbeat(_lifetime, Times, _time);

    public void Dispose()
    {
        _heartbeat.Dispose();
        _lifetime.Dispose();
    }

    [Fact]
    public void DefaultTimes_WaitAMinuteForTheFirstPageAndTenSecondsAfterTheLastAndPingEveryFifteen()
    {
        Assert.Equal(new HeartbeatTimes(TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(15)), HeartbeatTimes.Default);
    }

    [Theory]
    [InlineData(0, 10, 15)]
    [InlineData(60, 0, 15)]
    [InlineData(60, 10, 0)]
    [InlineData(-1, 10, 15)]
    public void Times_NotAboveZero_AreRefused(int firstWait, int idleWait, int pingInterval)
    {
        var times = new HeartbeatTimes(TimeSpan.FromSeconds(firstWait), TimeSpan.FromSeconds(idleWait), TimeSpan.FromSeconds(pingInterval));

        Assert.Throws<ArgumentOutOfRangeException>(() => new Heartbeat(_lifetime, times, _time));
    }

    [Fact]
    public void NoPage_StopsAfterTheFirstWait()
    {
        _lifetime.Start();

        _time.Advance(Times.FirstWait - Tick);
        Assert.Equal(0, _lifetime.StopCalls);

        _time.Advance(Tick);
        Assert.Equal(1, _lifetime.StopCalls);
        Assert.True(_heartbeat.TimedOut);
    }

    [Fact]
    public void BeforeTheHostStarts_NothingStops()
    {
        _time.Advance(TimeSpan.FromHours(1));

        Assert.Equal(0, _lifetime.StopCalls);
    }

    [Fact]
    public void PageOpensWithinTheFirstWait_DoesNotStop()
    {
        _lifetime.Start();
        _time.Advance(TimeSpan.FromSeconds(30));

        using var page = Connect();
        _time.Advance(TimeSpan.FromHours(1));

        Assert.Equal(0, _lifetime.StopCalls);
    }

    [Fact]
    public void LastPageCloses_StopsAfterTheIdleWait()
    {
        _lifetime.Start();
        var page = Connect();
        _time.Advance(TimeSpan.FromHours(1));

        page.Dispose();
        _time.Advance(Times.IdleWait - Tick);
        Assert.Equal(0, _lifetime.StopCalls);

        _time.Advance(Tick);
        Assert.Equal(1, _lifetime.StopCalls);
        Assert.True(_heartbeat.TimedOut);
    }

    [Fact]
    public void PageReopensWithinTheIdleWait_CancelsTheStop()
    {
        _lifetime.Start();
        Connect().Dispose();
        _time.Advance(Times.IdleWait - Tick);

        var reloaded = Connect();
        _time.Advance(TimeSpan.FromHours(1));
        Assert.Equal(0, _lifetime.StopCalls);

        // The wait starts again from the second close, not from the first.
        reloaded.Dispose();
        _time.Advance(Times.IdleWait - Tick);
        Assert.Equal(0, _lifetime.StopCalls);
        _time.Advance(Tick);
        Assert.Equal(1, _lifetime.StopCalls);
    }

    [Fact]
    public void OneOfTwoPagesCloses_DoesNotStop()
    {
        _lifetime.Start();
        using var stays = Connect();

        Connect().Dispose();
        _time.Advance(TimeSpan.FromHours(1));

        Assert.Equal(0, _lifetime.StopCalls);
    }

    [Fact]
    public void PageClosedTwice_IsCountedOnce()
    {
        _lifetime.Start();
        using var stays = Connect();
        var closes = Connect();

        closes.Dispose();
        closes.Dispose();
        _time.Advance(TimeSpan.FromHours(1));

        Assert.Equal(0, _lifetime.StopCalls);
    }

    [Fact]
    public void PageOpenBeforeTheHostStarts_KeepsKilerRunning()
    {
        // Kestrel accepts connections before the host reports the start.
        var page = Connect();
        _lifetime.Start();
        _time.Advance(TimeSpan.FromHours(1));
        Assert.Equal(0, _lifetime.StopCalls);

        page.Dispose();
        _time.Advance(Times.IdleWait);
        Assert.Equal(1, _lifetime.StopCalls);
    }

    [Fact]
    public void PageOpenedAndClosedBeforeTheHostStarts_StartWaitsTheFirstWait()
    {
        Connect().Dispose();
        _lifetime.Start();

        _time.Advance(Times.FirstWait - Tick);
        Assert.Equal(0, _lifetime.StopCalls);
        _time.Advance(Tick);
        Assert.Equal(1, _lifetime.StopCalls);
    }

    [Fact]
    public void HealthCheckDuringTheIdleWait_PutsTheStopOffByTheFirstWait()
    {
        _lifetime.Start();
        Connect().Dispose();
        _time.Advance(TimeSpan.FromSeconds(5));

        Assert.True(_heartbeat.TryPostpone());

        _time.Advance(Times.FirstWait - Tick);
        Assert.Equal(0, _lifetime.StopCalls);
        _time.Advance(Tick);
        Assert.Equal(1, _lifetime.StopCalls);
    }

    [Fact]
    public void HealthCheckDuringTheFirstWait_CountsTheFirstWaitFromTheCheck()
    {
        _lifetime.Start();
        _time.Advance(TimeSpan.FromSeconds(50));

        Assert.True(_heartbeat.TryPostpone());

        _time.Advance(Times.FirstWait - Tick);
        Assert.Equal(0, _lifetime.StopCalls);
        _time.Advance(Tick);
        Assert.Equal(1, _lifetime.StopCalls);
    }

    [Fact]
    public void HealthCheckWhileAPageIsOpen_StopsNoSoonerThanTheFirstWaitFromTheCheck()
    {
        // The old tab closes while the browser of the second start is still opening the new one.
        _lifetime.Start();
        var page = Connect();
        Assert.True(_heartbeat.TryPostpone());
        _time.Advance(TimeSpan.FromSeconds(5));

        page.Dispose();

        _time.Advance(Times.FirstWait - TimeSpan.FromSeconds(5) - Tick);
        Assert.Equal(0, _lifetime.StopCalls);
        _time.Advance(Tick);
        Assert.Equal(1, _lifetime.StopCalls);
    }

    [Fact]
    public void HealthCheckLongBeforeThePageCloses_LeavesTheIdleWait()
    {
        _lifetime.Start();
        var page = Connect();
        Assert.True(_heartbeat.TryPostpone());
        _time.Advance(TimeSpan.FromHours(1));

        page.Dispose();

        _time.Advance(Times.IdleWait - Tick);
        Assert.Equal(0, _lifetime.StopCalls);
        _time.Advance(Tick);
        Assert.Equal(1, _lifetime.StopCalls);
    }

    [Fact]
    public void HealthCheckBeforeTheHostStarts_DoesNotShortenTheFirstWait()
    {
        Assert.True(_heartbeat.TryPostpone());
        _time.Advance(TimeSpan.FromSeconds(5));
        _lifetime.Start();

        _time.Advance(Times.FirstWait - Tick);
        Assert.Equal(0, _lifetime.StopCalls);
        _time.Advance(Tick);
        Assert.Equal(1, _lifetime.StopCalls);
    }

    [Fact]
    public void HealthCheck_NeverBringsTheStopForward()
    {
        var times = new HeartbeatTimes(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(15));
        using var lifetime = new FakeLifetime();
        using var heartbeat = new Heartbeat(lifetime, times, _time);
        lifetime.Start();
        heartbeat.TryConnect()!.Dispose();

        Assert.True(heartbeat.TryPostpone());

        _time.Advance(times.IdleWait - Tick);
        Assert.Equal(0, lifetime.StopCalls);
        _time.Advance(Tick);
        Assert.Equal(1, lifetime.StopCalls);
    }

    [Fact]
    public void AfterTheWait_RefusesPagesAndHealthChecksAndStopsOnce()
    {
        _lifetime.Start();
        _time.Advance(Times.FirstWait);

        Assert.Null(_heartbeat.TryConnect());
        Assert.False(_heartbeat.TryPostpone());
        _time.Advance(TimeSpan.FromHours(1));
        Assert.Equal(1, _lifetime.StopCalls);
    }

    [Fact]
    public void StopFromOutside_RefusesPagesAndHealthChecksAndDoesNotStopAgain()
    {
        // Ctrl+C while a page is open; then the page closes as the server stops.
        _lifetime.Start();
        var page = Connect();

        _lifetime.StopApplication();
        page.Dispose();

        Assert.Null(_heartbeat.TryConnect());
        Assert.False(_heartbeat.TryPostpone());
        _time.Advance(TimeSpan.FromHours(1));
        Assert.Equal(1, _lifetime.StopCalls);
        Assert.False(_heartbeat.TimedOut);
    }

    [Fact]
    public void StopFromOutsideDuringTheFirstWait_DoesNotStopAgain()
    {
        _lifetime.Start();
        _lifetime.StopApplication();

        _time.Advance(TimeSpan.FromHours(1));

        Assert.Equal(1, _lifetime.StopCalls);
        Assert.False(_heartbeat.TimedOut);
    }

    [Fact]
    public void DuringTheStopCallbacks_RefusesPagesAndHealthChecks()
    {
        // The host runs the stopping callbacks last registered first. Program registers the delete of the instance
        // file after the heartbeat was made, so the file is gone before the heartbeat hears of the stop.
        _lifetime.Start();
        bool? postponed = null;
        bool? connected = null;
        _lifetime.ApplicationStopping.Register(() =>
        {
            postponed = _heartbeat.TryPostpone();
            connected = _heartbeat.TryConnect() is not null;
        });

        _lifetime.StopApplication();

        Assert.False(postponed);
        Assert.False(connected);
    }

    [Fact]
    public void TimerCallbackThatComesAfterTheTimerWasCancelledOrReplaced_DoesNotStop()
    {
        // A timer callback can already be on its way when a page connects or a health check sets a new timer.
        var timers = new ManualTimers();
        using var lifetime = new FakeLifetime();
        using var heartbeat = new Heartbeat(lifetime, Times, timers);
        lifetime.Start();
        heartbeat.TryConnect()!.Dispose();
        Assert.True(heartbeat.TryPostpone());
        Assert.Equal(3, timers.Created.Count);

        timers.Created[0].Fire();
        timers.Created[1].Fire();
        Assert.Equal(0, lifetime.StopCalls);

        timers.Created[2].Fire();
        Assert.Equal(1, lifetime.StopCalls);
    }

    private IDisposable Connect() => _heartbeat.TryConnect() ?? throw new InvalidOperationException("The heartbeat refused the page.");

    /// <summary>A clock whose timers fire only when the test says, also after they were cancelled or replaced.</summary>
    private sealed class ManualTimers : TimeProvider
    {
        public List<ManualTimer> Created { get; } = [];

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(callback, state);
            Created.Add(timer);
            return timer;
        }
    }

    private sealed class ManualTimer(TimerCallback callback, object? state) : ITimer
    {
        public void Fire() => callback(state);

        public bool Change(TimeSpan dueTime, TimeSpan period) => true;

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>A host lifetime that the test starts. A stop raises <see cref="ApplicationStopping"/> at once, as the host does.</summary>
    private sealed class FakeLifetime : IHostApplicationLifetime, IDisposable
    {
        private readonly CancellationTokenSource _started = new();
        private readonly CancellationTokenSource _stopping = new();
        private readonly CancellationTokenSource _stopped = new();

        public CancellationToken ApplicationStarted => _started.Token;

        public CancellationToken ApplicationStopping => _stopping.Token;

        public CancellationToken ApplicationStopped => _stopped.Token;

        public int StopCalls { get; private set; }

        public void Start() => _started.Cancel();

        public void StopApplication()
        {
            StopCalls++;
            _stopping.Cancel();
        }

        public void Dispose()
        {
            _started.Dispose();
            _stopping.Dispose();
            _stopped.Dispose();
        }
    }
}

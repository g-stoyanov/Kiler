namespace Kiler.App;

/// <summary>The times of the heartbeat. The tests make them short (<see cref="StartOptions"/>).</summary>
internal sealed record HeartbeatTimes(TimeSpan FirstWait, TimeSpan IdleWait, TimeSpan PingInterval)
{
    public static HeartbeatTimes Default { get; } = new(TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(15));
}

/// <summary>
/// Stops Kiler when no Kiler page is open. Each open page holds a connection to <c>/api/events</c> (<see cref="EventsApi"/>).
/// <list type="bullet">
/// <item>After the start, Kiler waits <see cref="HeartbeatTimes.FirstWait"/> for the first page, while the browser starts.</item>
/// <item>When the last connection closes, it waits <see cref="HeartbeatTimes.IdleWait"/>, which covers a page reload. A new
/// connection cancels the wait.</item>
/// <item>A health check (<see cref="HealthApi"/>) puts the stop off until the first wait has passed from it, because a
/// second start asks it before it opens the browser.</item>
/// <item>Once the host stops, by the wait or by Ctrl+C, it accepts no connection and no health check.</item>
/// </list>
/// </summary>
internal sealed class Heartbeat : IDisposable
{
    private readonly Lock _lock = new();
    private readonly IHostApplicationLifetime _lifetime;
    private readonly HeartbeatTimes _times;
    private readonly TimeProvider _time;

    private bool _started;
    private bool _stopping;
    private int _connections;

    /// <summary>The time stamp of the last health check, if there was one.</summary>
    private long? _postponedAt;

    /// <summary>The timer of the stop. It is set only while the host runs and no connection is open.</summary>
    private ITimer? _timer;
    private long _timerSetAt;
    private TimeSpan _timerWait;

    /// <summary>Tells a timer that was replaced from the current one, in case its callback is already on the way.</summary>
    private int _timerVersion;

    public Heartbeat(IHostApplicationLifetime lifetime, HeartbeatTimes times, TimeProvider time)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(times.FirstWait, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(times.IdleWait, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(times.PingInterval, TimeSpan.Zero);
        _lifetime = lifetime;
        _times = times;
        _time = time;

        // Kestrel listens before the host reports the start, so a page can connect before the first wait begins.
        lifetime.ApplicationStarted.Register(Start);
        lifetime.ApplicationStopping.Register(OnStopping);
    }

    /// <summary>True when the heartbeat stopped Kiler, because no page was open.</summary>
    public bool TimedOut { get; private set; }

    /// <summary>
    /// The token counts as soon as the stop begins. The host runs the stopping callbacks in reverse order, so the delete
    /// of the instance file runs before <see cref="OnStopping"/>, and a health check in between must not get 200.
    /// </summary>
    private bool IsStopping => _stopping || _lifetime.ApplicationStopping.IsCancellationRequested;

    /// <summary>Counts an open page until the result is disposed; null once the host stops.</summary>
    public IDisposable? TryConnect()
    {
        lock (_lock)
        {
            if (IsStopping)
            {
                return null;
            }

            _connections++;
            CancelTimer();
            return new Connection(this);
        }
    }

    /// <summary>Puts the stop off for a browser that a second start opens; false once the host stops.</summary>
    public bool TryPostpone()
    {
        lock (_lock)
        {
            if (IsStopping)
            {
                return false;
            }

            _postponedAt = _time.GetTimestamp();
            // Only a stop that is due sooner is put off. While a page is open, the time stamp counts when it closes.
            if (_timer is not null && _timerWait - _time.GetElapsedTime(_timerSetAt) < _times.FirstWait)
            {
                SetTimer(_times.FirstWait);
            }

            return true;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _stopping = true;
            CancelTimer();
        }
    }

    private void Start()
    {
        lock (_lock)
        {
            if (_started || _stopping)
            {
                return;
            }

            _started = true;
            if (_connections == 0)
            {
                SetTimer(AtLeastPostponed(_times.FirstWait));
            }
        }
    }

    private void Disconnect()
    {
        lock (_lock)
        {
            _connections--;
            if (_connections == 0 && _started && !_stopping)
            {
                SetTimer(AtLeastPostponed(_times.IdleWait));
            }
        }
    }

    /// <summary>By the wait or by Ctrl+C.</summary>
    private void OnStopping()
    {
        lock (_lock)
        {
            _stopping = true;
            CancelTimer();
        }
    }

    private void OnTimer(int version)
    {
        lock (_lock)
        {
            if (version != _timerVersion || _timer is null)
            {
                return;
            }

            _stopping = true;
            TimedOut = true;
            CancelTimer();
        }

        // Outside the lock: the host runs the stopping callbacks, OnStopping among them, on this thread.
        _lifetime.StopApplication();
    }

    /// <summary>The wait, or what is left of the postponement by the last health check when that is longer.</summary>
    private TimeSpan AtLeastPostponed(TimeSpan wait)
    {
        if (_postponedAt is { } postponedAt)
        {
            var left = _times.FirstWait - _time.GetElapsedTime(postponedAt);
            if (left > wait)
            {
                return left;
            }
        }

        return wait;
    }

    private void SetTimer(TimeSpan wait)
    {
        CancelTimer();
        var version = ++_timerVersion;
        _timerSetAt = _time.GetTimestamp();
        _timerWait = wait;
        // The wait is never zero, so even a fake clock does not call back while the lock is held.
        _timer = _time.CreateTimer(_ => OnTimer(version), null, wait, Timeout.InfiniteTimeSpan);
    }

    private void CancelTimer()
    {
        _timer?.Dispose();
        _timer = null;
    }

    private sealed class Connection(Heartbeat heartbeat) : IDisposable
    {
        private int _closed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _closed, 1) == 0)
            {
                heartbeat.Disconnect();
            }
        }
    }
}

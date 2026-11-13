namespace Kiler.Core;

/// <summary>
/// The automatic locking (M4.5b, decided at the start of M4): the session locks after the minutes of <c>settings.json</c>
/// without activity, only of one whose seal verifies with the public key of the session (M4.15), and after
/// <see cref="DefaultMinutes"/> without one that fits and is sealed so (<see cref="MinutesOf"/>). The host calls
/// <see cref="Activity"/> for each request of a page but those that a page makes of its own again and again (the event stream,
/// the health check and the state of the data). The lock is <see cref="CurrentSession.Lock(Session)"/>, so the pages get the
/// <c>session</c> event as at the "Lock" button.
/// <para>
/// The timer runs from an activity until it locks. When it comes, it locks if the minutes have passed since the last activity,
/// and else waits for the rest, so a request sets no new timer. The minutes are read at each activity, at each sign-in and lock
/// (<see cref="CurrentSession.Changed"/>) and each time the timer comes: shorter minutes set the timer again at once, as the
/// first request of a page can come before the copy has opened or the wizard has written <c>settings.json</c> (code review),
/// and the request of a sign-in is an activity before its session, whose public key checks the seal, is in place (M4.15);
/// longer ones count when the timer comes.
/// </para>
/// </summary>
public sealed class AutoLock : IDisposable
{
    /// <summary>The minutes without a <c>settings.json</c> that fits (decided by the user in M4.5).</summary>
    public const int DefaultMinutes = 30;

    private readonly Lock _gate = new();
    private readonly CurrentSession _sessions;
    private readonly Func<TeamFiles?> _files;
    private readonly TimeProvider _time;

    private long _lastActivity;
    private ITimer? _timer;
    private long _timerSetAt;
    private TimeSpan _timerWait;

    /// <summary>Tells a timer that was replaced from the current one, in case its callback is already on the way.</summary>
    private int _timerVersion;
    private bool _disposed;

    /// <param name="sessions">The session of the process.</param>
    /// <param name="files">The files of the team as they are now, for the minutes of <c>settings.json</c>; null without data.</param>
    /// <param name="time">The clock.</param>
    public AutoLock(CurrentSession sessions, Func<TeamFiles?> files, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(time);
        _sessions = sessions;
        _files = files;
        _time = time;
        _sessions.Changed += OnSessionChanged;
    }

    /// <summary>
    /// The minutes of the automatic lock in <paramref name="files"/>: those of the newest <c>settings.json</c> whose seal
    /// verifies with <paramref name="adminPublicKey"/>, the public key of the session, on the share or in the local copy
    /// (<see cref="TeamFiles.SealedSettings"/>, M4.16a), else <see cref="DefaultMinutes"/> (M4.15). So whoever can write the
    /// share does not set the lock of every session to a day or to a minute (the M4.5b security review).
    /// </summary>
    public static int MinutesOf(TeamFiles? files, string? adminPublicKey) =>
        adminPublicKey is not null && files?.SealedSettings(adminPublicKey)?.Model is { } settings ? settings.AutoLockMinutes : DefaultMinutes;

    /// <summary>A request of a page: the lock is put off until the minutes have passed from now.</summary>
    public void Activity()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _lastActivity = _time.GetTimestamp();
            Arm();
        }
    }

    public void Dispose()
    {
        _sessions.Changed -= OnSessionChanged;
        lock (_gate)
        {
            _disposed = true;
            CancelTimer();
        }
    }

    /// <summary>
    /// A sign-in or a lock, on the thread of the change: the minutes of the session now in place count from the last activity
    /// (M4.15). Only a timer that runs is set again, as a sign-in is an activity and sets one; it does not throw (core.md).
    /// </summary>
    private void OnSessionChanged(object? sender, EventArgs e)
    {
        lock (_gate)
        {
            if (!_disposed && _timer is not null)
            {
                Arm();
            }
        }
    }

    /// <summary>
    /// Sets the timer for what is left of the minutes since the last activity, when none is set or when the one set comes
    /// later; at least a tick, so even a fake clock does not call back while the lock is held.
    /// </summary>
    private void Arm()
    {
        var left = Wait() - _time.GetElapsedTime(_lastActivity);
        if (_timer is null || _timerWait - _time.GetElapsedTime(_timerSetAt) > left)
        {
            SetTimer(left > TimeSpan.Zero ? left : TimeSpan.FromTicks(1));
        }
    }

    private void OnTimer(int version)
    {
        Session? idle;
        lock (_gate)
        {
            if (version != _timerVersion || _timer is null)
            {
                return;
            }

            var left = Wait() - _time.GetElapsedTime(_lastActivity);
            if (left > TimeSpan.Zero)
            {
                SetTimer(left);
                return;
            }

            CancelTimer();
            // Read under the lock: a sign-in is an activity before its session is in place, so a session that a sign-in puts in
            // place from here on is not this one, and CurrentSession.Lock(idle) leaves it.
            idle = _sessions.Session;
        }

        // Outside the lock: the keys wait for a use under way, and the requests must not wait for them in Activity.
        if (idle is not null)
        {
            _sessions.Lock(idle);
        }
    }

    /// <summary>
    /// The minutes of the session in place, whose public key checks the seal of <c>settings.json</c>; without a session the
    /// default, as the timer locks nothing then, and the next activity or the timer reads them again (the remarks above).
    /// </summary>
    private TimeSpan Wait() => TimeSpan.FromMinutes(MinutesOf(_files(), _sessions.Session?.AdminPublicKey));

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
}

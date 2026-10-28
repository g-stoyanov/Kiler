namespace Kiler.Storage;

/// <summary>
/// A back-off of the load (M2.8b, decided by the user): how many times in a row something was tried and gave nothing new, and
/// when it was tried last, on the clock of the copy. The next try waits <see cref="First"/> after the first time, twice as
/// long after each time after it, and at most <see cref="Longest"/>; a clock put back tries again at once. It lives in memory
/// only, so a start tries everything again. The default is no time yet, and is always due.
/// </summary>
internal readonly record struct BackOff(int Times, DateTimeOffset At)
{
    /// <summary>The wait after the first time: 20 s, two passes of the watch.</summary>
    public static readonly TimeSpan First = TimeSpan.FromSeconds(20);

    /// <summary>The longest wait: 5 minutes.</summary>
    public static readonly TimeSpan Longest = TimeSpan.FromMinutes(5);

    /// <summary>One time more, tried at <paramref name="now"/>.</summary>
    public BackOff Again(DateTimeOffset now) => new(Times + 1, now);

    /// <summary>Whether the next try is due at <paramref name="now"/>.</summary>
    public bool IsDue(DateTimeOffset now) => now < At || now - At >= Wait;

    /// <summary>How long the next try waits after the last one.</summary>
    private TimeSpan Wait
    {
        get
        {
            if (Times <= 0)
            {
                return TimeSpan.Zero;
            }

            var wait = First;
            for (var time = 1; time < Times && wait < Longest; time++)
            {
                wait *= 2;
            }

            return wait < Longest ? wait : Longest;
        }
    }
}

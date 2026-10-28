namespace Kiler.Storage;

/// <summary>
/// The watching of the share (M2.8, PLAN M2 step 7): a load of the local copy (<see cref="DataCopy.Load"/>) at the start, and
/// then <see cref="Interval"/> after the end of each load on the clock that the watch is given, on a thread of its own, as a
/// load blocks on the network: a read can take about 60 s over SMB (M2.7). Each load goes to the upper layers as one event in
/// the process (<see cref="Loaded"/>), and a load that throws as another (<see cref="Failed"/>), decided by the user. Both
/// are raised on the thread of the watch, after the load has let go of the copy, so a handler may call the copy; the next
/// load is due before they are raised. A handler that throws ends the process, as on any thread of its own, so the upper
/// layers handle their own errors. The writes of the upper layers go through the copy, which keeps them apart from the loads.
/// A watch starts once. Its stop does not wait for a load under way beyond the limit that the caller gives, as a load takes
/// no cancellation (M2.3): the load then ends on its own, on a background thread, and gives no event.
/// </summary>
public sealed class DataWatch : IDisposable
{
    /// <summary>How long after the end of a load the next one starts (PLAN M2 step 7): 10 s.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);

    private readonly DataCopy _copy;
    private readonly TimeProvider _time;
    private readonly Thread _thread;

    /// <summary>Kept for the state below, which the thread of the watch waits on between two loads.</summary>
    private readonly object _gate = new();
    private bool _started;
    private bool _stopping;

    /// <summary>Whether the timer has said that the next load is due.</summary>
    private bool _due;

    public DataWatch(DataCopy copy, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(copy);
        ArgumentNullException.ThrowIfNull(time);
        _copy = copy;
        _time = time;
        _thread = new Thread(Run) { IsBackground = true, Name = "Kiler watch" };
    }

    /// <summary>What each load gave, raised on the thread of the watch once the load has let go of the copy.</summary>
    public event EventHandler<LoadResult>? Loaded;

    /// <summary>
    /// A load that threw, raised as <see cref="Loaded"/> is; the next load is tried after <see cref="Interval"/>. What the
    /// load took before it is in the copy (<see cref="DataCopy.Load"/>) and is given by no later load, so a handler reads the
    /// copy again (<see cref="DataCopy.Documents"/>, <see cref="DataCopy.Trash"/>).
    /// </summary>
    public event EventHandler<Exception>? Failed;

    /// <summary>
    /// Starts the watch: the first load runs at once, on the thread of the watch. A watch that has started or stopped is an
    /// <see cref="InvalidOperationException"/>.
    /// </summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_started || _stopping)
            {
                throw new InvalidOperationException("A watch starts once.");
            }

            _started = true;
            _thread.Start();
        }
    }

    /// <summary>
    /// Stops the watch: no load comes due after it, and a load under way, or one that has just come due, gives no event; an
    /// event that is being raised runs to its end. Waits at most <paramref name="wait"/> for the thread of the watch, and
    /// gives whether it has ended; one that has not ends with its load. Called from a handler of the watch, it does not wait,
    /// and gives <c>false</c>: the thread ends once the handler returns.
    /// </summary>
    public bool Stop(TimeSpan wait)
    {
        lock (_gate)
        {
            _stopping = true;
            Monitor.PulseAll(_gate);
            if (!_started)
            {
                return true;
            }
        }

        return Thread.CurrentThread != _thread && _thread.Join(wait);
    }

    /// <summary>Stops the watch without waiting for a load under way (<see cref="Stop"/>).</summary>
    public void Dispose() => Stop(TimeSpan.Zero);

    private void Run()
    {
        while (true)
        {
            LoadResult? load = null;
            Exception? failure = null;
            try
            {
                load = _copy.Load();
            }
            catch (Exception ex)
            {
                failure = ex;
            }

            using var timer = _time.CreateTimer(_ => Due(), null, Interval, Timeout.InfiniteTimeSpan);
            lock (_gate)
            {
                if (_stopping)
                {
                    return;
                }
            }

            if (load is not null)
            {
                Loaded?.Invoke(this, load);
            }
            else if (failure is not null)
            {
                Failed?.Invoke(this, failure);
            }

            lock (_gate)
            {
                while (!_due && !_stopping)
                {
                    Monitor.Wait(_gate);
                }

                if (_stopping)
                {
                    return;
                }

                _due = false;
            }
        }
    }

    private void Due()
    {
        lock (_gate)
        {
            _due = true;
            Monitor.PulseAll(_gate);
        }
    }
}

using System.Globalization;
using Kiler.Storage;

namespace Kiler.App;

/// <summary>
/// The data of a running Kiler (M2.10): the data folder of the start (<see cref="DataPlace"/>), its local copy in the profile,
/// and the watch of the share (<see cref="DataWatch"/>). It opens once the server listens, on a task of its own, as the first
/// look at a share that cannot be reached can block for long (M2.8a) and the page should not wait for it; the watch stops on
/// <c>ApplicationStopping</c> (<see cref="KilerHost"/>), and the copy is let go with <see cref="Dispose"/>, after the server
/// has stopped. Kiler runs without data, and says so, when the data folder does not exist (nothing makes it; the wizard of M4
/// does), when another Kiler of the user has the local copy open, or when the index of the copy cannot be read now; the
/// screens are M4 and M6. What it says goes to the console as lines, at once (decided by the user): what the user acts on to
/// the errors.
/// </summary>
internal sealed class KilerData : IDisposable
{
    /// <summary>
    /// The file in the folder of the local copy that a Kiler holds without sharing while it has the copy open (decided by the
    /// user): two Kilers of one user on one data folder would overwrite each other's index, and the open of one deletes the
    /// young files of the other after 10 minutes (M2.7). The file stays when it is let go.
    /// </summary>
    public const string LockFile = "lock";

    /// <summary><c>ERROR_SHARING_VIOLATION</c>: another handle holds the file without sharing.</summary>
    private const int SharingViolation = 32;

    private readonly DataPlace _place;
    private readonly IFileOwner _owners;
    private readonly TimeProvider _time;
    private readonly TextWriter _output;
    private readonly TextWriter _errors;
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Kept for the state below.</summary>
    private readonly Lock _gate = new();
    private bool _starting;

    /// <summary>Whether the open runs: meanwhile it, not <see cref="Dispose"/>, lets the copy go, so the copy is never let go under <see cref="DataCopy.Open"/>.</summary>
    private bool _opening;
    private bool _disposed;

    /// <summary>
    /// Whether the watch had ended at the last stop; null before one. A load that outlives the stop still writes the copy, so
    /// the copy stays locked until the process ends (security review).
    /// </summary>
    private bool? _watchEnded;
    private FileStream? _lock;
    private DataCopy? _copy;
    private DataWatch? _watch;

    /// <summary>
    /// Whether Kiler stops; read without the lock, so a line never waits for it: a write to the console can block, as while
    /// the user selects text in it.
    /// </summary>
    private volatile bool _stopping;

    private long _startedAt;

    // Only the thread of the watch uses these.
    private bool _heard;
    private bool _saidLoad;
    private int _saidSkipped;

    public KilerData(DataPlace place, IFileOwner owners, TimeProvider time, TextWriter output, TextWriter errors)
    {
        ArgumentNullException.ThrowIfNull(place);
        ArgumentNullException.ThrowIfNull(owners);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(errors);
        _place = place;
        _owners = owners;
        _time = time;
        _output = output;
        _errors = errors;
    }

    /// <summary>The local copy once it is open and watched; null before, and when Kiler runs without data. M4 reads and writes through it.</summary>
    public DataCopy? Copy
    {
        get
        {
            lock (_gate)
            {
                return _copy;
            }
        }
    }

    /// <summary>
    /// Completes when the start of the data has ended: the first load of the watch has ended, offline or failed too, or Kiler
    /// runs without data, or stops before. It never fails.
    /// </summary>
    public Task Started => _started.Task;

    /// <summary>Opens the data on a task of its own; once, when the server listens.</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_starting)
            {
                throw new InvalidOperationException("The data starts once.");
            }

            _starting = true;
            _opening = true;
        }

        _startedAt = _time.GetTimestamp();
        _ = Task.Run(Open);
    }

    /// <summary>
    /// Stops the watch, waiting at most <paramref name="wait"/> for a load under way, which takes no cancellation (M2.3); one
    /// that is still under way ends on its own and says nothing. An open under way stops before the watch, and says nothing
    /// more. <see cref="Started"/> completes. The copy stays locked until <see cref="Dispose"/>, as requests can still run
    /// until the server has stopped.
    /// </summary>
    public void Stop(TimeSpan wait)
    {
        DataWatch? watch;
        lock (_gate)
        {
            _stopping = true;
            watch = _watch;
        }

        _started.TrySetResult();

        // Without a watch now, none starts: the open starts it only before a stop.
        var ended = watch?.Stop(wait) ?? true;
        lock (_gate)
        {
            _watchEnded = ended;
        }
    }

    /// <summary>
    /// Stops the watch, and lets the local copy go once no load uses it: an open under way lets it go when it ends, and a load
    /// that outlived the stop keeps it until the process ends, as it still writes the copy (security review). Without a stop
    /// before it, it waits for the watch as the stop of the host does.
    /// </summary>
    public void Dispose()
    {
        bool stopped;
        lock (_gate)
        {
            stopped = _watchEnded is not null;
        }

        Stop(stopped ? TimeSpan.Zero : KilerHost.ShutdownTimeout);
        lock (_gate)
        {
            _disposed = true;
            if (!_opening && _watchEnded == true)
            {
                LetGo();
            }
        }
    }

    private static bool IsSharingViolation(IOException ex) => (ex.HResult & 0xFFFF) == SharingViolation;

    private void Open()
    {
        var watching = false;
        try
        {
            watching = OpenAndWatch();
        }
        catch (Exception ex)
        {
            // A fault of the code: this task has no one to throw to.
            SayError($"Kiler runs without data: its data could not be opened: {ex}");
        }
        finally
        {
            lock (_gate)
            {
                _opening = false;
                if (!watching || (_disposed && _watchEnded == true))
                {
                    LetGo();
                }
            }

            if (!watching)
            {
                _started.TrySetResult();
            }
        }
    }

    /// <summary>Lets the local copy go; called under the lock.</summary>
    private void LetGo()
    {
        _lock?.Dispose();
        _lock = null;
    }

    /// <summary>Opens the data folder and its local copy, and starts the watch; false when Kiler runs without data, which it has said, or stops.</summary>
    private bool OpenAndWatch()
    {
        DataFolder share;
        try
        {
            share = DataFolder.Open(_place.DataFolder, _owners);
        }
        catch (DirectoryNotFoundException)
        {
            SayError($"Kiler runs without data: the data folder does not exist: {_place.DataFolder}");
            return false;
        }
        catch (IOException ex)
        {
            // A link or a file at its name.
            SayError($"Kiler runs without data: the data folder cannot be used: {ex.Message}");
            return false;
        }

        if (_stopping || !LockCopy() || _stopping)
        {
            return false;
        }

        DataCopy copy;
        try
        {
            // No account until M4 decides how an account becomes a folder name (M2.7b).
            copy = DataCopy.Open(share, _place.CopyFolder, _time);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Taken for no index, it would cost the last good bytes of the documents that the share no longer has (M2.7).
            SayError($"Kiler runs without data: the index of its local copy cannot be read now, so start Kiler again: {ex.Message}");
            return false;
        }

        var watch = new DataWatch(copy, _time);
        watch.Loaded += OnLoaded;
        watch.Failed += OnFailed;
        watch.OfflineChanged += OnOfflineChanged;
        Say($"Kiler opens the data folder {_place.DataFolder}, with its local copy in {_place.CopyFolder}.");
        lock (_gate)
        {
            if (_stopping)
            {
                return false;
            }

            _copy = copy;
            _watch = watch;
            watch.Start();
            return true;
        }
    }

    /// <summary>Makes the folder of the local copy, each level private, and locks it; false when it cannot, which it has said.</summary>
    private bool LockCopy()
    {
        FileStream held;
        try
        {
            PrivateFolder.Create(_place.LocalFolder);
            PrivateFolder.Create(_place.CopiesFolder);
            PrivateFolder.Create(_place.CopyFolder);
            held = new FileStream(Path.Combine(_place.CopyFolder, LockFile), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException ex) when (IsSharingViolation(ex))
        {
            SayError("Kiler runs without data: another Kiler of this user has the same data folder open, in another session, or one started with --data and one without.");
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SayError($"Kiler runs without data: its local copy cannot be made or locked: {ex.Message}");
            return false;
        }

        // Let go when the open ends, if it stops or the data is disposed meanwhile.
        lock (_gate)
        {
            _lock = held;
        }

        return true;
    }

    // Each handler says its line before the start ends, so whoever waits for it finds the line.
    private void OnLoaded(object? sender, LoadResult load)
    {
        var first = !_heard;
        _heard = true;

        // A load that changed nothing is not said, and neither are the skipped entries that every load repeats (M2.8).
        var skipped = load.Skipped.Count + load.MoreSkipped;
        if (!_saidLoad || load.Changed.Count > 0 || load.Gone.Count > 0 || load.Outside.Count > 0 || load.TrashChanged || skipped != _saidSkipped)
        {
            _saidLoad = true;
            _saidSkipped = skipped;
            var line = string.Create(
                CultureInfo.InvariantCulture,
                $"Loaded the data folder: {load.Changed.Count} new or changed, {load.Gone.Count} gone, {load.Outside.Count} changed outside Kiler, {skipped} skipped");
            if (load.TrashChanged)
            {
                line += ", the trash changed";
            }

            // The time of the first load counts the open of the data folder and of the local copy too.
            Say(first ? string.Create(CultureInfo.InvariantCulture, $"{line}, in {_time.GetElapsedTime(_startedAt).TotalSeconds:0.0} s.") : $"{line}.");
        }

        _started.TrySetResult();
    }

    private void OnFailed(object? sender, Exception failure)
    {
        _heard = true;

        // A fault of the code, so the whole exception, with where it was thrown. The screens that read the documents again
        // after it come with M4 and M6.
        SayError($"A load of the data folder failed: {failure}");
        _started.TrySetResult();
    }

    private void OnOfflineChanged(object? sender, OfflineChange change)
    {
        _heard = true;
        Say(change.Offline
            ? $"The data folder cannot be reached, so Kiler works from its local copy: {change.Cause?.Message}"
            : "The data folder can be reached again.");
        _started.TrySetResult();
    }

    private void Say(string line) => Write(_output, line);

    private void SayError(string line) => Write(_errors, line);

    /// <summary>Writes a line, unless Kiler stops, so no line comes after its last one. A handler of the watch never throws (M2.8).</summary>
    private void Write(TextWriter writer, string line)
    {
        if (_stopping)
        {
            return;
        }

        try
        {
            writer.WriteLine(line);
        }
        catch (IOException)
        {
            // A console that has gone: the line is lost, and the watch goes on.
        }
    }
}

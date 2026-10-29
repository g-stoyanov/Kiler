using System.Globalization;
using System.Text.Json.Serialization;
using Kiler.Core;
using Kiler.Storage;

namespace Kiler.App;

/// <summary>
/// The data of a running Kiler (M2.10): the data folder of the start (<see cref="DataPlace"/>), its local copy in the profile,
/// the watch of the share (<see cref="DataWatch"/>), and the team that follows it (<see cref="Core.Team"/>, M4.2). It opens
/// once the server listens, on a task of its own, as the first look at a share that cannot be reached can block for long
/// (M2.8a) and the page should not wait for it; the watch stops on <c>ApplicationStopping</c> (<see cref="KilerHost"/>), and
/// the copy is let go with <see cref="Dispose"/>, after the server has stopped. It is always in DI (M4.2), also when no data
/// folder was chosen, and gives its <see cref="State"/> to the page (<see cref="DataApi"/>). Kiler runs without data, and
/// says so, when the data folder does not exist (nothing makes it at the start; the wizard does, M4.3) or cannot be used, when another
/// Kiler of the user has the local copy open, or when the index of the copy cannot be read now; without a data folder chosen,
/// <c>Program</c> says it. What it says goes to the console as lines, at once (decided by the user): what the user acts on to
/// the errors.
/// </summary>
internal sealed partial class KilerData : IDisposable
{
    /// <summary>
    /// The file in the folder of the local copy that a Kiler holds without sharing while it has the copy open (decided by the
    /// user): two Kilers of one user on one data folder would overwrite each other's index, and the open of one deletes the
    /// young files of the other after 10 minutes (M2.7). The file stays when it is let go.
    /// </summary>
    public const string LockFile = "lock";

    /// <summary><c>ERROR_SHARING_VIOLATION</c>: another handle holds the file without sharing.</summary>
    private const int SharingViolation = 32;

    private readonly DataPlace? _place;
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
    private Team? _team;
    private DataWatch? _watch;
    private DataState _state;

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

    /// <summary>The data of the start at <paramref name="place"/>; null when no data folder was chosen, so Kiler runs without data.</summary>
    public KilerData(DataPlace? place, IFileOwner owners, TimeProvider time, TextWriter output, TextWriter errors)
    {
        ArgumentNullException.ThrowIfNull(owners);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(errors);
        _place = place;
        _owners = owners;
        _time = time;
        _output = output;
        _errors = errors;
        _state = place is null ? DataState.NoFolder : DataState.Opening;
    }

    /// <summary>The full path of the data folder of the start; null when none was chosen.</summary>
    public string? Folder => _place?.DataFolder;

    /// <summary>The state of the data, which the page shows (decided at the start of M4).</summary>
    public DataState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
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

    /// <summary>The levels, the settings and the users of the local copy, with it; null when <see cref="Copy"/> is.</summary>
    public Team? Team
    {
        get
        {
            lock (_gate)
            {
                return _team;
            }
        }
    }

    /// <summary>
    /// Completes when the start of the data has ended: the first load of the watch has ended, offline or failed too, or Kiler
    /// runs without data, or stops before. It never fails.
    /// </summary>
    public Task Started => _started.Task;

    /// <summary>Without a data folder (<see cref="DataState.NoFolder"/>): what <see cref="KilerHost"/> takes when it is given no data, as the API tests are.</summary>
    public static KilerData WithoutFolder() => new(null, FileOwner.Instance, TimeProvider.System, TextWriter.Null, TextWriter.Null);

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
            _opening = _place is not null;
        }

        if (_place is null)
        {
            _started.TrySetResult();
            return;
        }

        _startedAt = _time.GetTimestamp();
        _ = Task.Run(() => Open(_place));
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

    /// <summary>The open of the start, and of the wizard, which gives <paramref name="create"/> (<see cref="OpenAndWatch"/>).</summary>
    private void Open(DataPlace place, Action<DataCopy>? create = null)
    {
        var watching = false;
        try
        {
            watching = OpenAndWatch(place, create);
        }
        catch (Exception ex)
        {
            // A fault of the code: this task has no one to throw to.
            RunWithout(DataState.Failed, $"Kiler runs without data: its data could not be opened: {ex}");
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

    /// <summary>
    /// Opens the data folder and its local copy, reads the team from the copy, and starts the watch; false when Kiler runs
    /// without data, which it has said, or stops. The state stays <see cref="DataState.Opening"/> until the first load has
    /// ended, as a new local copy has nothing before it. With <paramref name="create"/>, the wizard's (M4.3), it makes the
    /// data folder new first, and gives it the copy before the team reads it; a data folder that it cannot make, which it
    /// has said, opens as at the start, and <paramref name="create"/> does not run.
    /// </summary>
    private bool OpenAndWatch(DataPlace place, Action<DataCopy>? create)
    {
        var made = create is null ? null : MakeFolder(place);
        DataFolder share;
        try
        {
            share = made ?? DataFolder.Open(place.DataFolder, _owners);
        }
        catch (DirectoryNotFoundException)
        {
            return RunWithout(DataState.Missing, $"Kiler runs without data: the data folder does not exist: {place.DataFolder}");
        }
        catch (IOException ex)
        {
            // A link or a file at its name.
            return RunWithout(DataState.Unusable, $"Kiler runs without data: the data folder cannot be used: {ex.Message}");
        }

        if (_stopping || !LockCopy(place) || _stopping)
        {
            return false;
        }

        DataCopy copy;
        try
        {
            // No account until M4 decides how an account becomes a folder name (M2.7b).
            copy = DataCopy.Open(share, place.CopyFolder, _time);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Taken for no index, it would cost the last good bytes of the documents that the share no longer has (M2.7).
            return RunWithout(DataState.IndexUnreadable, $"Kiler runs without data: the index of its local copy cannot be read now, so start Kiler again: {ex.Message}");
        }

        if (made is not null && create is not null)
        {
            create(copy);
        }

        var team = new Team(copy);
        var watch = new DataWatch(copy, _time);
        watch.Loaded += (_, load) => OnLoaded(team, load);
        watch.Failed += (_, failure) => OnFailed(copy, team, failure);
        watch.OfflineChanged += (_, change) => OnOfflineChanged(team, change);
        Say($"Kiler opens the data folder {place.DataFolder}, with its local copy in {place.CopyFolder}.");
        lock (_gate)
        {
            if (_stopping)
            {
                return false;
            }

            _copy = copy;
            _team = team;
            _watch = watch;
            watch.Start();
            return true;
        }
    }

    /// <summary>
    /// Makes the data folder new for the wizard (M4.3), or takes it when it holds nothing but the empty folders of the layout
    /// (<see cref="DataFolder.Create"/>, decided at the start of M4); null when it cannot, as when it holds more by now or
    /// cannot be written, which it says.
    /// </summary>
    private DataFolder? MakeFolder(DataPlace place)
    {
        try
        {
            return DataFolder.Create(place.DataFolder, _owners);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SayError($"The wizard cannot make a new Kiler in the data folder: {ex.Message}");
            return null;
        }
    }

    /// <summary>Makes the folder of the local copy, each level private, and locks it; false when it cannot, which it has said.</summary>
    private bool LockCopy(DataPlace place)
    {
        FileStream held;
        try
        {
            PrivateFolder.Create(place.LocalFolder);
            PrivateFolder.Create(place.CopiesFolder);
            PrivateFolder.Create(place.CopyFolder);
            held = new FileStream(Path.Combine(place.CopyFolder, LockFile), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException ex) when (IsSharingViolation(ex))
        {
            return RunWithout(DataState.InUse, "Kiler runs without data: another Kiler of this user has the same data folder open, in another session, or one started with --data and one without.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return RunWithout(DataState.Failed, $"Kiler runs without data: its local copy cannot be made or locked: {ex.Message}");
        }

        // Let go when the open ends, if it stops or the data is disposed meanwhile.
        lock (_gate)
        {
            _lock = held;
        }

        return true;
    }

    // Each handler takes the change into the team, then says its line and the state, before the start ends, so whoever waits
    // for it finds them.
    private void OnLoaded(Team team, LoadResult load)
    {
        var first = !_heard;
        _heard = true;
        Update(() => team.Follow(load));

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

        // A load that ends is not offline: the watch says that the copy is back before it.
        SetState(DataState.Open);
        _started.TrySetResult();
    }

    private void OnFailed(DataCopy copy, Team team, Exception failure)
    {
        _heard = true;

        // What the load took before it threw is in the copy, and no later load gives it (core.md).
        Update(team.Reload);

        // A fault of the code, so the whole exception, with where it was thrown.
        SayError($"A load of the data folder failed: {failure}");
        SetState(copy.Offline ? DataState.Offline : DataState.Open);
        _started.TrySetResult();
    }

    private void OnOfflineChanged(Team team, OfflineChange change)
    {
        _heard = true;

        // As after a failed load: a load that went offline may have taken documents first (core.md).
        Update(team.Reload);
        Say(change.Offline
            ? $"The data folder cannot be reached, so Kiler works from its local copy: {change.Cause?.Message}"
            : "The data folder can be reached again.");
        SetState(change.Offline ? DataState.Offline : DataState.Open);
        _started.TrySetResult();
    }

    /// <summary>Runs an update of the team on the thread of the watch, whose handlers never throw (M2.8): a fault of the code is said, and the watch goes on.</summary>
    private void Update(Action update)
    {
        try
        {
            update();
        }
        catch (Exception ex)
        {
            SayError($"The levels, the settings and the users could not be read from the local copy: {ex}");
        }
    }

    private void SetState(DataState state)
    {
        lock (_gate)
        {
            _state = state;
        }
    }

    /// <summary>Sets a state without data and says why; false, for the open to return.</summary>
    private bool RunWithout(DataState state, string line)
    {
        SetState(state);
        SayError(line);
        return false;
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

/// <summary>
/// The state of the data of a running Kiler (decided at the start of M4), which the page shows (<see cref="DataApi"/>).
/// Each state without data has its line on the console too, with what the page does not show, such as an exception.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<DataState>))]
internal enum DataState
{
    /// <summary>No data folder was chosen: Kiler has never been started from the shared folder, and there is no <c>--data</c>.</summary>
    [JsonStringEnumMemberName("noFolder")]
    NoFolder,

    /// <summary>
    /// The data folder and its local copy are opening, until the first load has ended. A start whose file server does not
    /// answer stays here about 37 s before the copy opens (M2.11b).
    /// </summary>
    [JsonStringEnumMemberName("opening")]
    Opening,

    /// <summary>The data folder does not exist: the wizard makes a new Kiler there (M4.3).</summary>
    [JsonStringEnumMemberName("missing")]
    Missing,

    /// <summary>The data folder cannot be used: a link, or a file at its name.</summary>
    [JsonStringEnumMemberName("unusable")]
    Unusable,

    /// <summary>Another Kiler of the user has the local copy of the data folder open.</summary>
    [JsonStringEnumMemberName("inUse")]
    InUse,

    /// <summary>The index of the local copy cannot be read now, so Kiler is started again.</summary>
    [JsonStringEnumMemberName("indexUnreadable")]
    IndexUnreadable,

    /// <summary>The local copy cannot be made or locked, or a fault of the code; the console says which.</summary>
    [JsonStringEnumMemberName("failed")]
    Failed,

    /// <summary>The local copy is open, and its last load reached the data folder.</summary>
    [JsonStringEnumMemberName("open")]
    Open,

    /// <summary>The local copy is open, but the data folder cannot be reached: Kiler works from the copy, and the writes wait (core.md).</summary>
    [JsonStringEnumMemberName("offline")]
    Offline,
}

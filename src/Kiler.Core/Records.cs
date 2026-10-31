using System.Collections.Immutable;
using Kiler.Storage;

namespace Kiler.Core;

/// <summary>
/// The stashes and the shared records of the local copy, in memory (M5.3, design 5.3): every stash, for every reader, and
/// each record as one session reads it (<see cref="RecordView"/>), the groups of its keys decrypted; since M5.10 the records
/// of the trash too. What it has is one <see cref="RecordSet"/>, never changed, which each update replaces whole, as
/// <see cref="Team"/> does. It follows each load of the watch after <see cref="Team.Follow"/> (<see cref="Follow"/>), whose
/// templates it reads, and reads the copy again after <see cref="Team.Reload"/> (<see cref="Reload"/>), both on the thread of
/// the watch. It follows <see cref="CurrentSession.Changed"/>: a sign-in decrypts for the new session, and a lock drops what
/// was decrypted. The updates keep a lock of their own. A write of this Kiler goes through it (<see cref="Create(RecordFile)"/>,
/// M5.4; <see cref="Replace"/>, M5.8; <see cref="Delete"/> and <see cref="RestoreFromTrash"/>, M5.10), as no load gives it.
/// The private records come after the exam, so it skips the private folders.
/// </summary>
public sealed class Records : IDisposable
{
    private readonly DataCopy _copy;
    private readonly Team _team;
    private readonly CurrentSession _current;
    private readonly Lock _gate = new();
    private volatile RecordSet _set;

    /// <summary>The templates of the last update, kept under the lock, so a load that changes one reads its records again.</summary>
    private ImmutableSortedDictionary<string, TeamFile<TemplateFile>> _templates;

    /// <summary>
    /// The versions that this Kiler wrote, deleted or restored (<see cref="TakeWritten(DocumentKey, WriteResult, Func{RecordSet, int, RecordSet?})"/>)
    /// since the set last followed a load, by key; kept under the lock, as <see cref="Team"/> keeps its own (core.md, "The
    /// team"): <see cref="Follow"/> takes no older version of these from a load that ended before the write, such as the
    /// version that an edit replaced (M5.8), and of a delete not the version deleted either (M5.10, <see cref="Wrote.Hides"/>).
    /// </summary>
    private readonly Dictionary<DocumentKey, Wrote> _written = [];

    /// <summary>
    /// Reads the stashes and the records, of their folder and of the trash, from the documents of <paramref name="copy"/>, as
    /// the last load left them, for a guest: a session gets its own at its first <see cref="Read"/> or at the next
    /// <see cref="CurrentSession.Changed"/>.
    /// </summary>
    public Records(DataCopy copy, Team team, CurrentSession current)
    {
        ArgumentNullException.ThrowIfNull(copy);
        ArgumentNullException.ThrowIfNull(team);
        ArgumentNullException.ThrowIfNull(current);
        _copy = copy;
        _team = team;
        _current = current;
        _templates = team.Files.Templates;
        _set = ReadAll(copy.Documents(), copy.Trash(), _templates, null);
        current.Changed += OnSessionChanged;
    }

    /// <summary>The set there now, for the tests: a sign-in and a lock replace it before any request.</summary>
    internal RecordSet Set => _set;

    /// <summary>
    /// The set that <paramref name="session"/> reads, or a guest when it is null: the one there when it is for that session,
    /// else one read now for it. A request reads it once, with the session that it read once (core.md, "Keys and the session").
    /// </summary>
    /// <exception cref="SessionLockedException"><paramref name="session"/> is locked.</exception>
    public RecordSet Read(Session? session)
    {
        if (session is { IsLocked: true })
        {
            throw new SessionLockedException();
        }

        var set = _set;
        if (set.Session == session)
        {
            return set;
        }

        lock (_gate)
        {
            if (_set.Session != session)
            {
                _set = WithSession(_set, session);
            }

            return _set;
        }
    }

    /// <summary>
    /// Takes what a load changed of the stashes and the records: a document new, changed, damaged or gone; and reads again the
    /// records whose template the load changed. When the load saw the trash change (<see cref="LoadResult.TrashChanged"/>), it
    /// reads the records of the trash again from the copy, which has them as they are now (M5.10). <see cref="Team.Follow"/>
    /// takes the same load first.
    /// </summary>
    public void Follow(LoadResult load)
    {
        ArgumentNullException.ThrowIfNull(load);
        lock (_gate)
        {
            var set = _set;
            var templates = _team.Files.Templates;
            var stashes = set.Stashes;
            var records = set.Records;
            var trash = load.TrashChanged ? ReadTrash(_copy.Trash(), templates, set.Session) : set.Trash;
            foreach (var key in load.Gone)
            {
                if (IsIn(key, DataFolder.Stashes))
                {
                    stashes = stashes.Remove(key.Id);
                }
                else if (IsIn(key, DataFolder.Records))
                {
                    records = records.Remove(key.Id);
                }
            }

            if (templates != _templates)
            {
                Func<RecordView, bool> changed = view => view.File.Model is { } record && templates.GetValueOrDefault(record.Template) != view.TemplateEntry;
                records = ReadAgain(records, templates, set.Session, changed);
                trash = ReadAgain(trash, templates, set.Session, changed);
                _templates = templates;
            }

            foreach (var loaded in load.Changed)
            {
                // A load that ended before a write of this Kiler: its older version does not take the place of the write.
                if (_written.TryGetValue(loaded.Key, out var wrote) && wrote.Hides(loaded.Read.Document?.Version, trash.ContainsKey(loaded.Key.Id)))
                {
                    continue;
                }

                if (IsIn(loaded.Key, DataFolder.Stashes))
                {
                    stashes = stashes.SetItem(loaded.Key.Id, TeamFile<StashFile>.Of(loaded.Read, StashFile.TryRead));
                }
                else if (IsIn(loaded.Key, DataFolder.Records))
                {
                    records = records.SetItem(loaded.Key.Id, RecordView.Read(TeamFile<RecordFile>.Of(loaded.Read, RecordFile.TryRead), templates, set.Session));
                }
            }

            _written.Clear();
            if (stashes != set.Stashes || records != set.Records || trash != set.Trash)
            {
                _set = new RecordSet(set.Session, stashes, records, trash);
            }
        }
    }

    /// <summary>
    /// Creates <paramref name="record"/> through the copy (M5.4) and, when it is written, takes it into the set at its version,
    /// read for the session of the set: the copy takes what this Kiler wrote as its own, so no load gives it (core.md, "The
    /// team"). A newer version, which a load has brought in meanwhile, stays. The write runs without the lock, as it can wait
    /// for a load under way. A load that ended before the write cannot give the record, which was not there.
    /// </summary>
    /// <exception cref="ArgumentException">The record is larger than a document can be (<see cref="DocumentFormat.MaxSize"/>).</exception>
    /// <exception cref="IOException">The share could not be reached before a load said so (core.md, "Calling the layer").</exception>
    /// <exception cref="UnauthorizedAccessException">The share refused the write before a load said so.</exception>
    internal WriteResult Create(RecordFile record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return TakeWritten(record, _copy.Create(record.Key, record.ToContent()));
    }

    /// <summary>
    /// Replaces the version <paramref name="version"/> of a record through the copy with <paramref name="record"/> (M5.8), and
    /// takes it into the set as <see cref="Create(RecordFile)"/> does. A load that ended before the write can give the version
    /// that it replaced: <see cref="Follow"/> does not take it over the write.
    /// </summary>
    /// <exception cref="ArgumentException">The record is larger than a document can be (<see cref="DocumentFormat.MaxSize"/>).</exception>
    /// <exception cref="IOException">The share could not be reached before a load said so (core.md, "Calling the layer").</exception>
    /// <exception cref="UnauthorizedAccessException">The share refused the write before a load said so.</exception>
    internal WriteResult Replace(RecordFile record, int version)
    {
        ArgumentNullException.ThrowIfNull(record);
        return TakeWritten(record, _copy.Replace(record.Key, version, record.ToContent()));
    }

    /// <summary>
    /// Moves the version <paramref name="version"/> of <paramref name="record"/> to the trash through the copy (M5.10), and
    /// takes the move into the set as <see cref="Create(RecordFile)"/> takes a write: the record leaves the records and goes
    /// into the trash, as the file moves as it is (M2.5). A newer version, which a load has brought in meanwhile, as another
    /// Kiler restored and changed it, stays. A load that ended before the delete can give the version deleted, as when the
    /// owner of its file alone changed (M2.8b): <see cref="Follow"/> does not take it, unless the trash has lost the record
    /// since, as another Kiler restored it (<see cref="Wrote.Hides"/>).
    /// </summary>
    /// <exception cref="IOException">The share could not be reached before a load said so (core.md, "Calling the layer").</exception>
    /// <exception cref="UnauthorizedAccessException">The share refused the write before a load said so.</exception>
    internal WriteResult Delete(RecordFile record, int version)
    {
        ArgumentNullException.ThrowIfNull(record);
        return TakeWritten(
            record.Key,
            _copy.Delete(record.Key, version),
            (set, deleted) => set.Records.GetValueOrDefault(record.Id)?.File.Version > deleted
                ? null
                : new RecordSet(set.Session, set.Stashes, set.Records.Remove(record.Id), set.Trash.SetItem(record.Id, ViewOf(record, deleted, set.Session))));
    }

    /// <summary>
    /// Brings <paramref name="record"/> back from the trash through the copy (M5.10), a move, so it keeps its version, and
    /// takes it into the set as <see cref="Create(RecordFile)"/> takes a write. <paramref name="version"/> is the version of
    /// the record in the trash of the set. The layer moves whatever the trash of the share has (M2.5): when that is another
    /// version, which another Kiler deleted since the last load, the set reads the copy again, as the copy has its bytes and
    /// no load gives them. A load that ended before the restore cannot give the record from its folder, as it was in the trash.
    /// </summary>
    /// <exception cref="IOException">The share could not be reached before a load said so (core.md, "Calling the layer").</exception>
    /// <exception cref="UnauthorizedAccessException">The share refused the write before a load said so.</exception>
    internal WriteResult RestoreFromTrash(RecordFile record, int version)
    {
        ArgumentNullException.ThrowIfNull(record);
        return TakeWritten(
            record.Key,
            _copy.RestoreFromTrash(record.Key),
            (set, restored) => restored != version ? ReadAll(_copy.Documents(), _copy.Trash(), _templates, set.Session)
                : set.Records.GetValueOrDefault(record.Id)?.File.Version > restored ? null
                : new RecordSet(set.Session, set.Stashes, set.Records.SetItem(record.Id, ViewOf(record, restored, set.Session)), set.Trash.Remove(record.Id)));
    }

    /// <summary>
    /// <paramref name="document"/> of <c>records\</c> read for <paramref name="session"/> with the templates of the set, without
    /// taking it into the set: the record on the share at a conflict (M5.8), which a later load brings in.
    /// </summary>
    internal RecordView ReadDocument(Document document, Session? session)
    {
        ArgumentNullException.ThrowIfNull(document);
        ImmutableSortedDictionary<string, TeamFile<TemplateFile>> templates;
        lock (_gate)
        {
            templates = _templates;
        }

        return RecordView.Read(TeamFile<RecordFile>.Of(document, RecordFile.TryRead), templates, session);
    }

    /// <summary>Creates <paramref name="stash"/> through the copy (M5.4), and takes it into the set, as <see cref="Create(RecordFile)"/> does a record.</summary>
    /// <exception cref="IOException">The share could not be reached before a load said so (core.md, "Calling the layer").</exception>
    /// <exception cref="UnauthorizedAccessException">The share refused the write before a load said so.</exception>
    internal WriteResult Create(StashFile stash)
    {
        ArgumentNullException.ThrowIfNull(stash);
        return TakeWritten(
            stash.Key,
            _copy.Create(stash.Key, stash.ToContent()),
            (set, version) => set.Stashes.GetValueOrDefault(stash.Id)?.Version > version
                ? null
                : new RecordSet(set.Session, set.Stashes.SetItem(stash.Id, TeamFile<StashFile>.Written(version, stash)), set.Records, set.Trash));
    }

    /// <summary>
    /// Reads the stashes and the records, of their folder and of the trash, again from the documents of the copy, after
    /// <see cref="Team.Reload"/>: after <see cref="DataWatch.Failed"/> and at each <see cref="DataWatch.OfflineChanged"/>
    /// (core.md, "Calling the layer").
    /// </summary>
    public void Reload()
    {
        lock (_gate)
        {
            _templates = _team.Files.Templates;
            _set = ReadAll(_copy.Documents(), _copy.Trash(), _templates, _set.Session);
        }
    }

    /// <summary>
    /// Stops following the session, and drops what was decrypted, also when it is disposed before the session, whose lock at
    /// the stop it no longer hears (M5.3 security review). A load that outlives it reads for a guest.
    /// </summary>
    public void Dispose()
    {
        _current.Changed -= OnSessionChanged;
        lock (_gate)
        {
            _set = RecordSet.Empty;
        }
    }

    /// <summary>
    /// A sign-in reads the records for the new session, and a lock for a guest, which drops what was decrypted (core.md,
    /// "Keys and the session"). It runs on the thread of the change, the timer's at the automatic lock, and does not throw: a
    /// group that does not decrypt or does not fit, and a lock while it reads, leave the rest of the record
    /// (<see cref="RecordView.Read"/>).
    /// </summary>
    private void OnSessionChanged(object? sender, EventArgs e)
    {
        lock (_gate)
        {
            var session = _current.Session;
            if (session is { IsLocked: true })
            {
                session = null;
            }

            if (_set.Session != session)
            {
                try
                {
                    _set = WithSession(_set, session);
                }
                // The guard of the M4.5b security review: a fault of the code while it decrypts neither fails the sign-in nor
                // stops the handlers after this one. The records are read for a guest instead, which decrypts nothing.
                catch (Exception) when (session is not null)
                {
                    _set = WithSession(_set, null);
                }
            }
        }
    }

    /// <summary>Takes a write of <paramref name="record"/> into the set, read for the session of the set, as <see cref="TakeWritten(DocumentKey, WriteResult, Func{RecordSet, int, RecordSet?})"/> does.</summary>
    private WriteResult TakeWritten(RecordFile record, WriteResult written) => TakeWritten(
        record.Key,
        written,
        (set, version) => set.Records.GetValueOrDefault(record.Id)?.File.Version > version
            ? null
            : new RecordSet(set.Session, set.Stashes, set.Records.SetItem(record.Id, ViewOf(record, version, set.Session)), set.Trash));

    /// <summary>
    /// Takes a write of this Kiler into the set under the lock, when it is written, or a delete, when it is in the trash:
    /// <paramref name="put"/> gives the set with it at its version, or <c>null</c> when a load has brought in a newer version
    /// meanwhile, which stays. The version is kept in <see cref="_written"/> until the next <see cref="Follow"/>.
    /// </summary>
    private WriteResult TakeWritten(DocumentKey key, WriteResult written, Func<RecordSet, int, RecordSet?> put)
    {
        if (written.Outcome is WriteOutcome.Written or WriteOutcome.Deleted)
        {
            lock (_gate)
            {
                if (put(_set, written.Version) is { } set)
                {
                    _set = set;
                }

                _written[key] = new Wrote(written.Version, written.Outcome == WriteOutcome.Deleted);
            }
        }

        return written;
    }

    /// <summary><paramref name="record"/>, which this Kiler wrote, deleted or restored at <paramref name="version"/>, read for <paramref name="session"/> with the templates of the last update.</summary>
    private RecordView ViewOf(RecordFile record, int version, Session? session) => RecordView.Read(TeamFile<RecordFile>.Written(version, record), _templates, session);

    private RecordSet WithSession(RecordSet set, Session? session)
    {
        _templates = _team.Files.Templates;
        return new RecordSet(session, set.Stashes, ReadAgain(set.Records, _templates, session, _ => true), ReadAgain(set.Trash, _templates, session, _ => true));
    }

    private static ImmutableSortedDictionary<string, RecordView> ReadAgain(
        ImmutableSortedDictionary<string, RecordView> records,
        ImmutableSortedDictionary<string, TeamFile<TemplateFile>> templates,
        Session? session,
        Func<RecordView, bool> which)
    {
        var again = records.ToBuilder();
        foreach (var (id, view) in records)
        {
            if (which(view))
            {
                again[id] = RecordView.Read(view.File, templates, session);
            }
        }

        return again.ToImmutable();
    }

    private static RecordSet ReadAll(
        IReadOnlyList<LoadedDocument> documents,
        IReadOnlyList<LoadedDocument> trash,
        ImmutableSortedDictionary<string, TeamFile<TemplateFile>> templates,
        Session? session)
    {
        var stashes = RecordSet.Empty.Stashes.ToBuilder();
        var records = RecordSet.Empty.Records.ToBuilder();
        foreach (var loaded in documents)
        {
            if (IsIn(loaded.Key, DataFolder.Stashes))
            {
                stashes[loaded.Key.Id] = TeamFile<StashFile>.Of(loaded.Read, StashFile.TryRead);
            }
            else if (IsIn(loaded.Key, DataFolder.Records))
            {
                records[loaded.Key.Id] = RecordView.Read(TeamFile<RecordFile>.Of(loaded.Read, RecordFile.TryRead), templates, session);
            }
        }

        return new RecordSet(session, stashes.ToImmutable(), records.ToImmutable(), ReadTrash(trash, templates, session));
    }

    /// <summary>The records of the trash of the copy (<see cref="DataCopy.Trash"/>), each read for <paramref name="session"/>, as those of their folder.</summary>
    private static ImmutableSortedDictionary<string, RecordView> ReadTrash(
        IReadOnlyList<LoadedDocument> trash,
        ImmutableSortedDictionary<string, TeamFile<TemplateFile>> templates,
        Session? session)
    {
        var records = RecordSet.Empty.Trash.ToBuilder();
        foreach (var loaded in trash)
        {
            if (IsIn(loaded.Key, DataFolder.Records))
            {
                records[loaded.Key.Id] = RecordView.Read(TeamFile<RecordFile>.Of(loaded.Read, RecordFile.TryRead), templates, session);
            }
        }

        return records.ToImmutable();
    }

    /// <summary>A document of <paramref name="folder"/> of the data folder, not of a private folder.</summary>
    private static bool IsIn(DocumentKey key, string folder) => key.Account is null && key.Folder == folder;

    /// <summary>A version that this Kiler wrote, or deleted (<see cref="_written"/>).</summary>
    private readonly record struct Wrote(int Version, bool Deleted)
    {
        /// <summary>
        /// Whether a load that ended before the write may have given <paramref name="version"/>, which then does not take its
        /// place: an older version, and the version deleted too while the trash still has the record (M5.10). A load after a
        /// restore of another Kiler gives that version from the trash, and the trash, read again, no longer has it.
        /// </summary>
        public bool Hides(int? version, bool inTrash) => version < Version || (Deleted && inTrash && version == Version);
    }
}

/// <summary>
/// The stashes and the records at one moment (<see cref="Records.Read"/>), never changed, the records as
/// <see cref="Session"/> reads them.
/// </summary>
public sealed class RecordSet
{
    private SearchIndex? _index;

    internal RecordSet(
        Session? session,
        ImmutableSortedDictionary<string, TeamFile<StashFile>> stashes,
        ImmutableSortedDictionary<string, RecordView> records,
        ImmutableSortedDictionary<string, RecordView> trash)
    {
        Session = session;
        Stashes = stashes;
        Records = records;
        Trash = trash;
    }

    internal static RecordSet Empty { get; } = new(
        null,
        ImmutableSortedDictionary.Create<string, TeamFile<StashFile>>(StringComparer.Ordinal),
        ImmutableSortedDictionary.Create<string, RecordView>(StringComparer.Ordinal),
        ImmutableSortedDictionary.Create<string, RecordView>(StringComparer.Ordinal));

    /// <summary>The session whose levels the records hold; null for a guest, who reads level 0.</summary>
    public Session? Session { get; }

    /// <summary>The stashes of <c>stashes\</c>, by id, for every reader.</summary>
    public ImmutableSortedDictionary<string, TeamFile<StashFile>> Stashes { get; }

    /// <summary>The shared records of <c>records\</c>, by id, each as <see cref="Session"/> reads it.</summary>
    public ImmutableSortedDictionary<string, RecordView> Records { get; }

    /// <summary>The shared records of <c>trash\records\</c> (M5.10), by id, each as <see cref="Session"/> reads it; the search and the names of a <c>ref</c> do not see them.</summary>
    public ImmutableSortedDictionary<string, RecordView> Trash { get; }

    /// <summary>What the search reads of the records, made at the first search of this set (M5.6, <see cref="RecordSearch"/>).</summary>
    internal SearchIndex Index => LazyInitializer.EnsureInitialized(ref _index, () => new SearchIndex(this));

    /// <summary>The name of the stash <paramref name="id"/>; null when it is missing or does not fit.</summary>
    public string? StashName(string id) => Stashes.GetValueOrDefault(id)?.Model?.Name;

    /// <summary>The name of the record <paramref name="id"/>, at level 0 for every reader, as of a <c>ref</c>; null when it has gone or does not fit.</summary>
    public string? RecordName(string id) => Records.GetValueOrDefault(id)?.File.Model?.Name;
}

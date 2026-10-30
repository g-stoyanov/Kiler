using System.Collections.Immutable;
using Kiler.Storage;

namespace Kiler.Core;

/// <summary>
/// The stashes and the shared records of the local copy, in memory (M5.3, design 5.3): every stash, for every reader, and
/// each record as one session reads it (<see cref="RecordView"/>), the groups of its keys decrypted. What it has is one
/// <see cref="RecordSet"/>, never changed, which each update replaces whole, as <see cref="Team"/> does. It follows each load
/// of the watch after <see cref="Team.Follow"/> (<see cref="Follow"/>), whose templates it reads, and reads the copy again
/// after <see cref="Team.Reload"/> (<see cref="Reload"/>), both on the thread of the watch. It follows
/// <see cref="CurrentSession.Changed"/>: a sign-in decrypts for the new session, and a lock drops what was decrypted. The
/// updates keep a lock of their own. The private records come after the exam, so it skips the private folders.
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
    /// Reads the stashes and the records from the documents of <paramref name="copy"/>, as the last load left them, for a guest:
    /// a session gets its own at its first <see cref="Read"/> or at the next <see cref="CurrentSession.Changed"/>.
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
        _set = ReadAll(copy.Documents(), _templates, null);
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
    /// records whose template the load changed. <see cref="Team.Follow"/> takes the same load first.
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
                records = ReadAgain(records, templates, set.Session, view => view.File.Model is { } record && templates.GetValueOrDefault(record.Template) != view.TemplateEntry);
                _templates = templates;
            }

            foreach (var loaded in load.Changed)
            {
                if (IsIn(loaded.Key, DataFolder.Stashes))
                {
                    stashes = stashes.SetItem(loaded.Key.Id, TeamFile<StashFile>.Of(loaded.Read, StashFile.TryRead));
                }
                else if (IsIn(loaded.Key, DataFolder.Records))
                {
                    records = records.SetItem(loaded.Key.Id, RecordView.Read(TeamFile<RecordFile>.Of(loaded.Read, RecordFile.TryRead), templates, set.Session));
                }
            }

            if (stashes != set.Stashes || records != set.Records)
            {
                _set = new RecordSet(set.Session, stashes, records);
            }
        }
    }

    /// <summary>
    /// Reads the stashes and the records again from the documents of the copy, after <see cref="Team.Reload"/>: after
    /// <see cref="DataWatch.Failed"/> and at each <see cref="DataWatch.OfflineChanged"/> (core.md, "Calling the layer").
    /// </summary>
    public void Reload()
    {
        lock (_gate)
        {
            _templates = _team.Files.Templates;
            _set = ReadAll(_copy.Documents(), _templates, _set.Session);
        }
    }

    public void Dispose() => _current.Changed -= OnSessionChanged;

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

    private RecordSet WithSession(RecordSet set, Session? session)
    {
        _templates = _team.Files.Templates;
        return new RecordSet(session, set.Stashes, ReadAgain(set.Records, _templates, session, _ => true));
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

    private static RecordSet ReadAll(IReadOnlyList<LoadedDocument> documents, ImmutableSortedDictionary<string, TeamFile<TemplateFile>> templates, Session? session)
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

        return new RecordSet(session, stashes.ToImmutable(), records.ToImmutable());
    }

    /// <summary>A document of <paramref name="folder"/> of the data folder, not of a private folder.</summary>
    private static bool IsIn(DocumentKey key, string folder) => key.Account is null && key.Folder == folder;
}

/// <summary>
/// The stashes and the records at one moment (<see cref="Records.Read"/>), never changed, the records as
/// <see cref="Session"/> reads them.
/// </summary>
public sealed class RecordSet
{
    internal RecordSet(
        Session? session,
        ImmutableSortedDictionary<string, TeamFile<StashFile>> stashes,
        ImmutableSortedDictionary<string, RecordView> records)
    {
        Session = session;
        Stashes = stashes;
        Records = records;
    }

    internal static RecordSet Empty { get; } = new(
        null,
        ImmutableSortedDictionary.Create<string, TeamFile<StashFile>>(StringComparer.Ordinal),
        ImmutableSortedDictionary.Create<string, RecordView>(StringComparer.Ordinal));

    /// <summary>The session whose levels the records hold; null for a guest, who reads level 0.</summary>
    public Session? Session { get; }

    /// <summary>The stashes of <c>stashes\</c>, by id, for every reader.</summary>
    public ImmutableSortedDictionary<string, TeamFile<StashFile>> Stashes { get; }

    /// <summary>The shared records of <c>records\</c>, by id, each as <see cref="Session"/> reads it.</summary>
    public ImmutableSortedDictionary<string, RecordView> Records { get; }
}

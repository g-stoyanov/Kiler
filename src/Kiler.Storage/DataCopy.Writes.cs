using System.Text.Json.Nodes;

namespace Kiler.Storage;

/// <summary>
/// The writes of this Kiler through the local copy (decided in M2.7a, by the user): each writes through the share
/// (<see cref="DataFolder"/>) and then takes into the copy what it wrote, within the budget of the copy. The stamp and the
/// owner of the file on the share stay unknown until a load reads it back (M2.3 and M2.6 notes). A write goes to the root
/// of its key, the data folder or the private folder of the current user, and to no other (<see cref="RootOf"/>, M2.7b).
/// </summary>
public sealed partial class DataCopy
{
    /// <summary>
    /// Creates a document on the share (<see cref="DataFolder.CreateDocument"/>) and, when it is written, takes the bytes it
    /// wrote into the copy, as this Kiler's own (decided in M2.7a, by the user). The size, the times and the owner of the file
    /// on the share stay unknown until a load reads it back: someone could have put another file under its name before the
    /// rename (M2.3 and M2.6 notes), and the load compares it with these bytes. The last good bytes that the copy kept of a
    /// document with that id, which went otherwise than to the trash, go: the write is a version that Kiler made.
    /// </summary>
    public WriteResult Create(DocumentKey key, JsonObject content)
    {
        var result = RootOf(key).CreateDocument(key, content);
        if (result is { Outcome: WriteOutcome.Written, Bytes: { } bytes })
        {
            Wrote(new Spot(key, InTrash: false), bytes, result.Version, historyFrom: null, slotTaken: false);
            FlushIndex();
        }

        return result;
    }

    /// <summary>
    /// Replaces the version <paramref name="version"/> of a document on the share (<see cref="DataFolder.ReplaceDocument"/>)
    /// and, when it is written, takes the bytes it wrote into the copy as <see cref="Create"/> does, and the version it
    /// replaced as the slot of that version in the history of the copy: the last good bytes of that version that the copy
    /// has, else those that the write put into the slot on the share, such as an edit in place that the copy did not have as
    /// good. Versions between the last that the copy saw and this one are taken from the history of the share by the next
    /// load. The last good bytes kept apart for a change outside Kiler go, as the write is a version that Kiler made.
    /// </summary>
    public WriteResult Replace(DocumentKey key, int version, JsonObject content)
    {
        var result = RootOf(key).ReplaceDocument(key, version, content);
        if (result is { Outcome: WriteOutcome.Written, Bytes: { } bytes, Old: { } old })
        {
            var spot = new Spot(key, InTrash: false);
            _index.TryGetValue(spot, out var entry);
            var slotTaken = TakeOwnSlot(key, version, SlotBytes(spot, entry, version, old), _time.GetUtcNow());
            int? from = entry?.LastGood is { } lastGood && lastGood.Version < version ? lastGood.Version : null;
            Wrote(spot, bytes, result.Version, from, slotTaken);
            FlushIndex();
        }

        return result;
    }

    /// <summary>
    /// Deletes the version <paramref name="version"/> of a document on the share (<see cref="DataFolder.DeleteDocument"/>) and,
    /// when it is in the trash, follows the move in the copy: the bytes it deleted go to the trash of the copy, with what the
    /// copy kept of the document, and into the slot of that version, as <see cref="Replace"/> takes it.
    /// </summary>
    public WriteResult Delete(DocumentKey key, int version)
    {
        var result = RootOf(key).DeleteDocument(key, version);
        if (result is { Outcome: WriteOutcome.Deleted, Old: { } old })
        {
            var spot = new Spot(key, InTrash: false);
            _index.TryGetValue(spot, out var entry);
            TakeOwnSlot(key, version, SlotBytes(spot, entry, version, old), _time.GetUtcNow());
            Moved(spot, new Spot(key, InTrash: true), old, version);
            FlushIndex();
        }

        return result;
    }

    /// <summary>
    /// Restores a document from the trash of the share (<see cref="DataFolder.RestoreFromTrash"/>) and, when it is back,
    /// follows the move in the copy, as <see cref="Delete"/> does the other way.
    /// </summary>
    public WriteResult RestoreFromTrash(DocumentKey key)
    {
        var result = RootOf(key).RestoreFromTrash(key);
        if (result is { Outcome: WriteOutcome.Written, Bytes: { } bytes })
        {
            Moved(new Spot(key, InTrash: true), new Spot(key, InTrash: false), bytes, result.Version);
            FlushIndex();
        }

        return result;
    }

    /// <summary>
    /// The root that a write of a document goes to (M2.7b): the data folder, or the private folder of the current user,
    /// opened for the write; one that is not there is a <see cref="DirectoryNotFoundException"/>, and one that is a link an
    /// <see cref="IOException"/>, before anything is written. A key of another private folder, or of any private folder in a
    /// copy without an account, is an <see cref="ArgumentException"/>: the copy takes no other private folder, and its user
    /// writes no other.
    /// </summary>
    private DataFolder RootOf(DocumentKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Account is null)
        {
            return _share;
        }

        return key.Account == _account
            ? _share.OpenPrivate(key.Account)
            : throw new ArgumentException("A write through the local copy goes only to the data folder or to the private folder of its user.", nameof(key));
    }

    /// <summary>Writes the index when it is behind the one in memory; one that cannot be written now is written by the next load.</summary>
    private void FlushIndex()
    {
        if (_indexBehind)
        {
            _indexBehind = !TryWriteIndex();
        }
    }

    /// <summary>
    /// The bytes for the slot of a version that a write of this Kiler replaced or deleted: the last good bytes that the copy
    /// has of that version, when it has them, as the share can have had other bytes at that version, such as an edit in place;
    /// else those that the write put into the slot on the share.
    /// </summary>
    private byte[] SlotBytes(Spot spot, CopyEntry? entry, int version, byte[] old) =>
        entry?.LastGood is { } lastGood && lastGood.Version == version && ReadLastGood(LastGoodPath(spot, entry), lastGood, out var bytes) == Keep.Done ? bytes : old;

    /// <summary>
    /// Whether the copy has room for an entry in place of others, as a write of this Kiler takes it: within the budget of
    /// bytes and of documents, or no further over it than it is. A copy that a load filled has no room for one more, and an
    /// index with more entries than the budget would be no index at the next start (security review of M2.7a).
    /// </summary>
    private bool HasRoom(CopyEntry next, params ReadOnlySpan<CopyEntry?> replaced)
    {
        var bytes = _index.Values.Sum(Cost);
        var count = _index.Count;
        var nextBytes = bytes + Cost(next);
        var nextCount = count + 1;
        foreach (var entry in replaced)
        {
            if (entry is not null)
            {
                nextBytes -= Cost(entry);
                nextCount--;
            }
        }

        return (nextBytes <= _limits.Bytes || nextBytes <= bytes) && (nextCount <= _limits.Documents || nextCount <= count);
    }

    /// <summary>
    /// Takes the bytes of a create or a replace of this Kiler into the copy, as its own: a version that Kiler made, with a
    /// stamp and an owner that no load has read yet, so the next load reads the file back from the share and compares it with
    /// them. When the slot of the version it replaced was taken, the index names it before the bytes of that version are
    /// overwritten. When the copy has no room, or the bytes cannot be written, the copy keeps what it had, with a stamp that
    /// makes the next load read the share's file.
    /// </summary>
    private void Wrote(Spot spot, byte[] bytes, int version, int? historyFrom, bool slotTaken)
    {
        _index.TryGetValue(spot, out var old);
        var next = new CopyEntry
        {
            Account = spot.Key.Account,
            Folder = spot.Key.Folder,
            Id = spot.Key.Id,
            InTrash = spot.InTrash,
            CreationTime = CopyEntry.Unseen,
            LastWriteTime = CopyEntry.Unseen,
            Taken = TakenAt(old, _time.GetUtcNow()),
            Version = version,
            Hash = Hash(bytes),
            Copied = bytes.Length,
            Own = true,
            HistoryFrom = historyFrom is { } known ? Lower(old?.HistoryFrom, known) : old?.HistoryFrom,
        };

        // The index on the disk names the slot of the version that is overwritten: the one this write took, or one that an index
        // that could not be written yet does not name (third security review of M2.7a).
        if (!HasRoom(next, old) || ((slotTaken || _indexBehind) && old is { Outside: null, Copied: > 0 } && !TryWriteIndex()) || !TryWriteFile(CopyPath(spot), bytes))
        {
            if (old is not null)
            {
                _index[spot] = old with { CreationTime = CopyEntry.Unseen, LastWriteTime = CopyEntry.Unseen };
                _indexBehind = true;
            }

            return;
        }

        _index[spot] = next;
        Stale(FilesOf(spot, old), FilesOf(spot, next));
        _reread.Remove(spot);
        _edits.Forget(spot.Key);
        _indexBehind = true;
    }

    /// <summary>
    /// Follows a delete or a restore of this Kiler in the copy: the bytes it moved go to the other place as the next state of
    /// the document (<see cref="Next"/>), with what the copy kept of it, in place of what the copy had there. When the copy
    /// has no room, or they cannot be taken, the copy stays as it was, and the next load follows the move.
    /// </summary>
    private void Moved(Spot from, Spot to, byte[] bytes, int version)
    {
        _index.TryGetValue(from, out var lineage);
        _index.TryGetValue(to, out var replaced);
        var hash = Hash(bytes);

        // A move keeps the times of the file, so the bytes of an edit in place stay one.
        var edited = lineage is { Outside: OutsideReason.EditedInPlace, Copied: > 0 } && lineage.Hash == hash;
        var unseen = new FileStamp(0, CopyEntry.Unseen, CopyEntry.Unseen);
        var next = Next(to, lineage, new Seen(unseen, null, null, version, hash, bytes.Length, edited), _time.GetUtcNow());
        if (!HasRoom(next, lineage, replaced))
        {
            return;
        }

        if (next.Kept is { } lastGood)
        {
            var kept = CopyLastGood(LastGoodPath(from, lineage!), KeptPath(to), lastGood);
            if (kept == Keep.Failed)
            {
                return;
            }

            if (kept == Keep.Lost)
            {
                next = next with { Kept = null };
            }
        }

        // Flushed, as the index after it lets the file of the other place go.
        if (!TryWriteFile(CopyPath(to), bytes, flush: true))
        {
            return;
        }

        _index[to] = next;
        _index.Remove(from);
        Stale(FilesOf(from, lineage).Concat(FilesOf(to, replaced)), FilesOf(to, next));
        _reread.Remove(from);
        _reread.Remove(to);
        _indexBehind = true;
    }
}

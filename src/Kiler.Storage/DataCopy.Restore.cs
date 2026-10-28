using System.Diagnostics.CodeAnalysis;

namespace Kiler.Storage;

/// <summary>
/// The restore from the local copy (M2.9, PLAN M2 step 10): the documents whose last good bytes the copy keeps for a restore
/// (<see cref="ToRestore"/>), and forgetting those that should not come back (<see cref="Forget"/>), such as the private
/// folder of someone who has left (<see cref="ForgetPrivate"/>, design 7.9). None of them touches the share, so they work
/// while the copy is offline too. Writing back a document gone outside the trash (<see cref="WriteBack"/>, M2.9a) is a write
/// through the copy; over a file that is there it is M2.9b, and into the private folder of another user M2.9c. The screen is
/// M6, and the check that only an admin restores is M4.
/// </summary>
public sealed partial class DataCopy
{
    /// <summary>
    /// The documents whose last good bytes the copy keeps for a restore (M2.9): each change outside Kiler that a load has
    /// reported (<see cref="LoadResult.Outside"/>) and that is still so, with the version of its last good bytes: a document
    /// gone otherwise than to the trash or back, damaged, edited in place, or at a version that is not after the last good
    /// one, in its folder or in the trash; of the data folder, of the private folder of the current user, and in an admin's
    /// copy of the private folders of other users, also of those that the copy is no longer given, as it keeps them for a
    /// restore (M2.7c). One without last good bytes, such as a damaged document that the copy never had good, has nothing to
    /// restore and is not here. The list comes from the index, as a load reports each change once (M2.7a): one that no load
    /// has reported yet, such as a document gone for less than <see cref="InPlaceEdits.SettleTime"/>, which can be a move that
    /// the SMB cache shows late, is here once a load reports it. In the order of the accounts, the folders and the ids, the
    /// data folder and the documents at its root first, each document in its folder before the trash. The share is not touched.
    /// </summary>
    public IReadOnlyList<OutsideChange> ToRestore()
    {
        lock (_gate)
        {
            var changes = new List<OutsideChange>();
            foreach (var (spot, entry) in _index)
            {
                if (entry is { Outside: { } reason, Kept: { } kept, Reported: true })
                {
                    changes.Add(new OutsideChange(spot.Key, spot.InTrash, reason, kept.Version));
                }
            }

            return [.. changes
                .OrderBy(change => change.Key.Account, StringComparer.Ordinal)
                .ThenBy(change => change.Key.Folder, StringComparer.Ordinal)
                .ThenBy(change => change.Key.Id, StringComparer.Ordinal)
                .ThenBy(change => change.InTrash)];
        }
    }

    /// <summary>
    /// Forgets the last good bytes of the changes outside Kiler in <paramref name="changes"/>, as <see cref="ToRestore"/> gave
    /// them, which should not come back (M2.9, decided by the user). A gone document leaves the copy with its last good bytes
    /// and the slots of its history in the copy, as junk that someone kept on the share for a day and took away blocks the
    /// budget until it is forgotten (M2.7). One that the share still has keeps its file as the share has it, and the slots of
    /// its history: one edited in place or at a version out of order takes the bytes of the share as its last good, which a
    /// later restore brings back, and a damaged one stays damaged, with nothing to restore, and is not reported again. A change
    /// is forgotten only while it is as the list gave it (<see cref="IsListed"/>), so a screen never forgets one of another
    /// reason or another last good version than it showed; the others stay as they are. The limit (reviews of M2.9): a second
    /// edit in place, or another file out of order, of a document already reported so, is not reported again (M2.7a), and is
    /// taken with the first, though the list never showed it; outside the threat model, as the admin chose to let the last
    /// good bytes go. Gives how many were forgotten. The files of the copy go once an index that no longer names them is
    /// written (review of M2.7a). The share is not touched.
    /// </summary>
    public int Forget(IEnumerable<OutsideChange> changes)
    {
        ArgumentNullException.ThrowIfNull(changes);
        OutsideChange[] chosen = [.. changes];
        lock (_gate)
        {
            var forgotten = 0;
            var gone = new HashSet<Spot>();
            foreach (var change in chosen)
            {
                if (!IsListed(change, out var spot, out var entry))
                {
                    continue;
                }

                forgotten++;
                if (change.Reason == OutsideReason.Gone)
                {
                    Drop(spot, entry);
                    gone.Add(spot);
                }
                else if (change.Reason == OutsideReason.Damaged)
                {
                    SetEntry(spot, entry, entry with { Kept = null });
                }
                else if (entry.Copied > 0)
                {
                    // The bytes in data\ are those of the file on the share, and become the last good.
                    SetEntry(spot, entry, entry with { Outside = null, Kept = null, Reported = false });
                }
                else
                {
                    // Without them, as when their file had gone from the copy at the start, the entry leaves the copy, and the next
                    // load reads the file anew, as a document that the copy did not have: good, or edited in place without last
                    // good bytes when its times say so. The document is still on the share, so its slots stay.
                    Drop(spot, entry);
                }
            }

            // The slots of the gone documents, in one walk of the history: those of their place, and those of the other place
            // when the copy has no document there, as the copy keeps the slots of a document that this Kiler deleted in its
            // folder (reviews of M2.9).
            var slotsOf = new HashSet<Spot>(gone);
            foreach (var spot in gone)
            {
                var other = spot with { InTrash = !spot.InTrash };
                if (!_index.ContainsKey(other))
                {
                    slotsOf.Add(other);
                }
            }

            if (slotsOf.Count > 0)
            {
                DropSlots(slot => slotsOf.Contains(new Spot(slot.Key, slot.InTrash)));
            }

            if (forgotten > 0)
            {
                _indexBehind = true;
                FlushIndex();
            }

            return forgotten;
        }
    }

    /// <summary>
    /// Forgets all that the copy has of the private folder of another user (M2.9, design 7.9), such as of someone who has
    /// left, once the folder is deleted on the share or should not come back: its documents in their folders and in the
    /// trash, with their last good bytes, the slots of their history from when it was the user's own, what the folder is
    /// (M2.7d), and what its last pass skipped (M2.8c). The account leaves the accounts that the copy is given too, until a
    /// call of <see cref="CopyOthers"/> gives it again (decided by the user), as the next load would otherwise copy the folder
    /// again while the share has it; given again, a folder at the name is taken as first seen. The stopped creates of that
    /// user stay on the share (M2.7e). The files of the copy go once an index that no longer names them is written. The share
    /// is not touched. An account that fails the name check of <see cref="StorageName"/>, or that of the current user, whose
    /// private folder every load takes, is an <see cref="ArgumentException"/>, and nothing is forgotten.
    /// </summary>
    public void ForgetPrivate(string account)
    {
        var name = StorageName.Check(account);
        if (name == _account)
        {
            throw new ArgumentException("The private folder of the current user is not forgotten, as every load takes it.", nameof(account));
        }

        lock (_gate)
        {
            var forgotten = false;
            foreach (var (spot, entry) in _index.Where(entry => entry.Key.Key.Account == name).ToList())
            {
                Drop(spot, entry);
                forgotten = true;
            }

            forgotten |= DropSlots(slot => slot.Key.Account == name);
            forgotten |= _roots.Remove(name);
            _others.Remove(name);
            _othersSkipped.Remove(name);
            if (forgotten)
            {
                _indexBehind = true;
                FlushIndex();
            }
        }
    }

    /// <summary>
    /// Writes back a document gone outside the trash, as <see cref="ToRestore"/> gave it (M2.9a): its last good bytes go back
    /// on the share as they are, at their version, where it went from, its folder or the trash, through a write of their own
    /// (<see cref="DataFolder.WriteBack"/>), and the copy follows it. So every copy that has the same last good bytes takes
    /// them as good again at its next load, and one that had an older version takes them as a new one (decided in M2.9a, by
    /// the user). The file is new, so its owner is the one who wrote it back (M4 must not mark it for that). Nothing is
    /// written, and the copy stays as it is, when the change is no longer as the list gave it (<see cref="IsListed"/>,
    /// <see cref="WriteOutcome.NotListed"/>); when the bytes kept apart are not the last good ones any more
    /// (<see cref="WriteOutcome.Missing"/>) or cannot be read now (<see cref="WriteOutcome.Busy"/>), as the bytes in
    /// <c>kept\</c> are checked against their hash first (M2.7a); when anything is at its name, or a document of its id is in
    /// the other place (<see cref="WriteOutcome.Exists"/>); when the history of the share has a version after them, or one of
    /// theirs with other bytes, as the copy did not see versions that other Kilers wrote (<see cref="WriteOutcome.Behind"/>);
    /// and while the copy is offline (<see cref="WriteOutcome.Offline"/>), as for the other writes through the copy. The
    /// limit (decided by the user): a copy that missed only the last version of the share writes back the one before it, as
    /// the slot of that version has its bytes, and a copy that saw the last one reports the document out of order, with its
    /// last good bytes. A change of another reason is a <see cref="NotSupportedException"/>, as writing back over a file that
    /// is there comes with M2.9b, and one of the private folder of another user an <see cref="ArgumentException"/> (M2.9c),
    /// before anything is looked at.
    /// </summary>
    public WriteResult WriteBack(OutsideChange change)
    {
        ArgumentNullException.ThrowIfNull(change.Key, nameof(change));
        if (change.Reason != OutsideReason.Gone)
        {
            throw new NotSupportedException("Only a document gone outside the trash is written back.");
        }

        if (!IsOwn(change.Key.Account))
        {
            throw new ArgumentException("A write through the local copy goes only to the data folder or to the private folder of its user.", nameof(change));
        }

        if (_offline)
        {
            return Refused;
        }

        lock (_gate)
        {
            if (_offline)
            {
                return Refused;
            }

            if (!IsListed(change, out var spot, out var entry) || entry.Kept is not { } lastGood)
            {
                return WriteResult.Not(WriteOutcome.NotListed);
            }

            var kept = ReadLastGood(KeptPath(spot), lastGood, out var bytes);
            if (kept != Keep.Done)
            {
                return WriteResult.Not(kept == Keep.Lost ? WriteOutcome.Missing : WriteOutcome.Busy);
            }

            var result = RootOf(change.Key).WriteBack(change.Key, spot.InTrash, bytes, _limits.Listed);
            if (result.Outcome == WriteOutcome.Written)
            {
                WroteBack(spot, entry, lastGood, bytes);
                FlushIndex();
            }

            return result;
        }
    }

    /// <summary>
    /// Whether a change is in the copy as <see cref="ToRestore"/> gave it: the entry of its place has a change outside Kiler
    /// that a load has reported, of the same reason, with last good bytes of the same version. A write back (M2.9a to M2.9c)
    /// takes a change so too.
    /// </summary>
    private bool IsListed(OutsideChange change, out Spot spot, [NotNullWhen(true)] out CopyEntry? entry)
    {
        spot = new Spot(change.Key, change.InTrash);
        return _index.TryGetValue(spot, out entry)
            && entry is { Outside: { } reason, Kept: { } kept, Reported: true }
            && reason == change.Reason
            && kept.Version == change.LastGood;
    }

    /// <summary>
    /// Follows a write back in the copy (M2.9a): the last good bytes become the bytes of the document in <c>data\</c>, a
    /// version that Kiler made, as the next load would take the share's file, with a stamp that no listing gives and the
    /// owner unknown, so that load reads the file back and compares its hash (M2.3 and M2.6 notes). Its stay counts only the
    /// time that the share listed it (<see cref="TakenAt"/>), and the bytes cost the budget what they cost kept apart, so it
    /// needs no room. When they cannot be written into the copy, it stays as it is, and the next load follows the file, as
    /// the last good bytes again are good.
    /// </summary>
    private void WroteBack(Spot spot, CopyEntry entry, KeptCopy lastGood, byte[] bytes)
    {
        // Flushed, as the index after it lets the bytes kept apart go.
        if (!TryWriteFile(CopyPath(spot), bytes, flush: true))
        {
            return;
        }

        SetEntry(spot, entry, entry with
        {
            Size = 0,
            CreationTime = CopyEntry.Unseen,
            LastWriteTime = CopyEntry.Unseen,
            Owner = null,
            Damage = null,
            Taken = TakenAt(entry, _time.GetUtcNow()),
            GoneAt = null,
            Version = lastGood.Version,
            Hash = lastGood.Hash,
            Copied = lastGood.Length,
            Own = lastGood.Own,
            Outside = null,
            Kept = null,
            Reported = false,
        });
        _indexBehind = true;
    }

    /// <summary>Puts the next state of an entry in its place; the files that it no longer names go with the next index.</summary>
    private void SetEntry(Spot spot, CopyEntry entry, CopyEntry next)
    {
        _index[spot] = next;
        Stale(FilesOf(spot, entry), FilesOf(spot, next));
        (spot.InTrash ? _trashEdits : _edits).Forget(spot.Key);
    }

    /// <summary>Takes an entry out of the index, with what the copy keeps of it in memory; its files go with the next index.</summary>
    private void Drop(Spot spot, CopyEntry entry)
    {
        _index.Remove(spot);
        Stale(FilesOf(spot, entry), []);
        _reread.Remove(spot);
        _touched.Remove(spot);
        (spot.InTrash ? _trashEdits : _edits).Forget(spot.Key);
    }

    /// <summary>Takes the slots that <paramref name="match"/> gives out of the history of the copy; their files go with the next index. Gives whether there were any.</summary>
    private bool DropSlots(Func<SlotKey, bool> match)
    {
        KeyValuePair<SlotKey, SlotEntry>[] slots = [.. _history.Where(slot => match(slot.Key))];
        ForgetSlots(slots);
        return slots.Length > 0;
    }
}

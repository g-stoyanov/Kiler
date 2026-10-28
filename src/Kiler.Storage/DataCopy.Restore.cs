using System.Diagnostics.CodeAnalysis;

namespace Kiler.Storage;

/// <summary>
/// The restore from the local copy (M2.9, PLAN M2 step 10): the documents whose last good bytes the copy keeps for a restore
/// (<see cref="ToRestore"/>), and forgetting those that should not come back (<see cref="Forget"/>), such as the private
/// folder of someone who has left (<see cref="ForgetPrivate"/>, design 7.9). None of them touches the share, so they work
/// while the copy is offline too. Writing one back is M2.9a to M2.9c; the screen is M6, and the check that only an admin
/// restores is M4.
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

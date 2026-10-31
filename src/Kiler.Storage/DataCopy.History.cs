using System.Globalization;

namespace Kiler.Storage;

/// <summary>
/// The history of the local copy (M2.7a): the slots that it takes from the history folders of the share after a change, the
/// slots of the versions that it had itself, and the budget of its own that they share (decided in M2.7a, by the user).
/// </summary>
public sealed partial class DataCopy
{
    /// <summary>
    /// How many slots of one history folder a load takes at most, the newest first: anyone can put any number of slot names
    /// there, and a version that anyone sets far ahead would make the range of the new ones as long.
    /// </summary>
    internal const int SlotsPerListing = 100;

    /// <summary>
    /// The most bytes of slots that one listing of a history folder reads, by their listing: 8 MiB. A slot is read before the
    /// budget knows whether it is good, and room is made only for one that is (security review of M2.7a).
    /// </summary>
    internal const long SlotBytesPerListing = 8L * 1024 * 1024;

    /// <summary>
    /// The documents whose history folder or slots failed at the last try, listed after the others once their back-off is due,
    /// with what that try reported, which every load reports until the next try (M2.8b).
    /// </summary>
    private readonly Dictionary<DocumentKey, HistoryRetry> _historyFailed = [];

    /// <summary>The budget of the history in one load, made at its first use.</summary>
    private HistoryBudget? _budget;

    private HistoryBudget Budget => _budget ??= new HistoryBudget(_history, _limits);

    /// <summary>For the tests: the skipped entries that the history folders that wait keep for the loads until their next try (M2.8b).</summary>
    internal int HistorySkipsKept
    {
        get
        {
            lock (_gate)
            {
                return _historyFailed.Values.Sum(retry => retry.Skipped.Length);
            }
        }
    }

    /// <summary>
    /// The old versions of a document in its folder that the copy keeps in its history, the newest first, for the history of a
    /// record (M5.11): each read anew from its file in the copy and checked against its hash, as <see cref="Documents"/> reads
    /// the documents. The share is not touched, so it answers while offline too. A slot whose file is missing, cannot be read
    /// now, has other bytes, is damaged or holds another version is left out, and keeps its entry. The copy keeps only what it
    /// saw (<see cref="TakeHistory"/>): the slots of the writes of this Kiler, and those after the version that it had when a
    /// load saw a write of another Kiler, within the budget of the history; so a version from before the first load of the copy
    /// is not there. The slots that it keeps for a document of the same id in the trash are not given. A key of the private
    /// folder of another user is an <see cref="ArgumentException"/>, as the copy gives none of its documents (M2.7c).
    /// </summary>
    public IReadOnlyList<Document> History(DocumentKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (!IsOwn(key.Account))
        {
            throw new ArgumentException("The local copy gives the history of the data folder and of the private folder of its user only.", nameof(key));
        }

        lock (_gate)
        {
            var history = new List<Document>();
            foreach (var (slot, entry) in _history.Where(slot => slot.Key.Key == key && !slot.Key.InTrash).OrderByDescending(slot => slot.Key.Version))
            {
                if (ReadLastGood(SlotPath(slot), new KeptCopy(slot.Version, entry.Hash, entry.Length, entry.Own), out var bytes) == Keep.Done
                    && DocumentFormat.Read(key, bytes) is { Document: { } document }
                    && document.Version == slot.Version)
                {
                    history.Add(document);
                }
            }

            return history;
        }
    }

    /// <summary>
    /// The name of the history folder of a document in a report: <c>history\records\8f3a1c</c>, <c>history\settings</c>,
    /// <c>private\demo.user\history\records\8f3a1c</c> (M2.7b).
    /// </summary>
    private static string HistoryName(DocumentKey key) => Path.Join(RootName(key.Account), DataFolder.History, key.Folder, key.Id);

    /// <summary>The name of a slot in a report, as the layer names it: <c>v6.json</c>.</summary>
    private static string SlotName(int version) => "v" + version.ToString(CultureInfo.InvariantCulture) + DataFolder.Extension;

    /// <summary>
    /// Lists the history folder of each document that has changed since the copy last took its history
    /// (<see cref="CopyEntry.HistoryFrom"/>), and takes the good slots from that version up to the newest version that the
    /// copy has of the document, the newest first, at most <see cref="SlotsPerListing"/> of them and
    /// <see cref="SlotBytesPerListing"/> by their listing, and as far as the budget of the history goes: room is made only for
    /// a slot that was read good, by the slots of the share that the copy took first, never by those of this Kiler (decided in
    /// M2.7a, by the user). A version that the copy has the bytes of, in either place, is not taken from the share: its slot
    /// comes from those bytes once a new version replaces it, as anyone can put a slot of it there first (second security
    /// review of M2.7a). A slot that the copy has is not read again, one that has gone from the share stays in the copy, and a
    /// damaged one is skipped. A history folder that cannot be listed, or a slot that cannot be read or written, is reported,
    /// and listed again with a back-off (<see cref="BackOff"/>, M2.8b, decided by the user), after those that have not failed;
    /// meanwhile, and while the caps or another rule leave it out, every load reports the failures of its last try, as the
    /// skipped entries of a load are the state of the share as the copy knows it (M2.8); its slots over the budget, and a
    /// listing of too many entries, are reported once, by the try. One load lists at most
    /// <see cref="CopyLimits.HistoryListings"/> history folders and reads at most <see cref="CopyLimits.HistoryReads"/> of
    /// slots, as anyone can make every document change and every slot unreadable; the others wait for the next load. The
    /// history of a document of the private folder waits too when this load did not list that folder (<paramref name="privateFolder"/>,
    /// M2.7b). The private folders of other users have no history in the copy, also when an entry of one has a
    /// <see cref="CopyEntry.HistoryFrom"/> left from when it was the user's own (M2.7c). Gives whether the index has changed.
    /// </summary>
    private bool TakeHistory(DataFolder? privateFolder, DateTimeOffset now, Report report)
    {
        var keys = _index
            .Where(entry => entry.Value.HistoryFrom is not null && IsOwn(entry.Key.Key.Account))
            .Select(entry => entry.Key.Key)
            .Distinct()
            .OrderBy(_historyFailed.ContainsKey)
            .ToList();

        // A document that has left the index is not tried again (third security review of M2.7a).
        foreach (var gone in _historyFailed.Keys.Except(keys).ToList())
        {
            _historyFailed.Remove(gone);
        }

        HashSet<DocumentKey> tried = [];
        var taken = false;
        var listings = 0;
        long read = 0;
        foreach (var key in keys)
        {
            // It waits without taking the place of another in the caps of the load.
            if (_historyFailed.TryGetValue(key, out var retry) && !retry.BackOff.IsDue(now))
            {
                continue;
            }

            if (listings == _limits.HistoryListings || read >= _limits.HistoryReads)
            {
                break;
            }

            if ((key.Account is null ? _share : privateFolder) is not { } root)
            {
                continue;
            }

            // Two documents of one id, in their folder and in the trash, share a history folder, and which slot is whose
            // cannot be told: anyone could put one there to have slots of versions that the other reaches later taken. The
            // copy takes the slots of their versions from its own bytes, and the share's once one of them has gone (third
            // security review of M2.7a).
            CopyEntry?[] entries = [_index.GetValueOrDefault(new Spot(key, InTrash: false)), _index.GetValueOrDefault(new Spot(key, InTrash: true))];
            if (entries.All(entry => entry is { Gone: false }))
            {
                continue;
            }

            listings++;
            tried.Add(key);
            List<SkippedEntry> failed = [];
            var from = entries.Min(entry => entry?.HistoryFrom ?? int.MaxValue);

            // The version of a file that is not a version that Kiler made says nothing.
            var top = entries.Max(entry => entry?.LastGood?.Version ?? 0);
            HashSet<int> held = [.. entries.SelectMany(entry => (int[])[entry is { Copied: > 0 } ? entry.Version : 0, entry?.Kept?.Version ?? 0])];
            var name = HistoryName(key);

            // The slots go to the history of the document that is there: the one in the trash when it alone is.
            var inTrash = entries[0] is not { Gone: false } && entries[1] is { Gone: false };
            var room = Math.Min(SlotBytesPerListing, _limits.HistoryReads - read);
            var cut = false;
            HistoryLoad load;
            try
            {
                load = root.LoadHistory(
                    key,
                    slots =>
                    {
                        var chosen = ChooseSlots(key, inTrash, from, top, held, room, slots, out var more);

                        // Only what the budget of the load left out waits for the next load; what one listing leaves is not taken.
                        cut = more && room < SlotBytesPerListing;
                        return chosen;
                    },
                    _limits.Listed,
                    now);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed.Add(new SkippedEntry(name, null, SkipReasonOf(ex)));
                Failed(key, failed, report, now);
                continue;
            }

            read += load.Slots.Sum(slot => slot.Slot.File.Size);

            if (load.TooMany)
            {
                report.Skip(new SkippedEntry(name, null, SkipReason.OverBudget));
            }

            var plan = Budget.Plan();
            var fit = new List<LoadedSlot>();
            foreach (var slot in load.Slots)
            {
                if (slot.Unreadable)
                {
                    failed.Add(new SkippedEntry(name, StorageName.Escape(slot.Slot.File.Name), SkipReason.Unreadable));
                }
                else if (slot.Good && plan.Fits(slot.Bytes.Length, own: false))
                {
                    fit.Add(slot);
                }
                else if (slot.Good)
                {
                    report.Skip(name, [slot.Slot.File], SkipReason.OverBudget);
                }
            }

            Budget.Commit(plan);
            ForgetSlots(plan.Evicted);
            var written = Workers.Run(fit, DataFolder.LoadReads, slot => TryWriteFile(SlotPath(new SlotKey(key, inTrash, slot.Slot.Version)), slot.Bytes));
            for (var i = 0; i < fit.Count; i++)
            {
                var slot = fit[i];
                if (written[i])
                {
                    _history[new SlotKey(key, inTrash, slot.Slot.Version)] = new SlotEntry(key.Account, key.Folder, key.Id, inTrash, slot.Slot.Version, Hash(slot.Bytes), slot.Bytes.Length, now, Own: false, FromShare: true);
                }
                else
                {
                    failed.Add(new SkippedEntry(name, StorageName.Escape(slot.Slot.File.Name), SkipReason.NotCopied));
                    Budget.Release(slot.Bytes.Length);
                }
            }

            if (failed.Count > 0)
            {
                Failed(key, failed, report, now);
            }
            else
            {
                _historyFailed.Remove(key);
            }

            if (failed.Count == 0 && !cut)
            {
                foreach (var spot in (Spot[])[new(key, InTrash: false), new(key, InTrash: true)])
                {
                    if (_index.TryGetValue(spot, out var entry) && entry.HistoryFrom is not null)
                    {
                        _index[spot] = entry with { HistoryFrom = null };
                    }
                }
            }

            taken = true;
        }

        foreach (var (key, retry) in _historyFailed)
        {
            if (!tried.Contains(key))
            {
                foreach (var entry in retry.Skipped)
                {
                    report.Skip(entry);
                }

                report.Untold(retry.Untold);
            }
        }

        return taken;
    }

    /// <summary>
    /// Reports what the try of a history folder that failed left, and keeps it until the next try, which waits one time more
    /// (M2.8b). All the history folders that wait keep at most as many skipped entries as one load gives, and count the rest,
    /// as anyone can make every slot unreadable.
    /// </summary>
    private void Failed(DocumentKey key, List<SkippedEntry> skipped, Report report, DateTimeOffset now)
    {
        foreach (var entry in skipped)
        {
            report.Skip(entry);
        }

        _historyFailed.Remove(key, out var last);
        var room = Math.Max(0, _limits.Reports - _historyFailed.Values.Sum(retry => retry.Skipped.Length));
        SkippedEntry[] kept = [.. skipped.Take(room)];
        _historyFailed[key] = new HistoryRetry((last?.BackOff ?? default).Again(now), kept, skipped.Count - kept.Length);
    }

    /// <summary>
    /// The slots of a listing of a history folder to read, as <see cref="TakeHistory"/> chooses them; the budget decides
    /// after the reads.
    /// </summary>
    private List<ListedSlot> ChooseSlots(DocumentKey key, bool inTrash, int from, int top, HashSet<int> held, long room, IReadOnlyList<ListedSlot> slots, out bool more)
    {
        var chosen = new List<ListedSlot>();
        long bytes = 0;
        more = false;
        var candidates = slots
            .Where(slot => slot.Version >= from
                && slot.Version <= top
                && !held.Contains(slot.Version)
                && slot.File.Size <= DocumentFormat.MaxSize
                && !_history.ContainsKey(new SlotKey(key, inTrash, slot.Version)))
            .OrderByDescending(slot => slot.Version);
        foreach (var slot in candidates)
        {
            if (chosen.Count == SlotsPerListing || bytes + slot.File.Size > room)
            {
                more = true;
                break;
            }

            chosen.Add(slot);
            bytes += slot.File.Size;
        }

        return chosen;
    }

    /// <summary>
    /// Takes the slot of a version that a replace or a delete of this Kiler changed into the history of the copy, as this
    /// Kiler's own (decided in M2.7a, by the user), as far as the budget of the history goes, the slots that the copy took
    /// first making room once it is written. Gives whether it took it.
    /// </summary>
    private bool TakeOwnSlot(DocumentKey key, int version, byte[] bytes, DateTimeOffset now)
    {
        // A slot read from the share with other bytes gives way to those of this Kiler (third security review of M2.7a), once
        // they fit and are written: the new slot takes its key.
        var slot = new SlotKey(key, InTrash: false, version);
        if (_history.TryGetValue(slot, out var had) && (!had.FromShare || had.Hash == Hash(bytes)))
        {
            return false;
        }

        // Flushed, as the index that names it lets the bytes of that version in data\ be overwritten.
        var plan = new HistoryBudget(_history, _limits).Plan();
        if (!plan.Fits(bytes.Length, own: true) || !TryWriteFile(SlotPath(slot), bytes, flush: true))
        {
            return false;
        }

        ForgetSlots(plan.Evicted);
        _history[slot] = new SlotEntry(key.Account, key.Folder, key.Id, InTrash: false, version, Hash(bytes), bytes.Length, now, Own: true, FromShare: false);
        _indexBehind = true;
        return true;
    }

    /// <summary>
    /// Takes slots out of the history of the copy, each only while it is still the slot that was planned to go, as another
    /// can have taken its key since the budget was made (fourth security review of M2.7a); their files go with the next index.
    /// </summary>
    private void ForgetSlots(IEnumerable<KeyValuePair<SlotKey, SlotEntry>> slots)
    {
        foreach (var (slot, entry) in slots)
        {
            if (_history.TryGetValue(slot, out var current) && current == entry)
            {
                _history.Remove(slot);
                _stale.Add(SlotPath(slot));
            }
        }
    }

    /// <summary>
    /// A history folder that failed at its last try (M2.8b): when it is tried again, and what that try reported, with how many
    /// skipped entries it did not keep.
    /// </summary>
    private sealed record HistoryRetry(BackOff BackOff, SkippedEntry[] Skipped, int Untold);

    /// <summary>
    /// The budget of the history of the copy (decided in M2.7a, by the user): what its slots take, and which go first to make
    /// room, those that the copy took first. The slots of this Kiler (<see cref="SlotEntry.Own"/>) make room only for other
    /// slots of this Kiler, so junk on the share cannot push them out; the slots that a load takes are not among those that
    /// make room, so they never push each other out.
    /// </summary>
    private sealed class HistoryBudget
    {
        private readonly CopyLimits _limits;
        private readonly (KeyValuePair<SlotKey, SlotEntry>[] Slots, long[] From) _shared;
        private readonly (KeyValuePair<SlotKey, SlotEntry>[] Slots, long[] From) _own;
        private long _bytes;
        private int _count;
        private int _nextShared;
        private int _nextOwn;

        public HistoryBudget(Dictionary<SlotKey, SlotEntry> history, CopyLimits limits)
        {
            _limits = limits;
            _shared = Oldest(history, own: false);
            _own = Oldest(history, own: true);
            _bytes = _shared.From[0] + _own.From[0];
            _count = history.Count;
        }

        public HistoryPlan Plan() => new(this);

        /// <summary>Takes what a plan chose and made room with.</summary>
        public void Commit(HistoryPlan plan)
        {
            _bytes = plan.Bytes;
            _count = plan.Count;
            _nextShared = plan.NextShared;
            _nextOwn = plan.NextOwn;
        }

        /// <summary>Gives back the room of a slot that a plan chose and the load did not take.</summary>
        public void Release(long size)
        {
            _bytes -= size;
            _count--;
        }

        /// <summary>The slots of one kind, those that the copy took first first, with the bytes from each one to the end.</summary>
        private static (KeyValuePair<SlotKey, SlotEntry>[] Slots, long[] From) Oldest(Dictionary<SlotKey, SlotEntry> history, bool own)
        {
            KeyValuePair<SlotKey, SlotEntry>[] oldest = [.. history
                .Where(slot => slot.Value.Own == own)
                .OrderBy(slot => slot.Value.Taken)
                .ThenBy(slot => slot.Key.Key.ToString(), StringComparer.Ordinal)
                .ThenBy(slot => slot.Key.Version)];
            var from = new long[oldest.Length + 1];
            for (var i = oldest.Length - 1; i >= 0; i--)
            {
                from[i] = from[i + 1] + oldest[i].Value.Length;
            }

            return (oldest, from);
        }

        /// <summary>What a load or a write chooses, on top of what the budget has taken, and the slots it makes room with.</summary>
        public sealed class HistoryPlan
        {
            private readonly HistoryBudget _budget;

            public HistoryPlan(HistoryBudget budget)
            {
                _budget = budget;
                Bytes = budget._bytes;
                Count = budget._count;
                NextShared = budget._nextShared;
                NextOwn = budget._nextOwn;
            }

            public long Bytes { get; private set; }

            public int Count { get; private set; }

            public int NextShared { get; private set; }

            public int NextOwn { get; private set; }

            public List<KeyValuePair<SlotKey, SlotEntry>> Evicted { get; } = [];

            /// <summary>
            /// Whether a slot fits, once as many of the oldest slots as it takes have gone: those of the share first, and those
            /// of this Kiler only for a slot of this Kiler. None go when even all that may would not do.
            /// </summary>
            public bool Fits(long size, bool own)
            {
                var limits = _budget._limits;
                var (shared, sharedFrom) = _budget._shared;
                var (mine, mineFrom) = _budget._own;
                var roomBytes = sharedFrom[NextShared] + (own ? mineFrom[NextOwn] : 0);
                var roomCount = shared.Length - NextShared + (own ? mine.Length - NextOwn : 0);
                if (Bytes + size - roomBytes > limits.HistoryBytes || Count + 1 - roomCount > limits.HistorySlots)
                {
                    return false;
                }

                while (Bytes + size > limits.HistoryBytes || Count + 1 > limits.HistorySlots)
                {
                    if (NextShared < shared.Length)
                    {
                        Bytes -= sharedFrom[NextShared] - sharedFrom[NextShared + 1];
                        Evicted.Add(shared[NextShared++]);
                    }
                    else
                    {
                        Bytes -= mineFrom[NextOwn] - mineFrom[NextOwn + 1];
                        Evicted.Add(mine[NextOwn++]);
                    }

                    Count--;
                }

                Bytes += size;
                Count++;
                return true;
            }
        }
    }
}

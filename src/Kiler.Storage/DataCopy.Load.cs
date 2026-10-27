namespace Kiler.Storage;

/// <summary>
/// The load of the local copy (M2.7, M2.7a): the listings of every folder of the share, one plan for all of them, the reads,
/// and the state that each read gives its document, with its last good bytes put where they are kept.
/// </summary>
public sealed partial class DataCopy
{
    /// <summary>
    /// One load of the share. The root documents, the document folders and their folders in the trash are each held and
    /// listed once, those of the data folder and then those of the private folder of the current user (M2.7b), and what to
    /// read is chosen for all of them at once, so a document that moved to the trash or back between
    /// two loads is followed with what the copy keeps of it; then each folder is read and taken into the copy (the new and
    /// changed files that fit the budget, read at most <see cref="DataFolder.LoadReads"/> at a time, M2.7). A document that
    /// the copy had and the share no longer lists, and that did not move, also when its folder has gone, is gone: its last
    /// good bytes are kept apart. The history folder of each document that has changed is then listed, and its new slots
    /// taken (decided in M2.7a). Links, names that fail the check, files that cannot be read, documents over the budget and
    /// bytes that cannot be written into the copy are skipped and reported; the next load tries them again. A folder that
    /// cannot be listed, such as one that is a link, or that lists too many entries, is reported, and its documents stay as
    /// the copy has them, and so is the private folder (<see cref="ListPrivate"/>). The root of the data folder that cannot be
    /// held or listed is an <see cref="IOException"/> or an <see cref="UnauthorizedAccessException"/>: the share cannot be
    /// reached (M2.8). Any other exception is a fault of the
    /// code: what the load took before it is written into the index first, so a start gives it. The index is written once,
    /// after the files, and before them only when the last good bytes of a document went apart and its file is overwritten.
    /// The old temporary files of the listings are deleted, on the clock given to
    /// <see cref="Open(DataFolder, string, TimeProvider, string?)"/>, which also times when the copy took a document and when it went,
    /// and the changes outside Kiler until they have settled.
    /// </summary>
    public LoadResult Load()
    {
        var now = _time.GetUtcNow();
        var report = new Report(_limits.Reports);
        var listed = new List<DataFolder.ListedFolder>();
        var missing = new List<FolderKey>();
        var taken = false;
        _budget = null;
        try
        {
            foreach (var (folder, inTrash) in FoldersOf(null))
            {
                try
                {
                    listed.Add(_share.ListFolder(folder, inTrash, _limits.Listed, _limits.Reports));
                }
                catch (Exception ex) when ((folder is not null || inTrash) && ex is IOException or UnauthorizedAccessException)
                {
                    report.Skip(new SkippedEntry(FolderName(null, folder, inTrash), null, SkipReason.Unreadable));
                }
            }

            var privateFolder = ListPrivate(listed, missing, report);
            var plan = Choose(listed, missing, now);
            foreach (var folder in listed)
            {
                using (folder)
                {
                    taken |= TakeFolder(folder, plan, now, report);
                }
            }

            plan.Loaded.UnionWith(missing);
            taken |= Leave(plan, now, report);
            taken |= TrimToBudget(plan);
            taken |= TakeHistory(privateFolder, now, report);
            taken |= ReportOutside(now, report);
        }
        catch
        {
            if (taken || _indexBehind)
            {
                _indexBehind = !TryWriteIndex();
            }

            throw;
        }
        finally
        {
            foreach (var folder in listed)
            {
                folder.Dispose();
            }

            _budget = null;
        }

        // Once, after the files it names; one that cannot be written is tried again at the next load.
        if (taken || _indexBehind)
        {
            _indexBehind = !TryWriteIndex();
            if (_indexBehind)
            {
                report.Skip(new SkippedEntry(null, null, SkipReason.NotCopied));
            }
        }

        return report.Result();
    }

    /// <summary>
    /// The name of a folder of a load in a report: the document folder, <c>null</c> for the root of the data folder, or its
    /// folder in the trash; in a private folder, after <c>private\&lt;account&gt;</c> (M2.7b).
    /// </summary>
    private static string? FolderName(string? account, string? folder, bool inTrash)
    {
        var name = Path.Join(RootName(account), inTrash ? DataFolder.Trash : null, folder);
        return name.Length == 0 ? null : name;
    }

    /// <summary>The name of a root in a report: <c>null</c> for the data folder, <c>private\&lt;account&gt;</c> for a private folder.</summary>
    private static string? RootName(string? account) => account is null ? null : Path.Join(DataFolder.Private, account);

    private static FolderKey FolderOf(Spot spot) => new(spot.Key.Account, spot.Key.Folder, spot.InTrash);

    private static FolderKey FolderOf(DataFolder.ListedFolder folder) => new(folder.Account, folder.Folder, folder.InTrash);

    /// <summary>
    /// Whether a document that the share stopped listing is still one whose move a listing can show late, through the SMB
    /// cache of about 10 s: gone for less than <see cref="InPlaceEdits.SettleTime"/>, and not reported yet. Later, a file
    /// in the other place is not taken for it, as anyone could put one there to make it a move (second security review of
    /// M2.7a).
    /// </summary>
    private static bool JustGone(CopyEntry entry, DateTimeOffset now) =>
        entry.GoneAt is { } went && now - went < InPlaceEdits.SettleTime && !entry.Reported;

    /// <summary>
    /// Whether a document that moved may take the place of what the copy has in the place it moved to: nothing, or an entry
    /// that a late listing of the move leaves (<see cref="IsLateListing"/>), with no last good bytes or the same ones as the
    /// document that moved, and none that this Kiler wrote, as its current bytes or kept apart (fifth security review). Any other keeps what it has, its last good bytes, its stay and
    /// whether this Kiler wrote them, and the file is read as a change of it: else anyone could put a file in one place and
    /// take it away again to change what the copy keeps of a real document in the other (third and fourth security reviews of
    /// M2.7a).
    /// </summary>
    private static bool CanTakeThePlaceOf(CopyEntry? entry, CopyEntry from, DateTimeOffset now) =>
        entry is null || (IsLateListing(entry, now) && entry.LastGood is not { Own: true } && (entry.LastGood is not { } there || there.Hash == from.LastGood?.Hash));

    /// <summary>
    /// Whether the copy took an entry less than <see cref="InPlaceEdits.SettleTime"/> ago and has not reported it, as the
    /// listing of a move that the SMB cache shows late leaves the document for a while in both places.
    /// </summary>
    private static bool IsLateListing(CopyEntry entry, DateTimeOffset now) => now - entry.Taken < InPlaceEdits.SettleTime && !entry.Reported;

    /// <summary>
    /// What the plan counts a document at once a listed file of it is read, from what the copy has of it
    /// (<paramref name="lineage"/>): the file, and the last good bytes beside it when it looks edited in place. A file that
    /// looks as Kiler wrote it counts the larger of the two, as it turns out a new version, the last good version again, or
    /// damaged (<see cref="Cost(CopyEntry)"/>). When it turns out at a version that is not after the last good one, it needs
    /// both, and the load finds the room in what the plan left, or leaves it (<see cref="Plan.Slack"/>).
    /// </summary>
    private static long Planned(FileEntry file, CopyEntry? lineage)
    {
        var size = Cost(file.Size);
        var lastGood = lineage?.LastGood?.Length ?? 0;
        return file.Stamp.LooksEditedInPlace ? size + lastGood : Math.Max(size, lastGood);
    }

    /// <summary>
    /// The state of a document once a file of it was read, from what the copy had of it, in the same place or in the place it
    /// moved from (<paramref name="lineage"/>, decided in M2.7a). The bytes of its last good version keep it good, or make it
    /// good again. Otherwise it is a change outside Kiler, and its last good bytes are kept apart, when it is damaged, looks
    /// edited in place, or is at a version that is not after the last good one; else a new version, whose history the next
    /// listing of the history folder takes from the last good version on. The slot of a delete that moved the bytes of the
    /// copy to the trash is not taken from the share, as the copy has those bytes in its trash, and takes them as a slot
    /// once a new version replaces them (second security review of M2.7a). A document that the copy did not have is good,
    /// with no history taken (decided in M2.7a, by the user).
    /// </summary>
    private static CopyEntry Next(Spot spot, CopyEntry? lineage, Seen seen, DateTimeOffset now)
    {
        var lastGood = lineage?.LastGood;
        var damaged = seen.Damage is not null;
        OutsideReason? outside;
        KeptCopy? kept = null;
        var own = false;
        var historyFrom = lineage?.HistoryFrom;
        if (damaged)
        {
            outside = OutsideReason.Damaged;
            kept = lastGood;
        }
        else if (lastGood is not null && seen.Hash == lastGood.Hash)
        {
            outside = null;
            own = lastGood.Own;
        }
        else if (seen.LooksEditedInPlace)
        {
            outside = OutsideReason.EditedInPlace;
            kept = lastGood;
        }
        else if (lastGood is null || seen.Version > lastGood.Version)
        {
            outside = null;
            if (lastGood is not null)
            {
                historyFrom = Lower(historyFrom, lastGood.Version);
            }
        }
        else
        {
            outside = OutsideReason.OutOfOrder;
            kept = lastGood;
        }

        return new CopyEntry
        {
            Account = spot.Key.Account,
            Folder = spot.Key.Folder,
            Id = spot.Key.Id,
            InTrash = spot.InTrash,
            Size = seen.Stamp.Size,
            CreationTime = seen.Stamp.CreationTime,
            LastWriteTime = seen.Stamp.LastWriteTime,
            Owner = seen.Owner,
            Damage = seen.Damage,
            Taken = TakenAt(lineage, now),
            Version = damaged ? 0 : seen.Version,
            Hash = damaged ? null : seen.Hash,
            Copied = damaged ? 0 : seen.Length,
            Own = own && !damaged,
            Outside = outside,
            Kept = outside is null ? null : kept,
            Reported = outside is not null && outside == lineage?.Outside && lineage?.Reported == true,
            HistoryFrom = historyFrom,
        };
    }

    /// <summary>The entry of a document that the share no longer lists and that did not move: gone, with its last good bytes kept apart, or <c>null</c> without any.</summary>
    private static CopyEntry? Went(CopyEntry entry, DateTimeOffset now) =>
        entry.LastGood is { } lastGood
            ? entry with
            {
                GoneAt = now,
                Damage = null,
                Version = 0,
                Hash = null,
                Copied = 0,
                Own = false,
                Outside = OutsideReason.Gone,
                Kept = lastGood,
                Reported = entry.Outside == OutsideReason.Gone && entry.Reported,
            }
            : null;

    /// <summary>
    /// The entry that the index names while the bytes of a version that Kiler made, in <c>data\</c>, are about to be
    /// overwritten by a change outside Kiler: its next state, with those bytes as its current ones, with their owner, and kept
    /// apart too, and a stamp that no listing gives, so a stop before the overwrite, or after it, leaves the last good bytes
    /// named (review of M2.7a).
    /// </summary>
    private static CopyEntry Guarded(CopyEntry next, CopyEntry lineage) =>
        next with
        {
            Size = 0,
            CreationTime = CopyEntry.Unseen,
            LastWriteTime = CopyEntry.Unseen,
            Owner = lineage.Owner,
            Version = lineage.Version,
            Hash = lineage.Hash,
            Copied = lineage.Copied,
            Own = lineage.Own,
        };

    /// <summary>
    /// The folders of a load of the data folder, or of the private folder of <paramref name="account"/>, in their order: the
    /// root and the document folders, then the same in the trash.
    /// </summary>
    private static IEnumerable<(string? Folder, bool InTrash)> FoldersOf(string? account)
    {
        foreach (var inTrash in (bool[])[false, true])
        {
            yield return (null, inTrash);
            foreach (var folder in DataFolder.DocumentFoldersOf(account))
            {
                yield return (folder, inTrash);
            }
        }
    }

    /// <summary>
    /// Lists the folders of the private folder of the current user (M2.7b), after those of the data folder, whose root the
    /// load holds by then. A private folder that is not there, not made yet or gone, gives its folders as listed whole and
    /// empty (<paramref name="missing"/>), so what the copy had of it is gone, with its last good bytes kept apart. One that
    /// cannot be opened or listed, such as one that is a link, is reported, and its documents stay as the copy has them; so
    /// does a folder of it that cannot be listed. Gives the private folder, or <c>null</c> when it was not listed.
    /// </summary>
    private DataFolder? ListPrivate(List<DataFolder.ListedFolder> listed, List<FolderKey> missing, Report report)
    {
        if (_account is not { } account)
        {
            return null;
        }

        try
        {
            var own = _share.OpenPrivate(account);
            foreach (var (folder, inTrash) in FoldersOf(account))
            {
                try
                {
                    listed.Add(own.ListFolder(folder, inTrash, _limits.Listed, _limits.Reports));
                }
                catch (Exception ex) when ((folder is not null || inTrash) && ex is IOException or UnauthorizedAccessException)
                {
                    report.Skip(new SkippedEntry(FolderName(account, folder, inTrash), null, SkipReason.Unreadable));
                }
            }

            return own;
        }
        catch (DirectoryNotFoundException)
        {
            missing.AddRange(FoldersOf(account).Select(place => new FolderKey(account, place.Folder, place.InTrash)));
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            report.Skip(new SkippedEntry(RootName(account), null, SkipReason.Unreadable));
            return null;
        }
    }

    /// <summary>
    /// What to read of the listed folders, chosen for all of them at once: the new documents and the changed ones, and those
    /// that moved to the trash or back, as far as the budget goes. A document moved when the copy has it in the other place,
    /// that place no longer lists it, or listed it no more less than 30 s ago, and this one lists it new or changed: it
    /// is read as a change of the document from the other place, and it takes the place of what the copy had in this one,
    /// when that is nothing or was taken less than 30 s ago (<see cref="CanTakeThePlaceOf"/>), once a document a load. The SMB
    /// client caches the listing of each folder for about 10 s, so the move can show in two loads (<see cref="JustGone"/>). What the copy
    /// keeps takes its part first: the entries that are still listed, or whose folder was not listed whole, and the last good
    /// bytes of those that are no longer listed. A changed document, one that is listed again after it went, one that moved,
    /// and one whose file in the copy could not be read at the start, are read when they fit at what the plan counts them
    /// (<see cref="Planned"/>), else they keep what the copy has; a new one when it fits and the copy has room for one more.
    /// To make them fit, the last good bytes of gone documents that were in the copy for less than <see cref="BriefStay"/>,
    /// that this Kiler did not write and that neither place lists, go, those that went first first, but only as many as it
    /// takes, and none when even all of them would not make it fit (<see cref="FolderPlan.Evicted"/>). The others go into
    /// <see cref="FolderPlan.OverBudget"/>.
    /// </summary>
    private Plan Choose(IReadOnlyList<DataFolder.ListedFolder> listed, IEnumerable<FolderKey> missing, DateTimeOffset now)
    {
        var plan = new Plan();
        plan.Whole.UnionWith(missing);
        foreach (var folder in listed.Where(folder => !folder.TooMany))
        {
            plan.Whole.Add(FolderOf(folder));
            foreach (var document in folder.Listing.Documents.Concat(folder.Listing.Unread))
            {
                plan.Listed.Add(new Spot(document.Key, folder.InTrash));
            }
        }

        long bytes = 0;
        var count = 0;
        foreach (var (spot, entry) in _index)
        {
            // An entry that is no longer listed becomes gone with its last good bytes, or moves with them, or goes without any.
            if (plan.IsLeaving(spot, entry))
            {
                if (entry.LastGood is { } lastGood)
                {
                    bytes += lastGood.Length;
                    count++;
                }
            }
            else
            {
                bytes += Cost(entry);
                count++;
            }
        }

        var evictable = new Queue<KeyValuePair<Spot, CopyEntry>>(_index
            .Where(entry => entry.Value.GoneAt is { } went
                && went - entry.Value.Taken < BriefStay
                && entry.Value.Kept is { Own: false }
                && !plan.Listed.Contains(entry.Key)
                && !plan.Listed.Contains(entry.Key with { InTrash = !entry.Key.InTrash }))
            .OrderBy(entry => entry.Value.GoneAt));
        var evictableBytes = evictable.Sum(entry => Cost(entry.Value));

        // Whether the copy has room for more bytes and documents, once as many brief leftovers as it takes have gone.
        bool Fits(long more, int documents, FolderPlan folderPlan)
        {
            if (bytes + more - evictableBytes > _limits.Bytes || count + documents - evictable.Count > _limits.Documents)
            {
                return false;
            }

            while (bytes + more > _limits.Bytes || count + documents > _limits.Documents)
            {
                var gone = evictable.Dequeue();
                bytes -= Cost(gone.Value);
                evictableBytes -= Cost(gone.Value);
                count--;
                folderPlan.Evicted.Add(gone.Key);
            }

            return true;
        }

        foreach (var folder in listed.Where(folder => !folder.TooMany))
        {
            var folderPlan = plan.For(folder);
            var added = new List<ListedDocument>();
            foreach (var document in folder.Listing.Documents)
            {
                var spot = new Spot(document.Key, folder.InTrash);
                var other = spot with { InTrash = !spot.InTrash };
                _index.TryGetValue(spot, out var entry);
                var changed = entry is not null && (entry.Gone || entry.Stamp != document.File.Stamp || _reread.Contains(spot));
                // At most one move of a document a load: two that cross would each take the other's entry. A gone document that
                // its own place lists again is no move.
                if (_index.TryGetValue(other, out var from)
                    && (plan.IsLeaving(other, from) || (JustGone(from, now) && !plan.Listed.Contains(other)))
                    && (entry is null || changed)
                    && CanTakeThePlaceOf(entry, from, now)
                    && !plan.Moves.ContainsKey(other))
                {
                    // Its cost replaces what the one that moved was counted at. What this place had still counts, as a move that
                    // fails after the plan leaves both, and an index over the budget would be no index (fourth security review).
                    var more = Planned(document.File, from) - (from.LastGood?.Length ?? 0);
                    var moreDocuments = from.LastGood is null ? 1 : 0;
                    if (Fits(more, moreDocuments, folderPlan))
                    {
                        bytes += more;
                        count += moreDocuments;
                        plan.Moves[spot] = other;
                        folderPlan.Read.Add(document);
                    }
                    else
                    {
                        folderPlan.OverBudget.Add(document);
                    }
                }
                else if (entry is null)
                {
                    added.Add(document);
                }
                else if (changed)
                {
                    var more = Planned(document.File, entry) - Cost(entry);
                    if (Fits(more, 0, folderPlan))
                    {
                        bytes += more;
                        folderPlan.Read.Add(document);
                    }
                    else
                    {
                        folderPlan.OverBudget.Add(document);
                    }
                }
            }

            foreach (var document in added)
            {
                if (Fits(Cost(document.File.Size), 1, folderPlan))
                {
                    bytes += Cost(document.File.Size);
                    count++;
                    folderPlan.Read.Add(document);
                }
                else
                {
                    folderPlan.OverBudget.Add(document);
                }
            }
        }

        plan.Slack = Math.Max(0, _limits.Bytes - bytes);
        return plan;
    }

    /// <summary>
    /// Reads a listed folder and takes it into the copy (<see cref="Take"/>). A folder that lists too many entries, or that
    /// cannot be read, such as one made a link before its reads were checked, is reported and nothing of it is taken; the
    /// root of the data folder throws, and that of a private folder is reported too (M2.7b). Gives whether the index has
    /// changed.
    /// </summary>
    private bool TakeFolder(DataFolder.ListedFolder folder, Plan plan, DateTimeOffset now, Report report)
    {
        var name = FolderName(folder.Account, folder.Folder, folder.InTrash);
        if (folder.TooMany)
        {
            report.Skip(new SkippedEntry(name, null, SkipReason.OverBudget));
            return false;
        }

        var folderPlan = plan.For(folder);
        IReadOnlyList<LoadedFile> files;
        try
        {
            files = folder.Source.ReadFolder(folder, folderPlan.Read, now);
        }
        catch (Exception ex) when ((folder.Account is not null || folder.Folder is not null || folder.InTrash) && ex is IOException or UnauthorizedAccessException)
        {
            report.Skip(new SkippedEntry(name, null, SkipReason.Unreadable));
            return false;
        }

        plan.Loaded.Add(FolderOf(folder));
        var taken = Evict(folderPlan.Evicted);
        report.Skip(name, folder.Listing.Links, SkipReason.Link);
        report.Skip(name, folder.Listing.BadNames, SkipReason.BadName);
        report.Untold(folder.Untold);
        report.Skip(name, folder.Listing.Unread.Select(document => document.File), SkipReason.Unreadable);
        report.Skip(name, folderPlan.OverBudget.Select(document => document.File), SkipReason.OverBudget);
        return Take(folder, files, plan, now, report) | taken;
    }

    /// <summary>Takes out of the copy the gone documents that <see cref="Choose"/> made room with. Gives whether there were any.</summary>
    private bool Evict(List<Spot> evicted)
    {
        foreach (var spot in evicted)
        {
            if (_index.Remove(spot, out var entry))
            {
                Stale(FilesOf(spot, entry), []);
            }
        }

        return evicted.Count > 0;
    }

    /// <summary>
    /// Takes the files of a folder that a load read into the copy, each as the next state of what the copy had of the
    /// document (<see cref="Next"/>). First the last good bytes that a change takes the place of go where they are kept,
    /// apart for a change outside Kiler, or as the slot of their version in the history of the copy for a new version, so
    /// that version is never left to the history of the share alone (security review of M2.7a); and when they were the
    /// bytes in <c>data\</c> that a new file overwrites, an index that names where they went is written first
    /// (<see cref="Guarded"/>). Then the bytes of the files that are not in the copy already, at most
    /// <see cref="DataFolder.LoadReads"/> at a time, as an antivirus makes each new file slow (2000 one by one took about 5 s
    /// on the developer machine, at once about 1 s), then the entries; the files that no entry names any more go with the
    /// next index. A document that moved leaves its other place. A file that cannot be written, or a document that turns out
    /// to need more room than the budget has left, keeps its entry as it was, so the next load reads it again, and is given
    /// all the same. Gives whether the index has changed.
    /// </summary>
    private bool Take(DataFolder.ListedFolder folder, IReadOnlyList<LoadedFile> files, Plan plan, DateTimeOffset now, Report report)
    {
        var name = FolderName(folder.Account, folder.Folder, folder.InTrash);
        var changes = new List<Change>();
        foreach (var file in files)
        {
            if (file.Unreadable)
            {
                report.Skip(name, [file.Listed.File], SkipReason.Unreadable);
                continue;
            }

            // A file that has gone since the listing is gone at the next one.
            if (file.Read is not { } read)
            {
                continue;
            }

            var spot = new Spot(file.Listed.Key, folder.InTrash);
            var lineageSpot = plan.Moves.GetValueOrDefault(spot, spot);
            _index.TryGetValue(lineageSpot, out var lineage);
            _index.TryGetValue(spot, out var replaced);
            var seen = new Seen(
                file.Listed.File.Stamp, read.Owner, read.Damage, read.Document?.Version ?? 0, read.IsDamaged ? null : Hash(file.Bytes), file.Bytes.Length, file.Listed.File.Stamp.LooksEditedInPlace);
            var next = Next(spot, lineage, seen, now);

            // The cost that the plan did not foresee: a version that is not after the last good one, which keeps both.
            var extra = Cost(next) - Planned(file.Listed.File, lineage);
            if (extra > plan.Slack)
            {
                report.Skip(name, [file.Listed.File], SkipReason.OverBudget);
                report.Given(spot, read);
                continue;
            }

            plan.Slack -= Math.Max(0, extra);
            changes.Add(new Change(spot, lineageSpot, lineage, replaced, next, file, read));
        }

        // A file that an earlier index stopped naming can be named again here; the guarded index must not delete it.
        var apart = changes.Select(PlanApart).ToList();
        _stale.ExceptWith(apart.Where(job => job.To is not null).Select(job => job.To!));
        var copied = Workers.Run(apart, DataFolder.LoadReads, job => job.LastGood is { } lastGood ? CopyLastGood(job.From!, job.To!, lastGood) : Keep.Done);
        var failed = new bool[changes.Count];
        var guarded = new List<int>();
        for (var i = 0; i < changes.Count; i++)
        {
            if (apart[i] is not { LastGood: { } lastGood } job)
            {
                // An index on the disk that is behind may not name a slot of the copy yet.
                if (_indexBehind && OverwritesLastGood(changes[i]))
                {
                    guarded.Add(i);
                }

                continue;
            }

            if (job.Slot is { } slot)
            {
                if (copied[i] == Keep.Done)
                {
                    _history[slot] = new SlotEntry(slot.Key.Account, slot.Key.Folder, slot.Key.Id, slot.InTrash, slot.Version, lastGood.Hash, lastGood.Length, now, lastGood.Own, FromShare: false);
                }
                else
                {
                    Budget.Release(lastGood.Length);
                }
            }
            else if (copied[i] == Keep.Failed)
            {
                failed[i] = true;
                continue;
            }
            else if (copied[i] == Keep.Lost)
            {
                changes[i] = changes[i] with { Next = changes[i].Next with { Kept = null } };
            }

            if ((copied[i] == Keep.Done || _indexBehind) && OverwritesLastGood(changes[i]))
            {
                guarded.Add(i);
            }
        }

        if (guarded.Count > 0)
        {
            foreach (var i in guarded.Where(i => apart[i] is { LastGood: not null, Slot: null } && copied[i] == Keep.Done))
            {
                _index[changes[i].Spot] = Guarded(changes[i].Next, changes[i].Lineage!);
            }

            if (!TryWriteIndex())
            {
                foreach (var i in guarded)
                {
                    _index[changes[i].Spot] = changes[i].Lineage!;
                    failed[i] = true;
                }

                _indexBehind = true;
            }
        }

        var written = Workers.Run(
            [.. Enumerable.Range(0, changes.Count)],
            DataFolder.LoadReads,
            i => !failed[i] && (!NeedsWrite(changes[i]) || TryWriteFile(CopyPath(changes[i].Spot), changes[i].File.Bytes, flush: changes[i].LineageSpot != changes[i].Spot)));

        var taken = guarded.Count > 0;
        for (var i = 0; i < changes.Count; i++)
        {
            var change = changes[i];
            if (!written[i])
            {
                // A guarded entry stays as it was guarded, and its stamp makes the next load read the file again.
                report.Skip(name, [change.File.Listed.File], SkipReason.NotCopied);
                report.Given(change.Spot, change.Read);
                continue;
            }

            _index[change.Spot] = change.Next;
            if (change.LineageSpot != change.Spot)
            {
                _index.Remove(change.LineageSpot);
                _reread.Remove(change.LineageSpot);
                if (!change.LineageSpot.InTrash && !change.Lineage!.Gone)
                {
                    report.Gone.Add(change.LineageSpot.Key);
                }
            }

            Stale(FilesOf(change.LineageSpot, change.Lineage).Concat(FilesOf(change.Spot, change.Replaced)), FilesOf(change.Spot, change.Next));
            _reread.Remove(change.Spot);
            report.Given(change.Spot, change.Read);
            taken = true;
        }

        return taken;
    }

    /// <summary>
    /// Where the last good bytes that a change takes the place of go: apart, for a change outside Kiler; as the slot of their
    /// version in the history of the copy, for a new version, when the copy has no slot of that version and its budget has
    /// room, those of this Kiler only from each other (decided in M2.7a, by the user); else nowhere.
    /// </summary>
    private ApartJob PlanApart(Change change)
    {
        if (change.Next.Kept is { } lastGood)
        {
            return new ApartJob(LastGoodPath(change.LineageSpot, change.Lineage!), KeptPath(change.Spot), lastGood, null);
        }

        if (change.Next.Outside is not null || change.Lineage?.LastGood is not { } previous || change.Next.Version <= previous.Version)
        {
            return default;
        }

        // The slot goes to the history of the place where the copy had that version, so two documents of one id, in their folder
        // and in the trash, which Kiler never makes, never take each other's slots (fifth security review of M2.7a). A slot read
        // from the share with other bytes gives way to the version that the copy had (third security review), once that fits:
        // the new slot takes its key.
        var slot = new SlotKey(change.Spot.Key, change.LineageSpot.InTrash, previous.Version);
        if (_history.TryGetValue(slot, out var had) && (!had.FromShare || had.Hash == previous.Hash))
        {
            return default;
        }

        // The listing of the history folder reports a slot that has no room.
        var plan = Budget.Plan();
        if (!plan.Fits(previous.Length, previous.Own))
        {
            return default;
        }

        Budget.Commit(plan);
        Forget(plan.Evicted);
        return new ApartJob(LastGoodPath(change.LineageSpot, change.Lineage!), SlotPath(slot), previous, slot);
    }

    /// <summary>Whether a change overwrites, in <c>data\</c>, the bytes of the version that Kiler made that the copy had there.</summary>
    private bool OverwritesLastGood(Change change) =>
        change.LineageSpot == change.Spot && change.Lineage is { Outside: null, Copied: > 0 } && NeedsWrite(change);

    /// <summary>Whether the bytes of a change must be written into <c>data\</c>: they are not there already, or the file there could not be read at the start.</summary>
    private bool NeedsWrite(Change change) =>
        change.Next.Copied > 0
        && (change.LineageSpot != change.Spot || change.Lineage is not { Copied: > 0 } lineage || lineage.Hash != change.Next.Hash || _reread.Contains(change.Spot));

    /// <summary>
    /// The documents that the copy had in folders that this load took whole, that the share no longer lists there, and that
    /// did not move to the other place: each gone, with its last good bytes kept apart, or out of the copy without any. One
    /// whose move could not be taken, as its file could not be read or written, was over the budget, or its other place was
    /// not listed whole, is gone too, and a load in the next <see cref="InPlaceEdits.SettleTime"/> can still follow its move
    /// before it is reported (<see cref="JustGone"/>): a wait without a bound would let anyone keep a delete unreported (third
    /// security review of M2.7a). One whose last good bytes the copy has in the other place, as the last good bytes of an
    /// entry there that a late listing of the move left, goes without them, as nothing is lost, and that entry keeps the
    /// earlier stay and whether this Kiler wrote them. Gives whether the index has changed.
    /// </summary>
    private bool Leave(Plan plan, DateTimeOffset now, Report report)
    {
        var taken = false;
        foreach (var (spot, entry) in _index.Where(entry => plan.Loaded.Contains(FolderOf(entry.Key)) && plan.IsLeaving(entry.Key, entry.Value)).ToList())
        {
            var other = spot with { InTrash = !spot.InTrash };
            // The other place took the move from a late listing: it keeps the earlier stay, and whether this Kiler wrote the bytes.
            // An older entry there could be a copy that anyone put there, to be taken away later (fourth security review).
            if (entry.LastGood is { } moved
                && plan.Listed.Contains(other)
                && _index.TryGetValue(other, out var there)
                && there.LastGood?.Hash == moved.Hash
                && IsLateListing(there, now))
            {
                var stay = there.Taken < entry.Taken ? there.Taken : entry.Taken;
                _index[other] = there.Outside is null
                    ? there with { Taken = stay, Own = there.Own || moved.Own }
                    : there with { Taken = stay, Kept = there.Kept! with { Own = there.Kept.Own || moved.Own } };
                _index.Remove(spot);
                Stale(FilesOf(spot, entry), []);
                _reread.Remove(spot);
                if (!spot.InTrash)
                {
                    report.Gone.Add(spot.Key);
                }

                taken = true;
                continue;
            }

            var went = Went(entry, now);
            if (went?.Kept is { } lastGood)
            {
                var kept = CopyLastGood(LastGoodPath(spot, entry), KeptPath(spot), lastGood);
                if (kept == Keep.Failed)
                {
                    report.Skip(new SkippedEntry(FolderName(spot.Key.Account, spot.Key.Folder, spot.InTrash), spot.Key.Id + DataFolder.Extension, SkipReason.NotCopied));
                    continue;
                }

                if (kept == Keep.Lost)
                {
                    went = null;
                }
            }

            if (went is null)
            {
                _index.Remove(spot);
            }
            else
            {
                _index[spot] = went;
            }

            Stale(FilesOf(spot, entry), FilesOf(spot, went));
            _reread.Remove(spot);
            if (!spot.InTrash)
            {
                report.Gone.Add(spot.Key);
            }

            taken = true;
        }

        return taken;
    }

    /// <summary>
    /// Takes the copy back within its budget when a load ends above it. The evictions that the plan made for a folder that
    /// could not then be read are not done, so that its leftovers stay for it (M2.7), while the folders after it may have
    /// used the room that they would have made (fifth security review of M2.7a). The brief leftovers that a plan may evict
    /// go, those that went first first, until the copy fits. Gives whether the index has changed.
    /// </summary>
    private bool TrimToBudget(Plan plan)
    {
        var bytes = _index.Values.Sum(Cost);
        var count = _index.Count;
        var taken = false;
        foreach (var (spot, entry) in _index
            .Where(entry => entry.Value.GoneAt is { } went
                && went - entry.Value.Taken < BriefStay
                && entry.Value.Kept is { Own: false }
                && !plan.Listed.Contains(entry.Key)
                && !plan.Listed.Contains(entry.Key with { InTrash = !entry.Key.InTrash }))
            .OrderBy(entry => entry.Value.GoneAt)
            .ToList())
        {
            if (bytes <= _limits.Bytes && count <= _limits.Documents)
            {
                break;
            }

            _index.Remove(spot);
            Stale(FilesOf(spot, entry), []);
            bytes -= Cost(entry);
            count--;
            taken = true;
        }

        return taken;
    }

    /// <summary>
    /// Reports the documents changed outside Kiler that no load has reported yet (<see cref="LoadResult.Outside"/>), each
    /// once, at most as many a load as skipped entries are, as anyone can make every document change. A document that the
    /// share no longer lists only once it has been gone for <see cref="InPlaceEdits.SettleTime"/>, as the listing of a move,
    /// or of a file that is back, can come late through the SMB cache; an edit in place only once it has settled
    /// (<see cref="InPlaceEdits"/>). The edits in place of documents that are not edited any more are forgotten. Gives
    /// whether the index has changed.
    /// </summary>
    private bool ReportOutside(DateTimeOffset now, Report report)
    {
        var taken = false;
        HashSet<DocumentKey> edited = [];
        HashSet<DocumentKey> editedInTrash = [];
        foreach (var (spot, entry) in _index.ToList())
        {
            var edits = spot.InTrash ? _trashEdits : _edits;
            if (entry.Outside == OutsideReason.EditedInPlace)
            {
                (spot.InTrash ? editedInTrash : edited).Add(spot.Key);
            }

            if (entry.Outside is not { } reason || entry.Reported || report.Outside.Count >= _limits.Reports)
            {
                continue;
            }

            var settled = reason switch
            {
                OutsideReason.Gone => entry.GoneAt is { } went && now - went >= InPlaceEdits.SettleTime,
                OutsideReason.EditedInPlace => edits.ShouldReport(spot.Key, entry.Stamp),
                _ => true,
            };
            if (!settled)
            {
                continue;
            }

            report.Outside.Add(new OutsideChange(spot.Key, spot.InTrash, reason, entry.Kept?.Version));
            _index[spot] = entry with { Reported = true };
            taken = true;
        }

        _edits.KeepOnly(edited);
        _trashEdits.KeepOnly(editedInTrash);
        return taken;
    }

    /// <summary>
    /// What a document file gave, for the next state of its entry, and whether it looks edited in place: by its stamp, or, for
    /// a move of this Kiler, whose stamp the copy has not seen, by what the copy had of the bytes it moved.
    /// </summary>
    private readonly record struct Seen(FileStamp Stamp, string? Owner, DocumentDamage? Damage, int Version, string? Hash, long Length, bool LooksEditedInPlace);

    /// <summary>Where the last good bytes that a change takes the place of are copied to: apart, or into a slot of the history.</summary>
    private readonly record struct ApartJob(string? From, string? To, KeptCopy? LastGood, SlotKey? Slot);

    /// <summary>A file that a load read, with what the copy had of its document and the state it takes.</summary>
    private sealed record Change(Spot Spot, Spot LineageSpot, CopyEntry? Lineage, CopyEntry? Replaced, CopyEntry Next, LoadedFile File, DocumentRead Read);
}

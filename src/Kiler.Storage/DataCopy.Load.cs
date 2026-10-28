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
    /// changed files that fit the budget, read at most <see cref="DataFolder.LoadReads"/> at a time, M2.7). Then the private
    /// folder of each other user that the copy is given (<see cref="CopyOthers"/>, M2.7c) is loaded so, one after the other,
    /// while the data folder and <c>private\</c> are held, each in the budget of its own within that of all of them, with only
    /// the heads of its documents read, and without its history; nothing of it is given, and its changes outside Kiler are
    /// reported. They are only for a restore, so a load takes them all once in <see cref="OthersInterval"/>, and in between
    /// only those where a move or an edit that the SMB cache shows late waits for a listing (<see cref="OthersToLoad"/>,
    /// M2.8c); for each of the others it reports what its last pass skipped. A document that
    /// the copy had and the share no longer lists, and that did not move, also when its folder has gone, is gone: its last
    /// good bytes are kept apart. The history folder of each document that has changed is then listed, and its new slots
    /// taken (decided in M2.7a). After the pass of the user's own, at most once in 10 minutes, the old stopped creates of the
    /// private folder of the current user are deleted, and those that stay reported (<see cref="DeleteStoppedCreates"/>,
    /// M2.7e). Links, names that fail the check, files that cannot be read, documents over the budget and
    /// bytes that cannot be written into the copy are skipped and reported; the next load tries them again. A document whose
    /// times change with the same bytes, and a history folder that fails, are tried again with a back-off (M2.8b). A folder that
    /// cannot be listed, such as one that is a link, or that lists too many entries, is reported, and its documents stay as
    /// the copy has them, and so is the private folder (<see cref="ListPrivate"/>). The root of the data folder that cannot be
    /// held, listed or read is an <see cref="IOException"/> or an <see cref="UnauthorizedAccessException"/>: the share cannot
    /// be reached, and the copy is offline until a load ends (<see cref="Offline"/>, M2.8a). While it is offline, a load first
    /// holds the root of the data folder without the lock of the copy (<see cref="DataFolder.Reach"/>), and throws what that
    /// throws, so the other members do not wait for a try at a share that has gone (M2.8c). Any other exception is a fault of
    /// the code, and leaves the copy online or offline as it was. Either way, what the load took before it threw is written
    /// into the index first, so a start gives it. The index is written once,
    /// after the files, and before them only when the last good bytes of a document went apart and its file is overwritten.
    /// The old temporary files of the listings are deleted, on the clock given to
    /// <see cref="Open(DataFolder, string, TimeProvider, string?)"/>, which also times when the copy took a document and when it went,
    /// and the changes outside Kiler until they have settled.
    /// </summary>
    public LoadResult Load()
    {
        // While offline, the share is tried before the lock: the hold of the root is the request that blocks while a file server
        // has gone, up to about 60 s over SMB, and the reads of the copy and the calls of a sign-in do not wait for it (decided
        // in M2.8c, by the user).
        if (_offline)
        {
            _share.Reach();
        }

        lock (_gate)
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

                var privateFolder = _account is { } account ? ListPrivate(account, listed, missing, report) : null;
                LoadFolders(listed, missing, OwnCap, now, report, ref taken);
                DeleteStoppedCreates(now, report);
                var others = OthersToLoad(now, out var all);
                using (var held = others.Count > 0 ? _share.HoldPrivateFolders() : null)
                {
                    foreach (var other in _others)
                    {
                        if (!others.Contains(other))
                        {
                            ReportSkipped(other, report);
                            continue;
                        }

                        var mark = report.Mark;
                        var theirs = new List<DataFolder.ListedFolder>();
                        var theirsMissing = new List<FolderKey>();
                        try
                        {
                            ListPrivate(other, theirs, theirsMissing, report);
                            LoadFolders(theirs, theirsMissing, OtherCap(other), now, report, ref taken);
                        }
                        finally
                        {
                            foreach (var folder in theirs)
                            {
                                folder.Dispose();
                            }
                        }

                        KeepSkipped(other, report.SkippedSince(mark));
                    }
                }

                // Only once every one has had its pass: a load that throws in between leaves them all for the next.
                if (all)
                {
                    _othersAt = now;
                }

                taken |= TakeHistory(privateFolder, now, report);
                taken |= ReportOutside(now, report);
                ForgetTouched();
            }
            catch (Exception ex)
            {
                if (IsUnreachable(ex))
                {
                    _offline = true;
                }

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

            _offline = false;

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
    }

    /// <summary>
    /// Whether what a load threw is the share that cannot be reached (M2.8a): the root of the data folder that cannot be held,
    /// listed or read, an <see cref="IOException"/> or an <see cref="UnauthorizedAccessException"/>, as nothing else that a
    /// load meets on the share gets out of it. A <see cref="ForeignFolderException"/> is a folder that is not the one the copy
    /// knows, never a share that has gone (M2.7d); any other exception is a fault of the code.
    /// </summary>
    internal static bool IsUnreachable(Exception ex) => ex is UnauthorizedAccessException || (ex is IOException and not ForeignFolderException);

    /// <summary>
    /// Whether an entry is the last good bytes of a brief leftover, which may make room in its budget: gone, after less than
    /// <see cref="BriefStay"/> in the copy, not written by this Kiler, and listed by neither of its places (M2.7).
    /// </summary>
    private static bool IsBriefLeftover(KeyValuePair<Spot, CopyEntry> entry, HashSet<Spot> listed) =>
        entry.Value.GoneAt is { } went
        && went - entry.Value.Taken < BriefStay
        && entry.Value.Kept is { Own: false }
        && !listed.Contains(entry.Key)
        && !listed.Contains(entry.Key with { InTrash = !entry.Key.InTrash });

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
    /// The private folders of other users that a load takes (decided in M2.8c, by the user): all of them
    /// (<paramref name="all"/>) once in <see cref="OthersInterval"/> on the clock of the copy, at the first load and at the
    /// next one after <see cref="CopyOthers"/>, and at once when the clock was put back; in the loads in between, only those
    /// whose state waits for a listing within <see cref="InPlaceEdits.SettleTime"/>, as the SMB cache can show a change about
    /// 10 s late (the M2.8b note): a document that went less than that ago and is not reported (<see cref="JustGone"/>), or
    /// one in both places that the copy took less than that ago (<see cref="IsLateListing"/>), whose move a listing can still
    /// show; and one that looks edited in place and is not reported, as a write of another Kiler can look so for a moment
    /// (<see cref="InPlaceEdits"/>). Loaded a minute later, such a move would be a document gone and a new one in the other
    /// place, and such a write an edit in place, each reported for a restore. A document that a load takes new in one place
    /// gives no load more.
    /// </summary>
    private HashSet<string> OthersToLoad(DateTimeOffset now, out bool all)
    {
        all = _othersAt is not { } last || now < last || now - last >= OthersInterval;
        if (all || _others.Count == 0)
        {
            return [.. _others];
        }

        var waiting = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (spot, entry) in _index)
        {
            if (spot.Key.Account is { } account
                && (JustGone(entry, now)
                    || (IsLateListing(entry, now) && _index.ContainsKey(spot with { InTrash = !spot.InTrash }))
                    || entry is { Outside: OutsideReason.EditedInPlace, Reported: false }))
            {
                waiting.Add(account);
            }
        }

        return [.. _others.Where(waiting.Contains)];
    }

    /// <summary>Reports what the last pass of the private folder of another user skipped, for a load that does not take it (M2.8c).</summary>
    private void ReportSkipped(string account, Report report)
    {
        if (_othersSkipped.TryGetValue(account, out var skips))
        {
            foreach (var entry in skips.Kept)
            {
                report.Skip(entry);
            }

            report.Untold(skips.Untold);
        }
    }

    /// <summary>
    /// Keeps what the pass of the private folder of another user skipped, for the loads that do not take it until its next
    /// pass (M2.8c). All of them keep at most as many skipped entries as one load gives, and count the rest, as anyone can fill
    /// a folder with links.
    /// </summary>
    private void KeepSkipped(string account, (SkippedEntry[] Kept, int More) skipped)
    {
        _othersSkipped.Remove(account);
        var room = Math.Max(0, _limits.Reports - _othersSkipped.Values.Sum(skips => skips.Kept.Length));
        SkippedEntry[] kept = [.. skipped.Kept.Take(room)];
        var untold = skipped.More + skipped.Kept.Length - kept.Length;
        if (kept.Length > 0 || untold > 0)
        {
            _othersSkipped[account] = new Skips(kept, untold);
        }
    }

    /// <summary>
    /// Whether a document whose times changed waits before it is read (M2.8b, decided by the user): the copy has it good, with
    /// a stamp that a listing gave, it is listed at the same size, the copy has no file of it to read again, and its last reads
    /// gave the bytes that the copy has (<see cref="Touched"/>), up to its back-off. So a change of other bytes of the same size
    /// waits too, at most <see cref="BackOff.Longest"/>; one of another size, a write of this Kiler, and any other document are
    /// read at once.
    /// </summary>
    private bool Waits(Spot spot, CopyEntry entry, FileEntry file, DateTimeOffset now) =>
        entry is { Outside: null, Copied: > 0 }
        && entry.CreationTime != CopyEntry.Unseen
        && entry.Size == file.Size
        && !_reread.Contains(spot)
        && _touched.TryGetValue(spot, out var backOff)
        && !backOff.IsDue(now);

    /// <summary>
    /// Whether a read gave the bytes that the copy has of a document in the same place, read for a stamp that a listing gave
    /// (M2.8b): not after a write of this Kiler, nor to write the file of the copy again (<paramref name="reread"/>). Its times
    /// changed, and nothing else did but maybe its owner.
    /// </summary>
    private static bool SameBytes(Change change, bool reread) =>
        !reread
        && change.LineageSpot == change.Spot
        && change.Lineage is { Hash: { } hash } lineage
        && lineage.CreationTime != CopyEntry.Unseen
        && hash == change.Next.Hash;

    /// <summary>
    /// Keeps the back-off of a document that a load read (M2.8b): one more time when the read gave the bytes that the copy has
    /// (<see cref="SameBytes"/>), so the next change of its times waits while the copy has it good (<see cref="Waits"/>); any
    /// other read ends it.
    /// </summary>
    private void Touched(Change change, bool reread, DateTimeOffset now)
    {
        if (SameBytes(change, reread))
        {
            _touched[change.Spot] = _touched.GetValueOrDefault(change.Spot).Again(now);
        }
        else
        {
            _touched.Remove(change.Spot);
        }
    }

    /// <summary>For the tests: the documents whose back-off the copy keeps (M2.8b).</summary>
    internal int TouchesKept
    {
        get
        {
            lock (_gate)
            {
                return _touched.Count;
            }
        }
    }

    /// <summary>
    /// Forgets the back-offs of the documents that have left the index, so they take no memory; one that the copy has, but not
    /// good, waits for nothing (<see cref="Waits"/>) until a read ends it (M2.8b).
    /// </summary>
    private void ForgetTouched()
    {
        foreach (var spot in _touched.Keys.Where(spot => !_index.ContainsKey(spot)).ToList())
        {
            _touched.Remove(spot);
        }
    }

    /// <summary>
    /// What the plan counts a document at once a listed file of it is read, from what the copy has of it
    /// (<paramref name="lineage"/>): the file, and the last good bytes beside it when it looks edited in place. A file that
    /// looks as Kiler wrote it counts the larger of the two, as it turns out a new version, the last good version again, or
    /// damaged (<see cref="Cost(CopyEntry)"/>). When it turns out at a version that is not after the last good one, it needs
    /// both, and the load finds the room in what the plan left, or leaves it (<see cref="Room.Spare"/>).
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
    /// Lists the folders of the private folder of <paramref name="account"/>: of the current user (M2.7b), after those of
    /// the data folder, whose root the load holds by then; or of another user, while the load holds the data folder and
    /// <c>private\</c> (<see cref="DataFolder.HoldPrivateFolders"/>, M2.7c). A private folder that is not there, not made
    /// yet or gone, gives its folders as listed whole and
    /// empty (<paramref name="missing"/>), so what the copy had of it is gone, with its last good bytes kept apart. One that
    /// cannot be opened or listed, such as one that is a link, is reported, and its documents stay as the copy has them; so
    /// does a folder of it that cannot be listed. One that is not the folder that the copy made or first saw, or a folder in
    /// it of another owner, is reported as such (<see cref="SkipReason.ForeignFolder"/>, M2.7d), and its documents stay too.
    /// Gives the private folder, or <c>null</c> when it was not listed.
    /// </summary>
    private DataFolder? ListPrivate(string account, List<DataFolder.ListedFolder> listed, List<FolderKey> missing, Report report)
    {
        try
        {
            var own = OpenPrivate(account);
            foreach (var (folder, inTrash) in FoldersOf(account))
            {
                try
                {
                    listed.Add(own.ListFolder(folder, inTrash, _limits.Listed, _limits.Reports));
                }
                catch (Exception ex) when ((folder is not null || inTrash) && ex is IOException or UnauthorizedAccessException)
                {
                    report.Skip(new SkippedEntry(FolderName(account, folder, inTrash), null, SkipReasonOf(ex)));
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
            report.Skip(new SkippedEntry(RootName(account), null, SkipReasonOf(ex)));
            return null;
        }
    }

    /// <summary>
    /// Deletes the old stopped creates of the private folder of the current user (<see cref="DataFolder.DeleteStoppedCreates"/>,
    /// M2.7e) at most once in <see cref="DataFolder.TemporaryFileAge"/> on the clock of the copy, the first load of the copy
    /// among them, as each costs a listing of <c>private\</c> (decided by the user). The owner that they must have is that of
    /// the private folder that the copy knows, so none goes while it knows none. Those that stay are reported
    /// (<see cref="SkipReason.StoppedCreate"/>) by the look, and by every load after it until the next look (M2.8), so the
    /// skipped entries of each load are those of the share as the copy knows it. A <c>private\</c> that cannot be held or
    /// listed reports nothing here until the next look, as the pass of the private folder holds it too and reports it.
    /// </summary>
    private void DeleteStoppedCreates(DateTimeOffset now, Report report)
    {
        if (_account is not { } account)
        {
            return;
        }

        // A clock put back looks again at once, rather than wait until it is past the last look again.
        if (_stoppedCreatesAt is not { } last || now < last || now - last >= DataFolder.TemporaryFileAge)
        {
            _stoppedCreatesAt = now;
            _stoppedCreates = [];
            try
            {
                var owner = _roots.TryGetValue(account, out var known) ? known.Owner : null;
                _stoppedCreates = [.. _share.DeleteStoppedCreates(account, owner, now, _limits.Listed)
                    .Select(name => new SkippedEntry(DataFolder.Private, StorageName.Escape(name), SkipReason.StoppedCreate))];
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The next look tries again.
            }
        }

        foreach (var entry in _stoppedCreates)
        {
            report.Skip(entry);
        }
    }

    /// <summary>Why a folder of a private folder that could not be held or listed is skipped: another folder (M2.7d), or unreadable.</summary>
    private static SkipReason SkipReasonOf(Exception ex) => ex is ForeignFolderException ? SkipReason.ForeignFolder : SkipReason.Unreadable;

    /// <summary>
    /// One pass of a load over listed folders, in the budget that counts their documents (<paramref name="cap"/>): what to
    /// read, chosen for all of them at once (<see cref="Choose"/>), the reads and what they give (<see cref="TakeFolder"/>),
    /// the documents that are no longer listed (<see cref="Leave"/>), and the budget kept (<see cref="TrimToBudget"/>). Each
    /// folder is let go once it is taken. <paramref name="taken"/> says whether the index has changed, at each step, so a
    /// fault of the code in a later folder still has the load write what it took before (<see cref="Load"/>).
    /// </summary>
    private void LoadFolders(List<DataFolder.ListedFolder> listed, List<FolderKey> missing, Cap cap, DateTimeOffset now, Report report, ref bool taken)
    {
        var plan = Choose(listed, missing, cap, now);
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
    }

    /// <summary>The budget of the user's own: the data folder and the private folder of the current user, in one (decided in M2.7b, by the user).</summary>
    private Cap OwnCap => new(spot => IsOwn(spot.Key.Account), _limits.Bytes, _limits.Documents, null);

    /// <summary>
    /// The budget of the private folder of another user, within that of the private folders of all of them, those that the
    /// copy is no longer given among them (decided in M2.7c, by the user).
    /// </summary>
    private Cap OtherCap(string account) => new(
        spot => spot.Key.Account == account,
        _limits.BytesPerOther,
        _limits.DocumentsPerOther,
        new Cap(spot => !IsOwn(spot.Key.Account), _limits.OtherBytes, _limits.OtherDocuments, null));

    /// <summary>
    /// Fills the room of a budget, empty, with what the index holds now. With <paramref name="planned"/>, an entry that the
    /// plan sees leaving counts only the last good bytes that it keeps, as a gone document, a move, or nothing.
    /// </summary>
    private Room Fill(Room room, Plan? planned)
    {
        foreach (var (spot, entry) in _index)
        {
            if (room.Cap.Counts(spot))
            {
                if (planned is not null && planned.IsLeaving(spot, entry))
                {
                    if (entry.LastGood is { } lastGood)
                    {
                        room.Add(lastGood.Length, 1);
                    }
                }
                else
                {
                    room.Add(Cost(entry), 1);
                }
            }
            else if (room.Within is { } outer && outer.Cap.Counts(spot))
            {
                outer.Add(Cost(entry), 1);
            }
        }

        return room;
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
    /// A changed one whose last reads gave the bytes that the copy has keeps it until its back-off is due (<see cref="Waits"/>,
    /// M2.8b). To make them fit, the last good bytes of gone documents that were in the copy for less than <see cref="BriefStay"/>,
    /// that this Kiler did not write and that neither place lists, go, those that went first first, but only as many as it
    /// takes, and none when even all of them would not make it fit (<see cref="FolderPlan.Evicted"/>). The others go into
    /// <see cref="FolderPlan.OverBudget"/>. The budget is that of the folders of the pass (<paramref name="cap"/>, M2.7c):
    /// the user's own, or the private folder of another user within that of all of them, whose leftovers alone make room.
    /// </summary>
    private Plan Choose(IReadOnlyList<DataFolder.ListedFolder> listed, IEnumerable<FolderKey> missing, Cap cap, DateTimeOffset now)
    {
        var plan = new Plan(new Room(cap));
        plan.Whole.UnionWith(missing);
        foreach (var folder in listed.Where(folder => !folder.TooMany))
        {
            plan.Whole.Add(FolderOf(folder));
            foreach (var document in folder.Listing.Documents.Concat(folder.Listing.Unread))
            {
                plan.Listed.Add(new Spot(document.Key, folder.InTrash));
            }
        }

        // An entry that is no longer listed becomes gone with its last good bytes, or moves with them, or goes without any.
        var room = Fill(plan.Room, plan);
        room.MayEvict(_index.Where(entry => cap.Counts(entry.Key) && IsBriefLeftover(entry, plan.Listed)).OrderBy(entry => entry.Value.GoneAt));
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

                // It stayed still: its next change is read at once (M2.8b).
                if (entry is not null && !changed)
                {
                    _touched.Remove(spot);
                }

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
                    if (room.Take(more, moreDocuments, folderPlan.Evicted))
                    {
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
                else if (changed && !Waits(spot, entry, document.File, now))
                {
                    if (room.Take(Planned(document.File, entry) - Cost(entry), 0, folderPlan.Evicted))
                    {
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
                if (room.Take(Cost(document.File.Size), 1, folderPlan.Evicted))
                {
                    folderPlan.Read.Add(document);
                }
                else
                {
                    folderPlan.OverBudget.Add(document);
                }
            }
        }

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
            files = folder.Source.ReadFolder(folder, folderPlan.Read, now, headsOnly: !IsOwn(folder.Account));
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
    /// all the same. One read again with the bytes that the copy has, and with the same owner, is not given, and its next
    /// change of the times waits longer (<see cref="Touched"/>, M2.8b). Gives whether the index has changed.
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
            if (file.Head is not { } head)
            {
                continue;
            }

            var spot = new Spot(file.Listed.Key, folder.InTrash);
            var lineageSpot = plan.Moves.GetValueOrDefault(spot, spot);
            _index.TryGetValue(lineageSpot, out var lineage);
            _index.TryGetValue(spot, out var replaced);
            var damaged = head.Damage is not null;
            var seen = new Seen(
                file.Listed.File.Stamp, head.Owner, head.Damage, head.Version, damaged ? null : Hash(file.Bytes), file.Bytes.Length, file.Listed.File.Stamp.LooksEditedInPlace);
            var next = Next(spot, lineage, seen, now);

            // The cost that the plan did not foresee: a version that is not after the last good one, which keeps both.
            var extra = Cost(next) - Planned(file.Listed.File, lineage);
            if (extra > plan.Room.Spare)
            {
                report.Skip(name, [file.Listed.File], SkipReason.OverBudget);
                GivenUntaken(report, spot, file.Read);
                continue;
            }

            plan.Room.Add(Math.Max(0, extra), 0);
            changes.Add(new Change(spot, lineageSpot, lineage, replaced, next, file));
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
                GivenUntaken(report, change.Spot, change.File.Read);
                continue;
            }

            var reread = _reread.Contains(change.Spot);
            _index[change.Spot] = change.Next;
            if (change.LineageSpot != change.Spot)
            {
                _index.Remove(change.LineageSpot);
                _reread.Remove(change.LineageSpot);
                if (!change.Lineage!.Gone)
                {
                    Went(report, change.LineageSpot);
                }
            }

            Stale(FilesOf(change.LineageSpot, change.Lineage).Concat(FilesOf(change.Spot, change.Replaced)), FilesOf(change.Spot, change.Next));
            _reread.Remove(change.Spot);
            Touched(change, reread, now);

            // The bytes that the upper layers have, with the owner they have, tell them nothing new (M2.8b, decided by the user);
            // an owner that cannot be read is never the same.
            if (!SameBytes(change, reread) || change.Lineage!.Owner is null || change.Lineage.Owner != change.Next.Owner)
            {
                Given(report, change.Spot, change.File.Read);
            }

            taken = true;
        }

        return taken;
    }

    /// <summary>
    /// Gives a read of a document to the upper layers, when there is one: a document of the private folder of another user is
    /// read only for its head (<see cref="LoadedFile.Head"/>), so no document of it is ever made, nor given (M2.7c). One of
    /// the trash is not given either.
    /// </summary>
    private static void Given(Report report, Spot spot, DocumentRead? read)
    {
        if (read is not null)
        {
            report.Given(spot, read);
        }
    }

    /// <summary>
    /// Gives a read that the copy could not take, as it was over the budget or its bytes could not be written: the upper layers
    /// then have bytes that the copy does not, so the next read of the document is given and written whatever its bytes, also
    /// those that the copy has (<see cref="_reread"/>, code review of M2.8b).
    /// </summary>
    private void GivenUntaken(Report report, Spot spot, DocumentRead? read)
    {
        Given(report, spot, read);
        if (read is not null && _index.ContainsKey(spot))
        {
            _reread.Add(spot);
        }
    }

    /// <summary>
    /// Tells the upper layers that a document of the user's own has left its folder, or that the trash changed when it left
    /// the trash (M2.8); one of another user they never had (M2.7c).
    /// </summary>
    private void Went(Report report, Spot spot)
    {
        if (!IsOwn(spot.Key.Account))
        {
            return;
        }

        if (spot.InTrash)
        {
            report.LeftTheTrash();
        }
        else
        {
            report.Gone.Add(spot.Key);
        }
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

        // The private folder of another user keeps no history in the copy (M2.7c).
        if (!IsOwn(change.Spot.Key.Account) || change.Next.Outside is not null || change.Lineage?.LastGood is not { } previous || change.Next.Version <= previous.Version)
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
                Went(report, spot);
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
            Went(report, spot);
            taken = true;
        }

        return taken;
    }

    /// <summary>
    /// Takes the copy back within its budget when a load ends above it. The evictions that the plan made for a folder that
    /// could not then be read are not done, so that its leftovers stay for it (M2.7), while the folders after it may have
    /// used the room that they would have made (fifth security review of M2.7a). The brief leftovers that a plan may evict
    /// go, those that went first first, until the copy fits: the budget of the pass, and the one that it is within, with the
    /// leftovers of the pass alone (M2.7c). Gives whether the index has changed.
    /// </summary>
    private bool TrimToBudget(Plan plan)
    {
        var cap = plan.Room.Cap;
        var room = Fill(new Room(cap), planned: null);
        var taken = false;
        foreach (var (spot, entry) in _index.Where(entry => cap.Counts(entry.Key) && IsBriefLeftover(entry, plan.Listed)).OrderBy(entry => entry.Value.GoneAt).ToList())
        {
            if (!room.IsOver)
            {
                break;
            }

            _index.Remove(spot);
            Stale(FilesOf(spot, entry), []);
            room.Add(-Cost(entry), -1);
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

            // A private folder of another user that the copy is not given stays as it is, unreported too (M2.7c).
            if (entry.Outside is not { } reason || entry.Reported || report.Outside.Count >= _limits.Reports || !IsLoaded(spot.Key.Account))
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

    /// <summary>What a pass of the private folder of another user skipped: the entries kept, and how many more there were (M2.8c).</summary>
    private sealed record Skips(SkippedEntry[] Kept, int Untold);

    /// <summary>A file that a load read, with what the copy had of its document and the state it takes.</summary>
    private sealed record Change(Spot Spot, Spot LineageSpot, CopyEntry? Lineage, CopyEntry? Replaced, CopyEntry Next, LoadedFile File);
}

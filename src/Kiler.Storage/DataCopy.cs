using System.Security.Cryptography;
using System.Text.Json;

namespace Kiler.Storage;

/// <summary>
/// The local copy of a data folder (M2.7, design 5.3), in a folder of the user's own that Kiler.App gives (M2.10). Its
/// <c>data\</c> repeats the folders of the share, the trash and the history among them (M2.7a), with the bytes of the files
/// as the share has them, and its <c>index.json</c> keeps for each document the size and the times of its listing, its owner,
/// its damage, and the version and the hash of its bytes in the copy (decided in M2.7). A start gives the documents from the
/// copy at once, without the share (<see cref="Documents"/>, <see cref="Trash"/>); a load lists each folder of the share once
/// and reads only the files whose size or times have changed, at most <see cref="DataFolder.LoadReads"/> at a time
/// (<see cref="Load"/>). Only the index is kept in memory, and every document given is read anew, so it belongs to the caller
/// (decided in M2.7). The copy follows the changes that Kiler makes: new versions, the slots of their history, and the moves
/// to the trash and back, also those of this Kiler, which go through it (<see cref="Create"/>, <see cref="Replace"/>,
/// <see cref="Delete"/>, <see cref="RestoreFromTrash"/>). A document that the share no longer lists and that did not move to
/// the trash or back, one that is damaged, one edited in place, and one at a version that is not after its last good one,
/// keeps its last good bytes apart in <c>kept\</c>, and the load reports it for a restore (M2.9); the version that a new one
/// replaces goes into the history of the copy from the bytes the copy has; the upper layers see the share as it is (decided
/// at the start of M2 and in M2.7a). The bytes of a damaged file are never taken. A file that the index on the disk names is
/// deleted only once an index that no longer names it is written, and overwritten only once an index names the last good
/// bytes that it held where they went, so a stop at any point leaves them named (review of M2.7a). The copy takes at most
/// <see cref="MaxBytes"/> and <see cref="MaxDocuments"/>, the trash and the last good bytes included, also with the writes of
/// this Kiler, and at most <see cref="MaxHistoryBytes"/> and <see cref="MaxHistorySlots"/> of history, so nothing that anyone
/// puts on the share makes the memory or the disk of a Kiler grow without a bound; the last good bytes of documents that were
/// in the copy only briefly make room for the documents of the share (decided after the reviews of M2.7, by the user), and the
/// oldest slots for the new ones, those of this Kiler only for each other (decided in M2.7a, by the user). The paths in the
/// copy are built only from the checked names of <see cref="DocumentKey"/>, also those that come from the index. Not safe
/// across threads: the start, the writes and one loop of loads use it, and M2.8 keeps them apart.
/// </summary>
public sealed partial class DataCopy
{
    /// <summary>The most bytes of documents that the copy takes, by the sizes of their listings and of their bytes in it: 64 MiB.</summary>
    public const long MaxBytes = 64L * 1024 * 1024;

    /// <summary>The most documents that the copy takes, those in the trash and those that the share no longer lists included.</summary>
    public const int MaxDocuments = 20_000;

    /// <summary>The most bytes of history slots that the copy keeps, a budget of its own (decided in M2.7a, by the user): 64 MiB.</summary>
    public const long MaxHistoryBytes = 64L * 1024 * 1024;

    /// <summary>The most history slots that the copy keeps.</summary>
    public const int MaxHistorySlots = 20_000;

    /// <summary>
    /// How long a document must have been in the copy while the share listed it, from when the copy took it to when the share
    /// stopped listing it, for its last good bytes to keep their place when the budget is full (decided after the third
    /// security review of M2.7, by the user). Shorter, and they make room for the documents of the share, those that went
    /// first going first: anyone can put documents on the share and take them away again, and their bytes would block the
    /// copy for good. A document that was in the copy for a day keeps its last good bytes for a restore (M2.9), and junk kept
    /// on the share for a day blocks the new documents until the admin forgets it (M2.9). Bytes that this Kiler wrote never
    /// make room (M2.7a). The limit (fourth security review): a real document that is less than a day in a copy, a new one or
    /// one in a new copy, that another Kiler wrote, loses its last good bytes there when someone deletes it and fills the
    /// share with junk at the same time.
    /// </summary>
    internal static readonly TimeSpan BriefStay = TimeSpan.FromDays(1);

    private const string DataName = "data";
    private const string KeptName = "kept";
    private const string IndexName = "index.json";

    private readonly DataFolder _share;
    private readonly string _folder;
    private readonly TimeProvider _time;
    private readonly CopyLimits _limits;
    private readonly Dictionary<Spot, CopyEntry> _index;
    private readonly Dictionary<SlotKey, SlotEntry> _history;

    /// <summary>The documents whose file in the copy could not be read at the start, or written, which the next load reads again from the share.</summary>
    private readonly HashSet<Spot> _reread = [];

    /// <summary>The edits in place of the documents in their folders, and of those in the trash, until they have settled (M2.3).</summary>
    private readonly InPlaceEdits _edits;
    private readonly InPlaceEdits _trashEdits;

    /// <summary>The files of the copy that no entry names any more, deleted once an index that does not name them is on the disk.</summary>
    private readonly HashSet<string> _stale = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether the index on the disk is older than the one in memory, as its last write failed or it was corrected at the open.</summary>
    private bool _indexBehind;

    private DataCopy(DataFolder share, string folder, TimeProvider time, CopyLimits limits, Dictionary<Spot, CopyEntry> index, Dictionary<SlotKey, SlotEntry> history)
    {
        _share = share;
        _folder = folder;
        _time = time;
        _limits = limits;
        _index = index;
        _history = history;
        _edits = new InPlaceEdits(time);
        _trashEdits = new InPlaceEdits(time);
    }

    /// <summary>How a copy of last good bytes went: done, or lost, as they were not the bytes of their entry, or not, as they could not be read or written.</summary>
    private enum Keep
    {
        Done,
        Lost,
        Failed,
    }

    /// <summary>
    /// Opens the local copy of a data folder in <paramref name="folder"/>, which must exist; nothing is made until a load
    /// takes a document. Only the copy is read, not the share. An index that is missing, damaged, of another format, of
    /// another data folder, or with more entries than the copy takes, is no index, and the next load reads everything again;
    /// an entry that is not whole, or with a name that fails the check, is left out, and its document is read again. An index
    /// that is there but cannot be read now, as when another program holds it, is an <see cref="IOException"/> or an
    /// <see cref="UnauthorizedAccessException"/>, and nothing is deleted: taken for no index, it would cost the last good
    /// bytes of the documents that the share no longer has (third security review). The files of the copy that no entry
    /// names, and the temporary files of a write that stopped, are deleted once they are older than
    /// <see cref="DataFolder.TemporaryFileAge"/>, as the budget counts only what the index names; younger, they can be the
    /// writes of another Kiler of the same user, in another session. An entry whose bytes have gone from the copy loses them.
    /// </summary>
    public static DataCopy Open(DataFolder share, string folder, TimeProvider time) => Open(share, folder, time, CopyLimits.Default);

    /// <summary>Opens a local copy as <see cref="Open(DataFolder, string, TimeProvider)"/> does, with another budget, for the tests.</summary>
    internal static DataCopy Open(DataFolder share, string folder, TimeProvider time, CopyLimits limits)
    {
        ArgumentNullException.ThrowIfNull(share);
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(limits);
        if (share.IsPrivate)
        {
            throw new ArgumentException("A local copy is of a data folder; its private folders go into it (M2.7b).", nameof(share));
        }

        // A relative path would depend on the current folder of the process.
        if (!Path.IsPathFullyQualified(folder))
        {
            throw new ArgumentException("The local copy needs a full path.", nameof(folder));
        }

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"The folder of the local copy does not exist: {root}");
        }

        var (index, history) = ReadIndex(share, root, limits);
        var copy = new DataCopy(share, root, time, limits, index, history);
        copy.DeleteFilesNotNamed();
        return copy;
    }

    /// <summary>
    /// The documents of the share in their folders as the last load saw them, from the local copy: each with bytes read anew
    /// from its file in the copy, with the owner from the index, and a damaged one as damaged. The share is not touched. A
    /// file of the copy that is missing, damaged, or has other bytes than its entry, as a crash between the file and the
    /// index can leave it, is left out, and the next load reads it again from the share; the last good bytes kept apart stay.
    /// One that cannot be read now, as when another program holds it, is left out and read again from the share too, but
    /// keeps its entry. The documents that the share no longer lists are not given; their last good bytes stay in the copy.
    /// </summary>
    public IReadOnlyList<LoadedDocument> Documents() => Give(inTrash: false);

    /// <summary>The documents in the trash of the share as the last load saw them, from the local copy, as <see cref="Documents"/> gives the others (M2.7a, for M6).</summary>
    public IReadOnlyList<LoadedDocument> Trash() => Give(inTrash: true);

    /// <summary>
    /// The index of the copy, or an empty one when there is none that can be taken (<see cref="Open(DataFolder, string, TimeProvider)"/>).
    /// An index that is there and cannot be read now throws.
    /// </summary>
    private static (Dictionary<Spot, CopyEntry> Index, Dictionary<SlotKey, SlotEntry> History) ReadIndex(DataFolder share, string folder, CopyLimits limits)
    {
        var index = new Dictionary<Spot, CopyEntry>();
        var history = new Dictionary<SlotKey, SlotEntry>();
        CopyIndex? read;
        try
        {
            using var file = File.OpenRead(Path.Combine(folder, IndexName));
            read = JsonSerializer.Deserialize(file, StorageJsonContext.Default.CopyIndex);
        }
        catch (Exception ex) when (ex is FileNotFoundException or JsonException)
        {
            // None yet, or a damaged one.
            return (index, history);
        }

        // The budget keeps the index within its counts; twice them bounds the memory of one that is not what the copy wrote,
        // while one a little over them, as a fault could leave it, still keeps the last good bytes (fourth security review).
        if (read is not { Format: CopyIndex.CurrentFormat, Files: { } files }
            || files.Count > 2 * limits.Documents
            || (read.History?.Count ?? 0) > 2 * limits.HistorySlots
            || !string.Equals(read.Share, share.Root, StringComparison.OrdinalIgnoreCase))
        {
            return (index, history);
        }

        foreach (var entry in files)
        {
            if (entry is not null && KeyOf(share, entry.Folder, entry.Id) is { } key && IsWhole(entry))
            {
                index[new Spot(key, entry.InTrash)] = entry;
            }
        }

        foreach (var slot in read.History ?? [])
        {
            if (slot is { Version: >= 1, Hash: not null, Length: > 0 and <= DocumentFormat.MaxSize } && KeyOf(share, slot.Folder, slot.Id) is { } key)
            {
                history[new SlotKey(key, slot.InTrash, slot.Version)] = slot;
            }
        }

        return (index, history);
    }

    /// <summary>The key of an entry of the index, when its names pass the check and it is a document of the data folder.</summary>
    private static DocumentKey? KeyOf(DataFolder share, string? folder, string? id)
    {
        if (!StorageName.IsValid(id) || (folder is not null && !StorageName.IsValid(folder)))
        {
            return null;
        }

        var key = folder is null ? DocumentKey.AtRoot(id) : DocumentKey.InFolder(folder, id);
        return share.IsDocumentOf(key) ? key : null;
    }

    /// <summary>
    /// Whether an entry has sizes that the budget can count, reasons and damage that Kiler knows, and parts that go together:
    /// bytes in <c>data\</c> for a version that Kiler made, none for a gone or damaged one, and last good bytes kept apart only
    /// beside a change outside Kiler, always for a gone one, as a load keeps a gone document only for them. Its version and
    /// hash are checked against its file (<see cref="ReadCopy"/>).
    /// </summary>
    private static bool IsWhole(CopyEntry entry)
    {
        if (entry.Size < 0
            || entry.Copied is < 0 or > DocumentFormat.MaxSize
            || (entry.Copied > 0 && (entry.Hash is null || entry.Version < 1))
            || (entry.Kept is { } kept && (kept.Hash is null || kept.Version < 1 || kept.Length is <= 0 or > DocumentFormat.MaxSize))
            || entry.HistoryFrom is < 1
            || (entry.Damage is { } damage && !Enum.IsDefined(damage))
            || (entry.Outside is { } outside && !Enum.IsDefined(outside))
            || entry.Gone != (entry.Outside == OutsideReason.Gone)
            || (entry.Damage is not null) != (entry.Outside == OutsideReason.Damaged))
        {
            return false;
        }

        return entry.Outside switch
        {
            null => entry.Copied > 0 && entry.Kept is null,
            OutsideReason.Gone => entry.Copied == 0 && entry.Kept is not null,
            OutsideReason.Damaged => entry.Copied == 0,
            _ => entry.Copied > 0 || entry.Kept is not null,
        };
    }

    private static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static int? Lower(int? version, int other) => version is { } known && known < other ? known : other;

    /// <summary>
    /// When the copy took a document, for <see cref="BriefStay"/>: <paramref name="now"/> for a new one, and for one that the
    /// share lists again after it went, moved on by the time it was away, so its stay counts only the time that the share
    /// listed it; anyone could otherwise make junk old by putting it back for one load a day later (fourth security review).
    /// A change, and a move to the trash or back, keep it.
    /// </summary>
    private static DateTimeOffset TakenAt(CopyEntry? old, DateTimeOffset now) =>
        old is null ? now : old.GoneAt is { } went ? old.Taken + (now - went) : old.Taken;

    /// <summary>
    /// What a listed file costs the budget: its size, or nothing for a file over <see cref="DocumentFormat.MaxSize"/>, which
    /// is damaged without a read.
    /// </summary>
    private static long Cost(long size) => size > DocumentFormat.MaxSize ? 0 : size;

    /// <summary>
    /// What an entry costs the budget: the size of its listing while the share lists it, or the length of its bytes in the
    /// copy, when they are larger or the share no longer lists it; and the length of the last good bytes kept apart. A
    /// damaged document has no bytes in the copy, so its listing and its last good bytes count as one, the larger.
    /// </summary>
    private static long Cost(CopyEntry entry)
    {
        var listed = entry.Gone ? 0 : Cost(entry.Size);
        var kept = entry.Kept?.Length ?? 0;
        return entry.Damage is not null ? Math.Max(listed, kept) : Math.Max(listed, entry.Copied) + kept;
    }

    /// <summary>
    /// An entry without its bytes in <c>data\</c>, which have gone from the copy, and with a stamp that no listing gives, so
    /// the next load reads the share's file again; its last good bytes kept apart stay.
    /// </summary>
    private static CopyEntry WithoutCurrent(CopyEntry entry) =>
        entry with { Size = 0, CreationTime = CopyEntry.Unseen, LastWriteTime = CopyEntry.Unseen, Version = 0, Hash = null, Copied = 0, Own = false };

    /// <summary>
    /// Writes a file of the copy through a temporary file beside it and a rename, so a crash never leaves half of it under
    /// its name; <c>false</c> when it cannot, as a full disk or a program that holds the file refuses it. With
    /// <paramref name="flush"/>, the bytes are on the disk before the rename, as for the index: NTFS keeps a rename through a
    /// power cut but not always the bytes, and an index that comes back damaged loses the last good bytes that only it names
    /// (fourth security review). The folder of the copy is the user's own (M2.10), so its folders are made as they are needed.
    /// </summary>
    private static bool TryWriteFile(string path, byte[] bytes, bool flush = false)
    {
        var folder = Path.GetDirectoryName(path)!;
        var temporary = Path.Combine(folder, DataFolder.TemporaryName(Path.GetFileNameWithoutExtension(path)));
        try
        {
            Directory.CreateDirectory(folder);
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize: 0))
            {
                file.Write(bytes);
                file.Flush(flushToDisk: flush);
            }

            NativeFiles.MoveFile(temporary, path, replace: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(temporary);
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left for the next start.
        }
    }

    /// <summary>
    /// The last good bytes of a document from where the copy has them, checked against their hash, as a crash can leave other
    /// bytes beside an entry: <see cref="Keep.Lost"/> when they are missing or other bytes, <see cref="Keep.Failed"/> when the
    /// file cannot be read now.
    /// </summary>
    private static Keep ReadLastGood(string path, KeptCopy lastGood, out byte[] bytes)
    {
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 0);
            if (!DocumentFormat.TryReadBytes(file, out bytes))
            {
                return Keep.Lost;
            }
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            bytes = [];
            return Keep.Lost;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            bytes = [];
            return Keep.Failed;
        }

        return Hash(bytes) == lastGood.Hash ? Keep.Done : Keep.Lost;
    }

    /// <summary>
    /// Copies the last good bytes of a document to another file of the copy, as <see cref="ReadLastGood"/> reads them, and
    /// flushes them to the disk, as an index that names them lets the file they came from be overwritten or deleted, and a
    /// power cut keeps a rename but not always the bytes (second code review of M2.7a); nothing to do when they are there.
    /// </summary>
    private static Keep CopyLastGood(string from, string to, KeptCopy lastGood)
    {
        if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase))
        {
            return Keep.Done;
        }

        var read = ReadLastGood(from, lastGood, out var bytes);
        return read != Keep.Done ? read : TryWriteFile(to, bytes, flush: true) ? Keep.Done : Keep.Failed;
    }

    /// <summary>Deletes a folder of the copy when it is empty and its write time is older than <see cref="DataFolder.TemporaryFileAge"/>, as a write of another session may be about to fill it.</summary>
    private static void DeleteIfOldAndEmpty(string folder, DateTime now)
    {
        try
        {
            if (now - Directory.GetLastWriteTimeUtc(folder) >= DataFolder.TemporaryFileAge && !Directory.EnumerateFileSystemEntries(folder).Any())
            {
                Directory.Delete(folder);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not empty after all, or held: the next start tries again.
        }
    }

    /// <summary>The documents of one place, as <see cref="Documents"/> and <see cref="Trash"/> give them.</summary>
    private IReadOnlyList<LoadedDocument> Give(bool inTrash)
    {
        KeyValuePair<Spot, CopyEntry>[] entries = [.. _index.Where(entry => entry.Key.InTrash == inTrash && !entry.Value.Gone)];
        var reads = Workers.Run(entries, DataFolder.LoadReads, entry => ReadCopy(entry.Key, entry.Value));
        var documents = new List<LoadedDocument>(entries.Length);
        for (var i = 0; i < entries.Length; i++)
        {
            var (spot, entry) = entries[i];
            if (reads[i].Read is { } read)
            {
                documents.Add(new LoadedDocument(spot.Key, read));
            }
            else if (reads[i].Again)
            {
                _reread.Add(spot);
            }
            else if (entry.Kept is not null)
            {
                // The last good bytes stay; the next load reads the share's file again.
                _index[spot] = WithoutCurrent(entry);
                _indexBehind = true;
            }
            else
            {
                _index.Remove(spot);
            }
        }

        return documents;
    }

    /// <summary>
    /// The document of an entry, from the copy: damaged as the share gave it, or read from its file, with the owner of the
    /// entry. No read when the file is not the one the entry says: missing, damaged, or with other bytes; and to be read
    /// <see cref="CopyRead.Again"/> from the share when it cannot be read now.
    /// </summary>
    private CopyRead ReadCopy(Spot spot, CopyEntry entry)
    {
        if (entry.Damage is { } damage)
        {
            return new CopyRead(DocumentRead.Damaged(damage).WithOwner(entry.Owner), Again: false);
        }

        if (entry.Copied == 0)
        {
            return default;
        }

        byte[] bytes;
        try
        {
            using var file = new FileStream(CopyPath(spot), FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 0);
            if (!DocumentFormat.TryReadBytes(file, out bytes))
            {
                return default;
            }
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return default;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new CopyRead(null, Again: true);
        }

        if (Hash(bytes) != entry.Hash)
        {
            return default;
        }

        var read = DocumentFormat.Read(spot.Key, bytes);
        return read.IsDamaged || read.Document.Version != entry.Version ? default : new CopyRead(read.WithOwner(entry.Owner), Again: false);
    }

    /// <summary>
    /// Writes the index after the files it names, so it never names bytes that are not there; <c>false</c> when it cannot.
    /// Once it is on the disk, the files that no entry names any more are deleted (<see cref="_stale"/>); before, a stop would
    /// leave the old index, which still names them (review of M2.7a).
    /// </summary>
    private bool TryWriteIndex()
    {
        var index = new CopyIndex(CopyIndex.CurrentFormat, _share.Root, [.. _index.Values], [.. _history.Values]);
        if (!TryWriteFile(Path.Combine(_folder, IndexName), JsonSerializer.SerializeToUtf8Bytes(index, StorageJsonContext.Default.CopyIndex), flush: true))
        {
            return false;
        }

        if (_stale.Count > 0)
        {
            var named = NamedFiles();
            foreach (var path in _stale.Where(path => !named.Contains(path)))
            {
                TryDelete(path);
            }

            _stale.Clear();
        }

        return true;
    }

    /// <summary>The files of the copy that the entries of the index name.</summary>
    private HashSet<string> NamedFiles()
    {
        var named = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (spot, entry) in _index)
        {
            named.UnionWith(FilesOf(spot, entry));
        }

        named.UnionWith(_history.Keys.Select(SlotPath));
        return named;
    }

    /// <summary>The files that the entries before a change named and that they no longer name after it, to go with the next index (<see cref="_stale"/>).</summary>
    private void Stale(IEnumerable<string> before, IEnumerable<string> after)
    {
        var keep = after.ToHashSet(StringComparer.OrdinalIgnoreCase);
        _stale.UnionWith(before.Where(path => !keep.Contains(path)));
    }

    /// <summary>
    /// Deletes, in the folders that the copy writes, without going into others, the files that no entry of the index names,
    /// and there and in the folder of the copy the temporary files of a write that stopped, once they are older than
    /// <see cref="DataFolder.TemporaryFileAge"/>: younger, they can be the writes of another Kiler of the same user, in
    /// another session (M2.10). The folders are <c>data\</c>, <c>kept\</c>, their <c>trash\</c>, the document folders in each
    /// of them, and the history folders of the documents in <c>data\history\</c> and <c>data\trash\history\</c>, of which an empty one that old goes too, as
    /// ids that come and go would leave them without a bound (security review of M2.7a). When every folder could be listed,
    /// an entry whose bytes are no longer there loses them, as the budget would count them: the bytes of a version that Kiler
    /// made, and its entry, so the next load reads it again; the bytes of a change outside Kiler, and the last good bytes kept
    /// apart; a slot.
    /// </summary>
    private void DeleteFilesNotNamed()
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var named = new Dictionary<string, (Spot? Spot, bool Kept, SlotKey? Slot)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (spot, entry) in _index)
        {
            if (entry.Copied > 0)
            {
                named[CopyPath(spot)] = (spot, false, null);
            }

            if (entry.Kept is not null)
            {
                named[KeptPath(spot)] = (spot, true, null);
            }
        }

        foreach (var slot in _history.Keys)
        {
            named[SlotPath(slot)] = (null, false, slot);
        }

        var listedAll = true;
        var data = Path.Combine(_folder, DataName);
        var history = Path.Combine(data, DataFolder.History);
        var folders = new List<string> { _folder };
        foreach (var top in (string[])[data, Path.Combine(_folder, KeptName)])
        {
            foreach (var place in (string[])[top, Path.Combine(top, DataFolder.Trash)])
            {
                folders.Add(place);
                folders.AddRange(_share.DocumentFolders.Select(folder => Path.Combine(place, folder)));
            }
        }

        var historyFolders = new List<string>();
        foreach (var place in (string[])[history, Path.Combine(data, DataFolder.Trash, DataFolder.History)])
        {
            historyFolders.AddRange(_share.RootDocuments.Select(id => Path.Combine(place, id)));
            foreach (var folder in _share.DocumentFolders)
            {
                try
                {
                    historyFolders.AddRange(Directory.GetDirectories(Path.Combine(place, folder)).Where(path => StorageName.IsValid(Path.GetFileName(path))));
                }
                catch (DirectoryNotFoundException)
                {
                    // Not made yet.
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    listedAll = false;
                }
            }
        }

        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in folders.Concat(historyFolders))
        {
            string[] files;
            try
            {
                files = Directory.GetFiles(folder);
            }
            catch (DirectoryNotFoundException)
            {
                // Not made yet, so no entry has its bytes there.
                continue;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                listedAll = false;
                continue;
            }

            foreach (var file in files)
            {
                present.Add(file);
                var unnamed = DataFolder.IsTemporaryName(Path.GetFileName(file)) || (folder != _folder && !named.ContainsKey(file));
                if (unnamed && now - File.GetLastWriteTimeUtc(file) >= DataFolder.TemporaryFileAge)
                {
                    TryDelete(file);
                }
            }
        }

        foreach (var folder in historyFolders)
        {
            DeleteIfOldAndEmpty(folder, now);
        }

        if (!listedAll)
        {
            return;
        }

        foreach (var (path, (spot, kept, slot)) in named)
        {
            if (present.Contains(path))
            {
                continue;
            }

            _indexBehind = true;
            if (slot is { } missing)
            {
                _history.Remove(missing);
            }
            else if (spot is { } place && _index.TryGetValue(place, out var entry))
            {
                var without = kept ? entry with { Kept = null } : entry.Outside is null ? null : WithoutCurrent(entry);
                _index.Remove(place);
                if (without is not null && IsWhole(without))
                {
                    _index[place] = without;
                }
            }
        }
    }

    /// <summary>The files that an entry names in the copy: its bytes in <c>data\</c> and its last good bytes kept apart.</summary>
    private IEnumerable<string> FilesOf(Spot spot, CopyEntry? entry)
    {
        if (entry?.Copied > 0)
        {
            yield return CopyPath(spot);
        }

        if (entry?.Kept is not null)
        {
            yield return KeptPath(spot);
        }
    }

    /// <summary>Where the copy has the last good bytes of an entry: in <c>data\</c> for a version that Kiler made, else kept apart.</summary>
    private string LastGoodPath(Spot spot, CopyEntry entry) => entry.Outside is null ? CopyPath(spot) : KeptPath(spot);

    /// <summary>The file of a document in the copy: <c>data\records\8f3a1c.json</c>, <c>data\settings.json</c>, <c>data\trash\records\8f3a1c.json</c>.</summary>
    private string CopyPath(Spot spot) => Path.Combine(_folder, DataName, spot.InTrash ? DataFolder.Trash : "", spot.Key.Folder ?? "", spot.Key.Id + DataFolder.Extension);

    /// <summary>Where the copy keeps the last good bytes of a document apart: <c>kept\records\8f3a1c.json</c>, <c>kept\trash\records\8f3a1c.json</c>.</summary>
    private string KeptPath(Spot spot) => Path.Combine(_folder, KeptName, spot.InTrash ? DataFolder.Trash : "", spot.Key.Folder ?? "", spot.Key.Id + DataFolder.Extension);

    /// <summary>A history slot in the copy: <c>data\history\records\8f3a1c\v6.json</c>, <c>data\history\settings\v3.json</c>.</summary>
    private string SlotPath(SlotKey slot) =>
        Path.Combine(_folder, DataName, slot.InTrash ? DataFolder.Trash : "", DataFolder.History, slot.Key.Folder ?? "", slot.Key.Id, SlotName(slot.Version));

    /// <summary>Where a document is in the copy: in its folder, or in the trash.</summary>
    private readonly record struct Spot(DocumentKey Key, bool InTrash);

    /// <summary>A history slot of a document, in its folder or in the trash.</summary>
    private readonly record struct SlotKey(DocumentKey Key, bool InTrash, int Version);

    /// <summary>What a read of a file of the copy gave: the document, or none, and then whether to read it again from the share.</summary>
    private readonly record struct CopyRead(DocumentRead? Read, bool Again);
}

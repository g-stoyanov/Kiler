using System.Security.Cryptography;
using System.Text.Json;

namespace Kiler.Storage;

/// <summary>
/// The local copy of a data folder (M2.7, design 5.3), in a folder of the user's own that Kiler.App gives (M2.10). Its
/// <c>data\</c> repeats the folders of the share with the last good bytes of the documents, and its <c>index.json</c> keeps
/// for each document the size and the times of its listing, its owner, its damage, and the version and the hash of its bytes
/// in the copy (decided in M2.7). A start gives the documents from the copy at once, without the share
/// (<see cref="Documents"/>); a load lists each folder of the share once and reads only the files whose size or times have
/// changed, at most <see cref="DataFolder.LoadReads"/> at a time (<see cref="Load"/>). Only the index is kept in memory,
/// and every document given is read anew, so it belongs to the caller (decided in M2.7). The bytes of a damaged file are
/// never taken: a damaged document, or one that the share no longer lists, keeps its last good bytes in the copy (M2.7a
/// keeps them apart). The copy takes at most <see cref="MaxBytes"/> and <see cref="MaxDocuments"/>, those last good bytes
/// included, so nothing that anyone puts on the share makes the memory or the disk of a Kiler grow without a bound; the last
/// good bytes of documents that were in the copy only briefly make room for the documents of the share (decided after the
/// reviews of M2.7, by the user). The paths in the copy are built only from the checked names of <see cref="DocumentKey"/>,
/// also those that come from the index. Not safe across threads: the start and then one loop of loads use it (M2.8).
/// </summary>
public sealed class DataCopy
{
    /// <summary>The most bytes of documents that the copy takes, by the sizes of their listings and of their bytes in it: 64 MiB.</summary>
    public const long MaxBytes = 64L * 1024 * 1024;

    /// <summary>The most documents that the copy takes, those that the share no longer lists included.</summary>
    public const int MaxDocuments = 20_000;

    /// <summary>
    /// How long a document must have been in the copy while the share listed it, from when the copy took it to when the share
    /// stopped listing it, for its last good bytes to keep their place when the budget is full (decided after the third
    /// security review of M2.7, by the user). Shorter, and they make room for the documents of the share, those that went
    /// first going first: anyone can put documents on the share and take them away again, and their bytes would block the
    /// copy for good. A document that was in the copy for a day keeps its last good bytes for a restore (M2.9), and junk kept
    /// on the share for a day blocks the new documents until the admin forgets it (M2.9). The limit (fourth security review):
    /// a real document that is less than a day in a copy, a new one or one in a new copy, loses its last good bytes there when
    /// someone deletes it and fills the share with junk at the same time.
    /// </summary>
    internal static readonly TimeSpan BriefStay = TimeSpan.FromDays(1);

    private const string DataName = "data";
    private const string IndexName = "index.json";

    private readonly DataFolder _share;
    private readonly string _folder;
    private readonly TimeProvider _time;
    private readonly CopyLimits _limits;
    private readonly Dictionary<DocumentKey, CopyEntry> _index;

    /// <summary>The documents whose file in the copy could not be read at the start, which the next load reads again from the share.</summary>
    private readonly HashSet<DocumentKey> _reread = [];

    /// <summary>Whether the index on the disk is older than the one in memory, as its last write failed or it was corrected at the open.</summary>
    private bool _indexBehind;

    private DataCopy(DataFolder share, string folder, TimeProvider time, CopyLimits limits, Dictionary<DocumentKey, CopyEntry> index)
    {
        _share = share;
        _folder = folder;
        _time = time;
        _limits = limits;
        _index = index;
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
    /// writes of another Kiler of the same user, in another session. An entry whose last good bytes have gone from the copy
    /// loses them.
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

        var copy = new DataCopy(share, root, time, limits, ReadIndex(share, root, limits));
        copy.DeleteFilesNotNamed();
        return copy;
    }

    /// <summary>
    /// The documents of the share as the last load saw them, from the local copy: each good one read anew from its file in
    /// the copy, with the owner from the index, and a damaged one as damaged. The share is not touched. A file of the copy
    /// that is missing, damaged, or has other bytes than its entry, as a crash between the file and the index can leave it,
    /// is left out with its entry, and the next load reads it again from the share. One that cannot be read now, as when
    /// another program holds it, is left out and read again from the share too, but keeps its entry, so its last good bytes
    /// stay when the share no longer has it. The documents that the share no longer lists are not given; their last good
    /// bytes stay in the copy (M2.7a).
    /// </summary>
    public IReadOnlyList<LoadedDocument> Documents()
    {
        KeyValuePair<DocumentKey, CopyEntry>[] entries = [.. _index.Where(entry => !entry.Value.Gone)];
        var reads = Workers.Run(entries, DataFolder.LoadReads, entry => ReadCopy(entry.Key, entry.Value));
        var documents = new List<LoadedDocument>(entries.Length);
        for (var i = 0; i < entries.Length; i++)
        {
            if (reads[i].Read is { } read)
            {
                documents.Add(new LoadedDocument(entries[i].Key, read));
            }
            else if (reads[i].Again)
            {
                _reread.Add(entries[i].Key);
            }
            else
            {
                _index.Remove(entries[i].Key);
            }
        }

        return documents;
    }

    /// <summary>
    /// One load of the share: the root documents and then each document folder, each listed once, with the new and changed
    /// files that fit the budget read (<see cref="DataFolder.LoadFolder"/>) and taken into the copy, then the index, once.
    /// A document that the copy had and the share no longer lists, also when its folder has gone, is gone; its last good bytes
    /// stay (M2.7a). Links, names that fail the check, files that cannot be read, documents over the budget and bytes that
    /// cannot be written into the copy are skipped and reported; the next load tries them again. A document folder that
    /// cannot be listed, such as one that is a link, or that lists too many entries, is reported, and its documents stay as
    /// the copy has them. A root that cannot be held or listed is an <see cref="IOException"/> or an
    /// <see cref="UnauthorizedAccessException"/>: the share cannot be reached (M2.8). Any other exception is a fault of the
    /// code: what the load took before it is written into the index first, so a start gives it (<see cref="Documents"/>).
    /// The old temporary files of the listings are deleted, on the clock given to
    /// <see cref="Open(DataFolder, string, TimeProvider)"/>, which also times when the copy took a document and when it went.
    /// </summary>
    public LoadResult Load()
    {
        var now = _time.GetUtcNow();
        var report = new Report(_limits.Reports);
        var taken = false;
        try
        {
            foreach (var folder in (string?[])[null, .. _share.DocumentFolders])
            {
                var plan = new Plan();
                FolderLoad load;
                try
                {
                    load = _share.LoadFolder(folder, listing => Choose(folder, listing, report, plan), _limits.Listed, now);
                }
                catch (Exception ex) when (folder is not null && ex is IOException or UnauthorizedAccessException)
                {
                    report.Skip(new SkippedEntry(folder, null, SkipReason.Unreadable));
                    continue;
                }

                if (load.TooMany)
                {
                    report.Skip(new SkippedEntry(folder, null, SkipReason.OverBudget));
                    continue;
                }

                taken |= Evict(plan.Evicted);
                report.Skip(folder, load.Listing.Links, SkipReason.Link);
                report.Skip(folder, load.Listing.BadNames, SkipReason.BadName);
                report.Skip(folder, load.Listing.Unread.Select(document => document.File), SkipReason.Unreadable);
                report.Skip(folder, plan.OverBudget.Select(document => document.File), SkipReason.OverBudget);
                taken |= Take(folder, load, now, report);
            }
        }
        catch
        {
            if (taken || _indexBehind)
            {
                _indexBehind = !TryWriteIndex();
            }

            throw;
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
    /// The index of the copy, or an empty one when there is none that can be taken (<see cref="Open(DataFolder, string, TimeProvider)"/>).
    /// An index that is there and cannot be read now throws.
    /// </summary>
    private static Dictionary<DocumentKey, CopyEntry> ReadIndex(DataFolder share, string folder, CopyLimits limits)
    {
        var index = new Dictionary<DocumentKey, CopyEntry>();
        CopyIndex? read;
        try
        {
            using var file = File.OpenRead(Path.Combine(folder, IndexName));
            read = JsonSerializer.Deserialize(file, StorageJsonContext.Default.CopyIndex);
        }
        catch (Exception ex) when (ex is FileNotFoundException or JsonException)
        {
            // None yet, or a damaged one.
            return index;
        }

        if (read is not { Format: CopyIndex.CurrentFormat, Files: { } files }
            || files.Count > limits.Documents
            || !string.Equals(read.Share, share.Root, StringComparison.OrdinalIgnoreCase))
        {
            return index;
        }

        foreach (var entry in files)
        {
            if (entry is not null && KeyOf(share, entry) is { } key && IsWhole(entry))
            {
                index[key] = entry;
            }
        }

        return index;
    }

    /// <summary>The key of an entry of the index, when its names pass the check and it is a document of the data folder.</summary>
    private static DocumentKey? KeyOf(DataFolder share, CopyEntry entry)
    {
        if (!StorageName.IsValid(entry.Id) || (entry.Folder is not null && !StorageName.IsValid(entry.Folder)))
        {
            return null;
        }

        var key = entry.Folder is null ? DocumentKey.AtRoot(entry.Id) : DocumentKey.InFolder(entry.Folder, entry.Id);
        return share.IsDocumentOf(key) ? key : null;
    }

    /// <summary>
    /// Whether an entry has sizes that the budget can count, last good bytes when it is gone, as a load keeps a gone document
    /// only for them, and no damage that Kiler does not know. Its version and hash are checked against its file
    /// (<see cref="ReadCopy"/>).
    /// </summary>
    private static bool IsWhole(CopyEntry entry) =>
        entry.Size >= 0
        && entry.Copied is >= 0 and <= DocumentFormat.MaxSize
        && (!entry.Gone || entry.Copied > 0)
        && (entry.Damage is not { } damage || Enum.IsDefined(damage));

    private static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>
    /// When the copy took a document, for <see cref="BriefStay"/>: <paramref name="now"/> for a new one, and for one that the
    /// share lists again after it went, moved on by the time it was away, so its stay counts only the time that the share
    /// listed it; anyone could otherwise make junk old by putting it back for one load a day later (fourth security review).
    /// A change keeps it.
    /// </summary>
    private static DateTimeOffset TakenAt(CopyEntry? old, DateTimeOffset now) =>
        old is null ? now : old.GoneAt is { } went ? old.Taken + (now - went) : old.Taken;

    /// <summary>
    /// What a listed file costs the budget: its size, or nothing for a file over <see cref="DocumentFormat.MaxSize"/>, which
    /// is damaged without a read.
    /// </summary>
    private static long Cost(long size) => size > DocumentFormat.MaxSize ? 0 : size;

    /// <summary>
    /// What an entry costs the budget: the size of its listing while the share lists it, or the length of its last good
    /// bytes in the copy, when they are larger or the share no longer lists it.
    /// </summary>
    private static long Cost(CopyEntry entry) => Math.Max(entry.Gone ? 0 : Cost(entry.Size), entry.Copied);

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
    /// The listed documents of a folder that the load reads: the new ones, and the changed ones, as far as the budget goes.
    /// What the copy keeps takes its part first: the entries of the other folders, those of this folder that are still listed,
    /// and the last good bytes of those that are no longer; and also the documents that this load read and could not write
    /// into the copy, which are in memory. A changed document, one that is listed again after it went, and one whose file in
    /// the copy could not be read at the start, are read when they fit at their new size, else they keep what the copy has;
    /// a new one when it fits and the copy has room for one more. To make them fit, the last good bytes of gone documents that
    /// were in the copy for less than <see cref="BriefStay"/> go, those that went first first, but only as many as it takes,
    /// and none when even all of them would not make it fit (<see cref="Plan.Evicted"/>). The others go into
    /// <see cref="Plan.OverBudget"/>.
    /// </summary>
    private ListedDocument[] Choose(string? folder, FolderListing listing, Report report, Plan plan)
    {
        HashSet<DocumentKey> listed = [.. listing.Documents.Select(document => document.Key), .. listing.Unread.Select(document => document.Key)];
        var bytes = report.NotCopiedBytes;
        var count = report.NotCopiedCount;
        foreach (var (key, entry) in _index)
        {
            // An entry of this folder that is no longer listed becomes gone with its last good bytes, or goes without any.
            var leaving = key.Folder == folder && !entry.Gone && !listed.Contains(key);
            if (leaving && entry.Copied == 0)
            {
                continue;
            }

            bytes += leaving ? entry.Copied : Cost(entry);
            count++;
        }

        var evictable = new Queue<KeyValuePair<DocumentKey, CopyEntry>>(_index
            .Where(entry => entry.Value.GoneAt is { } went && went - entry.Value.Taken < BriefStay && !listed.Contains(entry.Key))
            .OrderBy(entry => entry.Value.GoneAt));
        var evictableBytes = evictable.Sum(entry => Cost(entry.Value));

        // Whether the copy has room for more bytes and documents, once as many brief leftovers as it takes have gone.
        bool Fits(long more, int documents)
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
                plan.Evicted.Add(gone.Key);
            }

            return true;
        }

        var chosen = new List<ListedDocument>();
        var added = new List<ListedDocument>();
        foreach (var document in listing.Documents)
        {
            if (!_index.TryGetValue(document.Key, out var entry))
            {
                added.Add(document);
            }
            else if (entry.Gone || entry.Stamp != document.File.Stamp || _reread.Contains(document.Key))
            {
                // Its last good bytes stay when it turns out damaged.
                var more = Math.Max(Cost(document.File.Size), entry.Copied) - Cost(entry);
                if (Fits(more, 0))
                {
                    bytes += more;
                    chosen.Add(document);
                }
                else
                {
                    plan.OverBudget.Add(document);
                }
            }
        }

        foreach (var document in added)
        {
            if (Fits(Cost(document.File.Size), 1))
            {
                bytes += Cost(document.File.Size);
                count++;
                chosen.Add(document);
            }
            else
            {
                plan.OverBudget.Add(document);
            }
        }

        return [.. chosen];
    }

    /// <summary>Takes out of the copy the gone documents that <see cref="Choose"/> made room with. Gives whether there were any.</summary>
    private bool Evict(List<DocumentKey> evicted)
    {
        foreach (var key in evicted)
        {
            _index.Remove(key);
            TryDelete(CopyPath(key));
        }

        return evicted.Count > 0;
    }

    /// <summary>
    /// Takes the load of a folder into the copy: the bytes of the good files that were read and are not in the copy already,
    /// at most <see cref="DataFolder.LoadReads"/> at a time, as an antivirus makes each new file slow (2000 one by one took
    /// about 5 s on the developer machine, at once about 1 s); then the entries of those that were written, of those whose
    /// bytes were there, and of the damaged ones, which keep the last good bytes; and the documents that are gone, which keep
    /// theirs, from <paramref name="now"/>, as long as they have any. A file that cannot be written keeps its entry as it was,
    /// so the next load reads it again, and is given all the same. A document that the copy takes for the first time is taken
    /// at <paramref name="now"/>. Gives whether the index has changed.
    /// </summary>
    private bool Take(string? folder, FolderLoad load, DateTimeOffset now, Report report)
    {
        var hashes = new Dictionary<DocumentKey, string>();
        var toWrite = new List<LoadedFile>();
        foreach (var file in load.Files)
        {
            if (file.Read is { IsDamaged: false })
            {
                var hash = Hash(file.Bytes);
                hashes[file.Listed.Key] = hash;
                // A file of the copy that could not be read at the start is written again, also with the same bytes.
                if (!_index.TryGetValue(file.Listed.Key, out var entry) || entry.Hash != hash || _reread.Contains(file.Listed.Key))
                {
                    toWrite.Add(file);
                }
            }
        }

        var written = Workers.Run(toWrite, DataFolder.LoadReads, file => TryWriteFile(CopyPath(file.Listed.Key), file.Bytes));
        var notCopied = toWrite.Where((_, i) => !written[i]).Select(file => file.Listed.Key).ToHashSet();

        var taken = false;
        foreach (var file in load.Files)
        {
            if (file.Unreadable)
            {
                report.Skip(folder, [file.Listed.File], SkipReason.Unreadable);
                continue;
            }

            // A file that has gone since the listing is gone at the next one.
            if (file.Read is not { } read)
            {
                continue;
            }

            var key = file.Listed.Key;
            var stamp = file.Listed.File.Stamp;
            _index.TryGetValue(key, out var old);
            if (notCopied.Contains(key))
            {
                report.Skip(folder, [file.Listed.File], SkipReason.NotCopied);
                report.NotCopiedBytes += Cost(stamp.Size);
                report.NotCopiedCount++;
            }
            else
            {
                _index[key] = read.IsDamaged
                    ? new CopyEntry(
                        key.Folder, key.Id, stamp.Size, stamp.CreationTime, stamp.LastWriteTime, read.Owner, read.Damage, TakenAt(old, now), null, old?.Version ?? 0, old?.Hash, old?.Copied ?? 0)
                    : new CopyEntry(
                        key.Folder, key.Id, stamp.Size, stamp.CreationTime, stamp.LastWriteTime, read.Owner, null, TakenAt(old, now), null, read.Document.Version, hashes[key], file.Bytes.Length);
                _reread.Remove(key);
                taken = true;
            }

            report.Changed.Add(new LoadedDocument(key, read));
        }

        HashSet<DocumentKey> listed = [.. load.Listing.Documents.Select(document => document.Key), .. load.Listing.Unread.Select(document => document.Key)];
        foreach (var (key, entry) in _index.Where(entry => entry.Key.Folder == folder && !entry.Value.Gone && !listed.Contains(entry.Key)).ToList())
        {
            if (entry.Copied > 0)
            {
                _index[key] = entry with { GoneAt = now };
            }
            else
            {
                _index.Remove(key);
            }

            _reread.Remove(key);
            report.Gone.Add(key);
            taken = true;
        }

        return taken;
    }

    /// <summary>
    /// The document of an entry, from the copy: damaged as the share gave it, or read from its file, with the owner of the
    /// entry. No read when the file is not the one the entry says: missing, damaged, or with other bytes; and to be read
    /// <see cref="CopyRead.Again"/> from the share when it cannot be read now.
    /// </summary>
    private CopyRead ReadCopy(DocumentKey key, CopyEntry entry)
    {
        if (entry.Damage is { } damage)
        {
            return new CopyRead(DocumentRead.Damaged(damage).WithOwner(entry.Owner), Again: false);
        }

        byte[] bytes;
        try
        {
            using var file = new FileStream(CopyPath(key), FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 0);
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

        var read = DocumentFormat.Read(key, bytes);
        return read.IsDamaged || read.Document.Version != entry.Version ? default : new CopyRead(read.WithOwner(entry.Owner), Again: false);
    }

    /// <summary>Writes the index after the files it names, so it never names bytes that are not there; <c>false</c> when it cannot.</summary>
    private bool TryWriteIndex()
    {
        var index = new CopyIndex(CopyIndex.CurrentFormat, _share.Root, [.. _index.Values]);
        return TryWriteFile(Path.Combine(_folder, IndexName), JsonSerializer.SerializeToUtf8Bytes(index, StorageJsonContext.Default.CopyIndex), flush: true);
    }

    /// <summary>
    /// Deletes, in <c>data\</c> and its document folders, without going into other folders, the files that no entry of the
    /// index names, and there and in the folder of the copy the temporary files of a write that stopped, once they are older
    /// than <see cref="DataFolder.TemporaryFileAge"/>: younger, they can be the writes of another Kiler of the same user, in
    /// another session (M2.10). When every folder could be listed, an entry whose last good bytes are no longer there loses
    /// them, as the budget would count them: a gone or good one goes, and the next load reads a good one again; a damaged one
    /// stays without them.
    /// </summary>
    private void DeleteFilesNotNamed()
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var named = _index.Where(entry => entry.Value.Copied > 0).ToDictionary(entry => CopyPath(entry.Key), entry => entry.Key, StringComparer.OrdinalIgnoreCase);
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var listedAll = true;
        var data = Path.Combine(_folder, DataName);
        foreach (var folder in (string[])[_folder, data, .. _share.DocumentFolders.Select(name => Path.Combine(data, name))])
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

        if (!listedAll)
        {
            return;
        }

        foreach (var (path, key) in named)
        {
            if (!present.Contains(path))
            {
                var entry = _index[key];
                if (entry.Damage is null || entry.Gone)
                {
                    _index.Remove(key);
                }
                else
                {
                    _index[key] = entry with { Version = 0, Hash = null, Copied = 0 };
                }

                _indexBehind = true;
            }
        }
    }

    /// <summary>The file of a document in the copy: <c>data\records\8f3a1c.json</c>, <c>data\settings.json</c>.</summary>
    private string CopyPath(DocumentKey key) => Path.Combine(_folder, DataName, key.Folder ?? "", key.Id + DataFolder.Extension);

    /// <summary>What a read of a file of the copy gave: the document, or none, and then whether to read it again from the share.</summary>
    private readonly record struct CopyRead(DocumentRead? Read, bool Again);

    /// <summary>What <see cref="Choose"/> decided for one folder beside the documents to read.</summary>
    private sealed class Plan
    {
        /// <summary>The listed documents that do not fit the budget.</summary>
        public List<ListedDocument> OverBudget { get; } = [];

        /// <summary>The gone documents whose last good bytes go to make room.</summary>
        public List<DocumentKey> Evicted { get; } = [];
    }

    /// <summary>
    /// What one load gives, with the skipped entries up to a limit and a count of the rest, and what the documents that it
    /// read and could not write into the copy cost the budget, as they are in memory.
    /// </summary>
    private sealed class Report(int limit)
    {
        private readonly List<SkippedEntry> _skipped = [];
        private int _more;

        public List<LoadedDocument> Changed { get; } = [];

        public List<DocumentKey> Gone { get; } = [];

        public long NotCopiedBytes { get; set; }

        public int NotCopiedCount { get; set; }

        public void Skip(SkippedEntry entry)
        {
            if (_skipped.Count < limit)
            {
                _skipped.Add(entry);
            }
            else
            {
                _more++;
            }
        }

        /// <summary>Skips entries of a listing, each with its escaped name; a name is escaped only when it is given.</summary>
        public void Skip(string? folder, IEnumerable<FileEntry> entries, SkipReason reason)
        {
            foreach (var entry in entries)
            {
                Skip(new SkippedEntry(folder, _skipped.Count < limit ? StorageName.Escape(entry.Name) : null, reason));
            }
        }

        public LoadResult Result() => new(Changed, Gone, _skipped, _more);
    }
}

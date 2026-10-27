namespace Kiler.Storage;

/// <summary>
/// The reads of a load of the local copy (M2.7, design 5.3): one listing of a folder, and the files of it that the copy
/// chooses, read at most <see cref="LoadReads"/> at a time. A folder is held from its listing until its reads are checked
/// through its handles (M2.3 note), so a document costs one open, not the four more requests of <see cref="Read"/>. The
/// folders of the trash are listed and read as the document folders (M2.7a); a history folder is listed only for a document
/// that has changed (<see cref="LoadHistory"/>).
/// </summary>
public sealed partial class DataFolder
{
    /// <summary>How many files a load reads at once: 2000 files took about 25 s one by one over SMB, and about 1 s 16 at a time (M1.0b).</summary>
    internal const int LoadReads = 16;

    /// <summary>The document folders of this root, in the order of its layout.</summary>
    internal IReadOnlyList<string> DocumentFolders => _layout.DocumentFolders;

    /// <summary>The documents at the root of this root, in the order of its layout.</summary>
    internal IReadOnlyList<string> RootDocuments => _layout.RootDocuments;

    /// <summary>
    /// Whether a key is of a document of this root: in one of its document folders, or one of its root documents. A key
    /// without an account is of the root it is given to; one with an account only of the private folder of that account,
    /// never of the data folder (M2.7b).
    /// </summary>
    internal bool IsDocumentOf(DocumentKey key) => (key.Account is null || key.Account == Account) && _layout.Has(key);

    /// <summary>
    /// Whether a key is of a document of its root by the layout alone: of the data folder, or of a private folder when it
    /// has an account (M2.7b). The local copy checks the entries of its index so, as a private folder may not be there.
    /// </summary>
    internal static bool IsDocument(DocumentKey key) => LayoutOf(key.Account).Has(key);

    /// <summary>The document folders of the data folder, or of a private folder when <paramref name="account"/> is given (M2.7b).</summary>
    internal static IReadOnlyList<string> DocumentFoldersOf(string? account) => LayoutOf(account).DocumentFolders;

    /// <summary>The documents at the root of the data folder, or of a private folder when <paramref name="account"/> is given (M2.7b).</summary>
    internal static IReadOnlyList<string> RootDocumentsOf(string? account) => LayoutOf(account).RootDocuments;

    private static Layout LayoutOf(string? account) => account is null ? SharedLayout : PrivateLayout;

    /// <summary>
    /// Holds and lists a folder of a load: a document folder, or the root when <paramref name="folder"/> is <c>null</c>, or,
    /// with <paramref name="inTrash"/>, its folder in the trash, <c>trash\records</c> or <c>trash</c> itself (M2.7a). The
    /// folder stays held until the result is let go, so its reads (<see cref="ReadFolder"/>) go through the same handles. A
    /// document folder, or a folder of the trash, that has gone gives an empty listing, and one that lists more than
    /// <paramref name="maxEntries"/> entries, as anyone can put any number of names there, gives none
    /// (<see cref="ListedFolder.TooMany"/>). Of the links and the names that fail the check, which can be up to 255
    /// characters long, the listing keeps at most <paramref name="maxReported"/> of each, as a report gives no more, and counts
    /// the rest (<see cref="ListedFolder.Untold"/>): a load holds the listings of all its folders at once (security review of
    /// M2.7a). A root that cannot be held or listed, or a folder that is a link, is an <see cref="IOException"/> or an
    /// <see cref="UnauthorizedAccessException"/>.
    /// </summary>
    internal ListedFolder ListFolder(string? folder, bool inTrash, int maxEntries, int maxReported)
    {
        var path = LoadPath(folder, inTrash);
        string[] below = inTrash ? (folder is null ? [Trash] : [Trash, folder]) : folder is null ? [] : [folder];
        var held = below.Length == 0 ? Hold(create: false) : HoldIfThere(below);
        if (held is null)
        {
            return new ListedFolder(this, folder, inTrash, path, null, FolderListing.Empty, tooMany: false, untold: 0);
        }

        try
        {
            var entries = Files.List(path, maxEntries + 1);
            if (entries.Count > maxEntries)
            {
                held.ThrowIfLink();
                return new ListedFolder(this, folder, inTrash, path, held, FolderListing.Empty, tooMany: true, untold: 0);
            }

            var listing = Sort(folder, entries);
            var untold = Math.Max(0, listing.Links.Count - maxReported) + Math.Max(0, listing.BadNames.Count - maxReported);
            if (untold > 0)
            {
                listing = listing with { Links = [.. listing.Links.Take(maxReported)], BadNames = [.. listing.BadNames.Take(maxReported)] };
            }

            return new ListedFolder(this, folder, inTrash, path, held, listing, tooMany: false, untold);
        }
        catch
        {
            held.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Reads the listed documents of a held folder that the copy chose, at most <see cref="LoadReads"/> at a time on threads
    /// of their own (<see cref="Workers"/>), then checks the held folders through their handles and deletes the temporary
    /// files of the listing that are older than <see cref="TemporaryFileAge"/> at <paramref name="now"/>, as a crash leaves
    /// them (M2.3); the markers of a delete or a restore are among them (M2.5). A folder that became a link before its reads
    /// were checked is an <see cref="IOException"/>, and nothing of it is given.
    /// </summary>
    internal IReadOnlyList<LoadedFile> ReadFolder(ListedFolder listed, IReadOnlyList<ListedDocument> chosen, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(listed);
        if (listed.Held is not { } held || listed.TooMany)
        {
            return [];
        }

        var files = Workers.Run(chosen, LoadReads, document => ReadListed(document, listed.InTrash ? TrashPath(document.Key) : DocumentPath(document.Key)));

        // An empty folder made a mount point in place would have given the listing, or the files, of another folder.
        held.ThrowIfLink();
        DeleteOldTemporaryFiles(listed.Path, listed.Listing.Temporary, now);
        return files;
    }

    /// <summary>
    /// Lists the history folder of a document and reads the slots that <paramref name="choose"/> gives of the listing, chosen
    /// on the calling thread before the reads, at most <see cref="LoadReads"/> at a time (M2.7a). Only the names that the
    /// layer gives its slots count (<see cref="SlotVersion"/>); a link, a folder and a slot whose times are after the year
    /// 9999 are skipped, and the old temporary files deleted, as <see cref="Versions"/> does. A history folder that is not
    /// there gives nothing; one that lists more than <paramref name="maxEntries"/> entries gives none and reads nothing. The
    /// folders are held and checked after the reads, so a link in place of one is an <see cref="IOException"/>.
    /// </summary>
    internal HistoryLoad LoadHistory(DocumentKey key, Func<IReadOnlyList<ListedSlot>, IReadOnlyList<ListedSlot>> choose, int maxEntries, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(choose);
        using var held = HoldIfThere(HistoryFolders(key));
        if (held is null)
        {
            return new HistoryLoad([], TooMany: false);
        }

        var path = HistoryFolderPath(key);
        var entries = Files.List(path, maxEntries + 1);
        if (entries.Count > maxEntries)
        {
            held.ThrowIfLink();
            return new HistoryLoad([], TooMany: true);
        }

        var slots = new List<ListedSlot>();
        var temporary = new List<FileEntry>();
        foreach (var entry in entries)
        {
            if (entry.IsLink || entry.IsFolder || entry.TimesUnread)
            {
                continue;
            }

            if (IsTemporaryName(entry.Name))
            {
                temporary.Add(entry);
            }
            else if (SlotVersion(entry.Name) is { } version)
            {
                slots.Add(new ListedSlot(version, entry));
            }
        }

        var read = Workers.Run(choose(slots), LoadReads, slot => ReadSlot(key, slot));
        held.ThrowIfLink();
        DeleteOldTemporaryFiles(path, temporary, now);
        return new HistoryLoad(read, TooMany: false);
    }

    /// <summary>The folder of a load: a document folder or the root, or its folder in the trash.</summary>
    private string LoadPath(string? folder, bool inTrash)
    {
        var path = FolderPath(folder);
        return inTrash ? Path.Combine(Root, Trash, folder ?? "") : path;
    }

    /// <summary>
    /// Reads a listed document through the held folders, opened as itself, with its owner through the same handle (M2.6),
    /// never more bytes than its listing gave, which the budget of the copy counts: a file listed over
    /// <see cref="DocumentFormat.MaxSize"/> is damaged without a read. A file that has gone since the listing gives no read,
    /// and the next listing says so. One that has grown since the listing, or that cannot be opened or read, is unreadable,
    /// for that file alone (M2.2 note), and the next load tries it again. The file is closed before the parse, as any open
    /// handle stops a rename over it (M2.5).
    /// </summary>
    private LoadedFile ReadListed(ListedDocument listed, string path)
    {
        byte[] bytes;
        string? owner;
        try
        {
            using var stream = Files.OpenRead(path);
            if (stream is null)
            {
                return new LoadedFile(listed, [], null, Unreadable: false);
            }

            owner = Files.Owner(stream);
            if (listed.File.Size > DocumentFormat.MaxSize)
            {
                return new LoadedFile(listed, [], DocumentRead.Damaged(DocumentDamage.TooLarge).WithOwner(owner), Unreadable: false);
            }

            if (!DocumentFormat.TryReadBytes(stream, listed.File.Size, out bytes))
            {
                return new LoadedFile(listed, [], null, Unreadable: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new LoadedFile(listed, [], null, Unreadable: true);
        }

        return new LoadedFile(listed, bytes, DocumentFormat.Read(listed.Key, bytes).WithOwner(owner), Unreadable: false);
    }

    /// <summary>
    /// Reads a listed history slot as <see cref="ReadListed"/> reads a document: opened as itself, never past the size of its
    /// listing. Only a slot that is a good document of its own version is given with its bytes; a damaged one, one with
    /// another version inside than its name, and one listed over the size limit are not (<see cref="LoadedSlot.Good"/>).
    /// </summary>
    private LoadedSlot ReadSlot(DocumentKey key, ListedSlot slot)
    {
        if (slot.File.Size > DocumentFormat.MaxSize)
        {
            return new LoadedSlot(slot, [], Good: false, Unreadable: false);
        }

        byte[] bytes;
        try
        {
            using var stream = Files.OpenRead(HistoryPath(key, slot.Version));
            if (stream is null)
            {
                return new LoadedSlot(slot, [], Good: false, Unreadable: false);
            }

            if (!DocumentFormat.TryReadBytes(stream, slot.File.Size, out bytes))
            {
                return new LoadedSlot(slot, [], Good: false, Unreadable: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new LoadedSlot(slot, [], Good: false, Unreadable: true);
        }

        var read = DocumentFormat.Read(key, bytes);
        return read.IsDamaged || read.Document.Version != slot.Version ? new LoadedSlot(slot, [], Good: false, Unreadable: false) : new LoadedSlot(slot, bytes, Good: true, Unreadable: false);
    }

    /// <summary>
    /// A folder of a load, held from its listing until it is let go, and its listing (M2.7, M2.7a), of the root that listed
    /// it (<see cref="Source"/>, M2.7b), which reads it. No hold for a document folder or a folder of the trash that is not
    /// there. <see cref="TooMany"/>: the folder listed more entries than the load takes, so it gives none.
    /// </summary>
    internal sealed class ListedFolder : IDisposable
    {
        internal ListedFolder(DataFolder source, string? folder, bool inTrash, string path, Holds? held, FolderListing listing, bool tooMany, int untold)
        {
            Source = source;
            Folder = folder;
            InTrash = inTrash;
            Path = path;
            Held = held;
            Listing = listing;
            TooMany = tooMany;
            Untold = untold;
        }

        /// <summary>The data folder, or the private folder, that listed it, and reads it (<see cref="ReadFolder"/>).</summary>
        public DataFolder Source { get; }

        /// <summary>The account of a private folder, or <c>null</c> for the data folder.</summary>
        public string? Account => Source.Account;

        /// <summary>The document folder, or <c>null</c> for the documents at the root.</summary>
        public string? Folder { get; }

        /// <summary>The folder in the trash, <c>trash\records</c> or <c>trash</c>, not the folder of the live documents.</summary>
        public bool InTrash { get; }

        public FolderListing Listing { get; }

        public bool TooMany { get; }

        /// <summary>How many links and names that fail the check the listing did not keep.</summary>
        public int Untold { get; }

        internal string Path { get; }

        internal Holds? Held { get; }

        public void Dispose() => Held?.Dispose();
    }
}

/// <summary>
/// A listed document that a load read (M2.7): its bytes as they are and what they give, with its owner. No read when the file
/// had gone since the listing, or is <see cref="Unreadable"/>: it had grown since the listing, or could not be opened or
/// read. No bytes for a damaged file over the size limit.
/// </summary>
internal sealed record LoadedFile(ListedDocument Listed, byte[] Bytes, DocumentRead? Read, bool Unreadable);

/// <summary>A history slot of a listing (M2.7a): its version, from its name, and its entry.</summary>
internal sealed record ListedSlot(int Version, FileEntry File);

/// <summary>
/// A history slot that a load read (M2.7a): its bytes when it is a good document of its version (<see cref="Good"/>), or
/// none: damaged, of another version inside, gone since the listing, or <see cref="Unreadable"/>.
/// </summary>
internal sealed record LoadedSlot(ListedSlot Slot, byte[] Bytes, bool Good, bool Unreadable);

/// <summary>The slots of a history folder that a load read (M2.7a). <see cref="TooMany"/>: the folder listed more entries than a load takes.</summary>
internal sealed record HistoryLoad(IReadOnlyList<LoadedSlot> Slots, bool TooMany);

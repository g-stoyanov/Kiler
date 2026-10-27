namespace Kiler.Storage;

/// <summary>
/// The reads of a load of the local copy (M2.7, design 5.3): one listing of a folder, and the files of it that the copy
/// chooses, read at most <see cref="LoadReads"/> at a time. The root and the folder are held once for the listing and all its
/// reads, and checked through their handles after the reads (M2.3 note), so a document costs one open, not the four more
/// requests of <see cref="Read"/>.
/// </summary>
public sealed partial class DataFolder
{
    /// <summary>How many files a load reads at once: 2000 files took about 25 s one by one over SMB, and about 1 s 16 at a time (M1.0b).</summary>
    internal const int LoadReads = 16;

    /// <summary>The document folders of this root, in the order of its layout.</summary>
    internal IReadOnlyList<string> DocumentFolders => _layout.DocumentFolders;

    /// <summary>Whether a key is of a document of this root: in one of its document folders, or one of its root documents.</summary>
    internal bool IsDocumentOf(DocumentKey key) =>
        key.Folder is null ? _layout.RootDocuments.Contains(key.Id) : _layout.DocumentFolders.Contains(key.Folder);

    /// <summary>
    /// Lists a document folder, or the root when <paramref name="folder"/> is <c>null</c>, and reads the listed documents
    /// that <paramref name="choose"/> gives of the listing, chosen on the calling thread before the reads, at most <see cref="LoadReads"/> at
    /// a time on threads of their own (<see cref="Workers"/>). A document folder that has gone gives an empty listing, and
    /// one that lists more than <paramref name="maxEntries"/> entries, as anyone can put any number of names there, gives
    /// none and reads nothing (<see cref="FolderLoad.TooMany"/>). A root that cannot be held or listed, a folder that is a
    /// link, or one that became a link before its reads were checked, is an <see cref="IOException"/> or an
    /// <see cref="UnauthorizedAccessException"/>, and nothing of it is given. The temporary files of the listing older than
    /// <see cref="TemporaryFileAge"/> at <paramref name="now"/> are deleted after the check, as a crash leaves them (M2.3).
    /// </summary>
    internal FolderLoad LoadFolder(string? folder, Func<FolderListing, IReadOnlyList<ListedDocument>> choose, int maxEntries, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(choose);
        var path = FolderPath(folder);
        using var held = folder is null ? Hold(create: false) : HoldIfThere(folder);
        if (held is null)
        {
            return new FolderLoad(FolderListing.Empty, [], TooMany: false);
        }

        var entries = Files.List(path, maxEntries + 1);
        if (entries.Count > maxEntries)
        {
            held.ThrowIfLink();
            return new FolderLoad(FolderListing.Empty, [], TooMany: true);
        }

        var listing = Sort(folder, entries);
        var files = Workers.Run(choose(listing), LoadReads, ReadListed);

        // An empty folder made a mount point in place would have given the listing, or the files, of another folder.
        held.ThrowIfLink();
        DeleteOldTemporaryFiles(path, listing.Temporary, now);
        return new FolderLoad(listing, files, TooMany: false);
    }

    /// <summary>
    /// Reads a listed document through the held folders, opened as itself, with its owner through the same handle (M2.6),
    /// never more bytes than its listing gave, which the budget of the copy counts: a file listed over
    /// <see cref="DocumentFormat.MaxSize"/> is damaged without a read. A file that has gone since the listing gives no read,
    /// and the next listing says so. One that has grown since the listing, or that cannot be opened or read, is unreadable,
    /// for that file alone (M2.2 note), and the next load tries it again. The file is closed before the parse, as any open
    /// handle stops a rename over it (M2.5).
    /// </summary>
    private LoadedFile ReadListed(ListedDocument listed)
    {
        byte[] bytes;
        string? owner;
        try
        {
            using var stream = Files.OpenRead(DocumentPath(listed.Key));
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
}

/// <summary>
/// The listing of a folder for a load, and the files of it that were read (M2.7). <see cref="TooMany"/>: the folder listed
/// more entries than the load takes, so it gives none.
/// </summary>
internal sealed record FolderLoad(FolderListing Listing, IReadOnlyList<LoadedFile> Files, bool TooMany);

/// <summary>
/// A listed document that a load read (M2.7): its bytes as they are and what they give, with its owner. No read when the file
/// had gone since the listing, or is <see cref="Unreadable"/>: it had grown since the listing, or could not be opened or
/// read. No bytes for a damaged file over the size limit.
/// </summary>
internal sealed record LoadedFile(ListedDocument Listed, byte[] Bytes, DocumentRead? Read, bool Unreadable);

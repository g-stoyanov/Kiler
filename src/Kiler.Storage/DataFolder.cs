using System.Globalization;

namespace Kiler.Storage;

/// <summary>
/// A root of the data layer (M2.1): the data folder of design 5.1, or a private folder <c>private\&lt;account&gt;\</c> of
/// design 7.10, which has the same structure with fewer folders and goes through the same code. <c>history\</c> and
/// <c>trash\</c> repeat the document folders: <c>history\records\8f3a1c\v6.json</c>, <c>history\settings\v3.json</c>,
/// <c>trash\records\8f3a1c.json</c>. Every name in a path passes <see cref="StorageName"/>. A folder that the layer works
/// in is checked first, and a link in its place is refused. A read or a write holds the folders of its path, each opened as
/// itself (<see cref="PinnedFolder"/>), so none of them can be swapped for a link while it works there (M2.3).
/// </summary>
public sealed partial class DataFolder
{
    public const string Templates = "templates";
    public const string Stashes = "stashes";
    public const string Users = "users";
    public const string Records = "records";
    public const string History = "history";
    public const string Trash = "trash";
    public const string Audit = "audit";
    public const string Private = "private";

    public const string Settings = "settings";
    public const string Levels = "levels";
    public const string Key = "key";

    internal const string Extension = ".json";

    /// <summary>The start of the name of a history slot: <c>v6.json</c>.</summary>
    private const char SlotPrefix = 'v';

    private static readonly Layout SharedLayout = new([Templates, Stashes, Users, Records], [Settings, Levels], [Audit, Private]);
    private static readonly Layout PrivateLayout = new([Records], [Key], []);

    private readonly Layout _layout;

    private DataFolder(string root, Layout layout, IFileSystem files, string? account, FolderIdentity? identity)
    {
        Root = root;
        _layout = layout;
        Files = files;
        Account = account;
        Identity = identity;
    }

    /// <summary>The full path of the root, without a trailing separator.</summary>
    public string Root { get; }

    /// <summary>A private folder, not the data folder.</summary>
    public bool IsPrivate => ReferenceEquals(_layout, PrivateLayout);

    internal IFileSystem Files { get; }

    /// <summary>The account of a private folder, or <c>null</c> for the data folder: the keys of its listings carry it (M2.7b).</summary>
    internal string? Account { get; }

    /// <summary>
    /// What a private folder is, the owner and the id of its root, which every hold of it is compared with (M2.7d), or
    /// <c>null</c> for the data folder.
    /// </summary>
    internal FolderIdentity? Identity { get; }

    private string Kind => IsPrivate ? "private folder" : "data folder";

    /// <summary>
    /// Makes an empty data folder with its folders, for the wizard of M4, the demo data and the tests. The folder above it
    /// must exist, so a wrong path does not become a chain of new folders. A folder that exists is taken when it holds
    /// nothing but empty folders of the layout, so a create that stopped halfway can be run again. The owners of its files
    /// come from <paramref name="owners"/> (M2.6).
    /// </summary>
    public static DataFolder Create(string path, IFileOwner owners)
    {
        ArgumentNullException.ThrowIfNull(owners);
        return Create(path, new FileSystem(owners));
    }

    /// <summary>
    /// Opens a data folder that exists. It creates nothing, so a wrong path never becomes a new data folder. The owners of its
    /// files come from <paramref name="owners"/> (M2.6).
    /// </summary>
    public static DataFolder Open(string path, IFileOwner owners)
    {
        ArgumentNullException.ThrowIfNull(owners);
        return Open(path, new FileSystem(owners));
    }

    internal static DataFolder Create(string path, IFileSystem files)
    {
        var root = FullPath(path);

        // The folder above is where the user put the data folder, so it may be a link; the data folder itself may not.
        var above = Path.GetDirectoryName(root);
        if (above is null || files.Attributes(above) is not { } attributes || (attributes & FileAttributes.Directory) == 0)
        {
            throw new DirectoryNotFoundException($"The folder above it does not exist: {root}");
        }

        if (!FolderExists(files, root))
        {
            files.CreateFolder(root);
        }
        else if (!HoldsOnlyEmptyFolders(root, SharedLayout, files))
        {
            throw new IOException($"The folder holds more than the empty folders of a new data folder: {root}");
        }

        MakeFolders(root, SharedLayout, files);
        return new DataFolder(root, SharedLayout, files, account: null, identity: null);
    }

    internal static DataFolder Open(string path, IFileSystem files)
    {
        var root = FullPath(path);
        return FolderExists(files, root) ? new DataFolder(root, SharedLayout, files, account: null, identity: null) : throw new DirectoryNotFoundException($"The folder does not exist: {root}");
    }

    /// <summary>
    /// Makes the private folder of an account (design 7.10). A folder that exists, even an empty one, is refused: anyone
    /// can make it on the share before the first private record, and would then be its owner (design 7.8). It is made
    /// under a temporary name and renamed without replacing, so a create that stops halfway leaves only the temporary
    /// folder. The temporary folder and its folders are made new, never taken when someone made them first, and the temporary
    /// folder is held from its create until just before the rename, which the hold would refuse, so someone could rename it
    /// away in between and put another folder under its name: the new private folder must be the folder that was made, by
    /// its id, and have the owner it had then, both read through the handles of the folders, else it is refused after the
    /// rename, and the folder at the name is left as it is (decided in M2.6). An owner that cannot be read, and an id that
    /// tells nothing (index 0), are refused before the rename. The data folder and <c>private\</c> are held for the whole create, and <c>private\</c>
    /// is made too when it is missing. The owner and the id are what the folder that it gives compares every later hold with,
    /// and the local copy keeps them (<see cref="Identity"/>, M2.7d). How an account maps to a folder name is decided in M4.
    /// </summary>
    public DataFolder CreatePrivate(string account) => CreatePrivate(account, renamed: null);

    /// <summary>
    /// Makes the private folder of an account as <see cref="CreatePrivate(string)"/> does, and gives what the folder it made
    /// is to <paramref name="renamed"/> once it has been renamed to the name, before the check after the rename: a folder
    /// that the check then refuses is not the one made, and the local copy keeps what was made, so it never takes that folder
    /// as first seen (review of M2.7d).
    /// </summary>
    internal DataFolder CreatePrivate(string account, Action<FolderIdentity>? renamed)
    {
        var privateFolders = PrivateFolders(account);
        if (!FolderExists(Files, privateFolders))
        {
            Files.CreateFolder(privateFolders);
        }

        using var folders = Hold(create: false, Private);
        var root = Path.Combine(privateFolders, account);
        if (Files.Attributes(root) is not null)
        {
            throw new IOException($"The private folder exists already: {root}");
        }

        // A new folder, with its id and its owner read through the handle of the call that made it, so no other folder can be
        // taken for it before they are read.
        var temporary = Path.Combine(privateFolders, TemporaryName(account));
        FileId made;
        string owner;
        using (var held = Files.HoldNewFolder(temporary, readOwner: true) ?? throw new IOException($"The name of the temporary folder is taken: {temporary}"))
        {
            made = Files.Id(held);
            if (made.Index == 0)
            {
                throw new IOException($"The file system gives the folder no id, which Kiler checks: {temporary}");
            }

            owner = Files.Owner(held) ?? throw new IOException($"Kiler cannot read the owner of the folder it made, which it checks: {temporary}");
            MakeNewFolders(temporary, PrivateLayout, Files);
        }

        folders.ThrowIfLink();
        Files.MoveFolder(temporary, root);
        var identity = new FolderIdentity(owner, made);
        renamed?.Invoke(identity);
        using (var atTheName = Files.HoldFolder(root, readOwner: true))
        {
            folders.ThrowIfLink();
            if (Files.Id(atTheName) != made || Files.Owner(atTheName) != owner)
            {
                throw new IOException($"The private folder is not the one Kiler made: another folder, or another owner: {root}");
            }
        }

        return new DataFolder(root, PrivateLayout, Files, account, identity);
    }

    /// <summary>
    /// Opens the private folder of an account that exists, as it is now: the owner and the id of its root, read through its
    /// handle, are what every later hold of it is compared with (<see cref="Identity"/>, M2.7d), so another folder put at the
    /// name, or an owner changed, after the open is a <see cref="ForeignFolderException"/>. It creates nothing. A folder whose
    /// owner cannot be read, or whose id tells nothing (index 0), is an <see cref="IOException"/>: it cannot be checked.
    /// </summary>
    public DataFolder OpenPrivate(string account) => OpenPrivate(account, expected: null);

    /// <summary>
    /// Opens the private folder of an account as <see cref="OpenPrivate(string)"/> does, as what the local copy knows of it
    /// (<paramref name="expected"/>, M2.7d), which its first hold compares, as each hold does: another owner or another id is
    /// a <see cref="ForeignFolderException"/> there, and the open itself holds nothing. Without it, the folder is taken as it
    /// is now, as first seen.
    /// </summary>
    internal DataFolder OpenPrivate(string account, FolderIdentity? expected)
    {
        var privateFolders = PrivateFolders(account);
        if (!FolderExists(Files, privateFolders))
        {
            throw new DirectoryNotFoundException($"The folder of the private folders does not exist: {privateFolders}");
        }

        var root = Path.Combine(privateFolders, account);
        if (!FolderExists(Files, root))
        {
            throw new DirectoryNotFoundException($"The folder does not exist: {root}");
        }

        if (expected is null)
        {
            using var holds = new Holds();
            expected = HoldPrivateRoot(Files, holds, root);
        }

        return new DataFolder(root, PrivateLayout, Files, account, expected);
    }

    public string DocumentPath(DocumentKey key) => Path.Combine(Root, Known(key).Folder ?? "", key.Id + Extension);

    /// <summary>The history slot of one version of a document (M2.4).</summary>
    public string HistoryPath(DocumentKey key, int version)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(version, 1);
        return Path.Combine(HistoryFolderPath(key), SlotPrefix + version.ToString(CultureInfo.InvariantCulture) + Extension);
    }

    public string TrashPath(DocumentKey key) => Path.Combine(Root, Trash, Known(key).Folder ?? "", key.Id + Extension);

    /// <summary>
    /// Reads a document (M2.2): <c>null</c> when its file is not there, else the document or why the file is damaged, with the
    /// owner of the file, read through the same handle (M2.6). Its folders are held and the file is opened as itself, so a
    /// link in place of any of them is an <see cref="IOException"/> (M2.3). A file that cannot be opened throws, as
    /// <see cref="IFileSystem.OpenRead"/> says.
    /// </summary>
    internal DocumentRead? Read(DocumentKey key)
    {
        using var folders = HoldDocumentFolders(key);
        using var stream = Files.OpenRead(DocumentPath(key));

        // An empty folder made a mount point in place would have given the file of that name elsewhere.
        folders.ThrowIfLink();
        return stream is null ? null : DocumentFormat.Read(key, stream).WithOwner(Files.Owner(stream));
    }

    /// <summary>
    /// The documents of a document folder, or of the root when <paramref name="folder"/> is <c>null</c>, from one listing:
    /// the files and their attributes come with it. Links, and <c>.json</c> files whose name fails the check, are skipped
    /// and given apart to be reported. Other files and folders are not documents and are left out. The folder that is
    /// listed is checked first, so a link in its place is not listed: one request for the folder, not one per file. The
    /// temporary files of the layer's writes are given apart, so the old ones can be deleted. A folder of a private folder is
    /// listed while it is held, as a load holds it (<see cref="ListFolder"/>), so what the private folder is gets checked
    /// (M2.7d, security review): another is a <see cref="ForeignFolderException"/>.
    /// </summary>
    internal FolderListing List(string? folder)
    {
        var path = FolderPath(folder);
        if (Account is not null)
        {
            using var held = folder is null ? Hold(create: false) : Hold(create: false, folder);
            var listing = Sort(folder, Files.List(path));
            held.ThrowIfLink();
            return listing;
        }

        if (!FolderExists(Files, path))
        {
            throw new DirectoryNotFoundException($"The folder does not exist: {path}");
        }

        return Sort(folder, Files.List(path));
    }

    /// <summary>
    /// The entries of a listing of a document folder, or of the root, sorted as <see cref="List"/> gives them. A document
    /// whose times could not be read (<see cref="FileEntry.TimesUnread"/>) is given apart: it is there, but has no stamp to
    /// compare (M2.7).
    /// </summary>
    private FolderListing Sort(string? folder, IReadOnlyList<FileEntry> entries)
    {
        var documents = new List<ListedDocument>();
        var unread = new List<ListedDocument>();
        var links = new List<FileEntry>();
        var badNames = new List<FileEntry>();
        var temporary = new List<FileEntry>();
        foreach (var entry in entries)
        {
            if (entry.IsLink)
            {
                links.Add(entry);
                continue;
            }

            if (entry.IsFolder || !entry.Name.EndsWith(Extension, StringComparison.OrdinalIgnoreCase))
            {
                if (!entry.IsFolder && IsTemporaryName(entry.Name))
                {
                    temporary.Add(entry);
                }

                continue;
            }

            var id = entry.Name[..^Extension.Length];
            if (!entry.Name.EndsWith(Extension, StringComparison.Ordinal) || !StorageName.IsValid(id))
            {
                badNames.Add(entry);
            }
            else if (folder is not null)
            {
                (entry.TimesUnread ? unread : documents).Add(new ListedDocument(OfThisRoot(DocumentKey.InFolder(folder, id)), entry));
            }
            else if (_layout.RootDocuments.Contains(id))
            {
                (entry.TimesUnread ? unread : documents).Add(new ListedDocument(OfThisRoot(DocumentKey.AtRoot(id)), entry));
            }
        }

        return new FolderListing(documents, links, badNames, temporary, unread);
    }

    private static string FullPath(string path)
    {
        // A relative path would depend on the current folder of the process.
        if (!Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException("A data folder needs a full path.", nameof(path));
        }

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    /// <summary>Whether a folder is there. A link or a file in its place is an <see cref="IOException"/>.</summary>
    private static bool FolderExists(IFileSystem files, string path)
    {
        if (files.Attributes(path) is not { } attributes)
        {
            return false;
        }

        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException($"The folder is a link, which Kiler does not follow: {path}");
        }

        if ((attributes & FileAttributes.Directory) == 0)
        {
            throw new IOException($"A file is where a folder should be: {path}");
        }

        return true;
    }

    /// <summary>Whether a root holds nothing but empty folders of its layout, as a create that stopped halfway leaves it.</summary>
    private static bool HoldsOnlyEmptyFolders(string root, Layout layout, IFileSystem files) =>
        files.List(root).All(entry =>
            entry.IsFolder && !entry.IsLink && layout.Folders.Contains(entry.Name) && files.List(Path.Combine(root, entry.Name)).Count == 0);

    /// <summary>Makes the folders of the layout that are missing; a link put in place of one is refused by the create.</summary>
    private static void MakeFolders(string root, Layout layout, IFileSystem files)
    {
        foreach (var folder in layout.Folders)
        {
            files.CreateFolder(Path.Combine(root, folder));
        }
    }

    /// <summary>
    /// Makes the folders of the layout in a new root, each a new folder: one that someone made there first, while the root was
    /// held, would be theirs, and is refused (M2.6).
    /// </summary>
    private static void MakeNewFolders(string root, Layout layout, IFileSystem files)
    {
        foreach (var folder in layout.Folders)
        {
            var path = Path.Combine(root, folder);
            (files.HoldNewFolder(path) ?? throw new IOException($"Someone else made the folder first: {path}")).Dispose();
        }
    }

    private string PrivateFolders(string account)
    {
        if (IsPrivate)
        {
            throw new InvalidOperationException("Private folders are in the data folder, not in a private folder.");
        }

        StorageName.Check(account);

        // The data folder can have gone, or been swapped for a link, since it was opened.
        if (!FolderExists(Files, Root))
        {
            throw new DirectoryNotFoundException($"The data folder does not exist: {Root}");
        }

        return Path.Combine(Root, Private);
    }

    /// <summary>The path of a document folder of this root, or of the root itself when <paramref name="folder"/> is <c>null</c>.</summary>
    private string FolderPath(string? folder)
    {
        if (folder is not null && !_layout.DocumentFolders.Contains(folder))
        {
            throw new ArgumentException($"Not a document folder of this {Kind}.", nameof(folder));
        }

        return folder is null ? Root : Path.Combine(Root, folder);
    }

    /// <summary>
    /// The version in a name that <see cref="HistoryPath"/> makes, <c>v&lt;N&gt;.json</c> with N from 1 and no leading zero, or
    /// <c>null</c> for any other name: a name from the shared folder is a slot only in the one form the layer writes.
    /// </summary>
    private static int? SlotVersion(string name)
    {
        if (name.Length <= 1 + Extension.Length || name[0] != SlotPrefix || !name.EndsWith(Extension, StringComparison.Ordinal))
        {
            return null;
        }

        var digits = name.AsSpan(1, name.Length - 1 - Extension.Length);
        return digits[0] != '0' && !digits.ContainsAnyExceptInRange('0', '9') && int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var version)
            ? version
            : null;
    }

    /// <summary>The folder of the history slots of a document: <c>history\records\8f3a1c</c>, <c>history\settings</c>.</summary>
    private string HistoryFolderPath(DocumentKey key) => Path.Combine(Root, History, Known(key).Folder ?? "", key.Id);

    /// <summary>A key that a listing of this root makes, with the account of a private folder (M2.7b).</summary>
    private DocumentKey OfThisRoot(DocumentKey key) => Account is null ? key : key.InPrivate(Account);

    /// <summary>The key, when its folder or its root document belongs to this root.</summary>
    private DocumentKey Known(DocumentKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return IsDocumentOf(key) ? key : throw new ArgumentException($"Not a document of this {Kind}: {key}", nameof(key));
    }

    /// <summary>The document folders, the documents at the root, and the other folders that a new root gets.</summary>
    private sealed record Layout(string[] DocumentFolders, string[] RootDocuments, string[] OtherFolders)
    {
        /// <summary>
        /// The folders of a new root. <c>history\</c> and <c>trash\</c> are made empty; their subfolders, such as
        /// <c>history\records\</c>, come with the first write or delete that needs them (M2.4, M2.5).
        /// </summary>
        public string[] Folders { get; } = [.. DocumentFolders, History, Trash, .. OtherFolders];

        /// <summary>Whether a key is in one of the document folders, or is one of the root documents.</summary>
        public bool Has(DocumentKey key) => key.Folder is null ? RootDocuments.Contains(key.Id) : DocumentFolders.Contains(key.Folder);
    }
}

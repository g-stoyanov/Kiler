using System.Globalization;
using System.Security.Cryptography;

namespace Kiler.Storage;

/// <summary>
/// A root of the data layer (M2.1): the data folder of design 5.1, or a private folder <c>private\&lt;account&gt;\</c> of
/// design 7.10, which has the same structure with fewer folders and goes through the same code. <c>history\</c> and
/// <c>trash\</c> repeat the document folders: <c>history\records\8f3a1c\v6.json</c>, <c>history\settings\v3.json</c>,
/// <c>trash\records\8f3a1c.json</c>. Every name in a path passes <see cref="StorageName"/>. A folder that the layer works
/// in is checked first, and a link in its place is refused; holding the folder so that it cannot be swapped between the
/// check and the work comes with the writes (M2.3).
/// </summary>
public sealed class DataFolder
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

    private const string Extension = ".json";

    private static readonly Layout SharedLayout = new([Templates, Stashes, Users, Records], [Settings, Levels], [Audit, Private]);
    private static readonly Layout PrivateLayout = new([Records], [Key], []);

    private readonly Layout _layout;

    private DataFolder(string root, Layout layout, IFileSystem files)
    {
        Root = root;
        _layout = layout;
        Files = files;
    }

    /// <summary>The full path of the root, without a trailing separator.</summary>
    public string Root { get; }

    /// <summary>A private folder, not the data folder.</summary>
    public bool IsPrivate => ReferenceEquals(_layout, PrivateLayout);

    internal IFileSystem Files { get; }

    private string Kind => IsPrivate ? "private folder" : "data folder";

    /// <summary>
    /// Makes an empty data folder with its folders, for the wizard of M4, the demo data and the tests. The folder above it
    /// must exist, so a wrong path does not become a chain of new folders. A folder that exists is taken when it holds
    /// nothing but empty folders of the layout, so a create that stopped halfway can be run again.
    /// </summary>
    public static DataFolder Create(string path) => Create(path, FileSystem.Instance);

    /// <summary>Opens a data folder that exists. It creates nothing, so a wrong path never becomes a new data folder.</summary>
    public static DataFolder Open(string path) => Open(path, FileSystem.Instance);

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
        return new DataFolder(root, SharedLayout, files);
    }

    internal static DataFolder Open(string path, IFileSystem files) => OpenRoot(FullPath(path), SharedLayout, files);

    /// <summary>
    /// Makes the private folder of an account (design 7.10). A folder that exists, even an empty one, is refused: anyone
    /// can make it on the share before the first private record, and would then be its owner (design 7.8). It is made
    /// under a temporary name and renamed without replacing, so a create that stops halfway leaves only the temporary
    /// folder. <c>private\</c> is made too when it is missing. How an account maps to a folder name is decided in M4.
    /// </summary>
    public DataFolder CreatePrivate(string account)
    {
        var privateFolders = PrivateFolders(account);
        if (!FolderExists(Files, privateFolders))
        {
            Files.CreateFolder(privateFolders);
        }

        var root = Path.Combine(privateFolders, account);
        if (Files.Attributes(root) is not null)
        {
            throw new IOException($"The private folder exists already: {root}");
        }

        var temporary = Path.Combine(privateFolders, $"{account}.{RandomNumberGenerator.GetHexString(8, lowercase: true)}{StorageName.TemporaryEnding}");
        Files.CreateFolder(temporary);
        MakeFolders(temporary, PrivateLayout, Files);
        Files.MoveFolder(temporary, root);
        return new DataFolder(root, PrivateLayout, Files);
    }

    /// <summary>Opens the private folder of an account that exists. It creates nothing.</summary>
    public DataFolder OpenPrivate(string account)
    {
        var privateFolders = PrivateFolders(account);
        if (!FolderExists(Files, privateFolders))
        {
            throw new DirectoryNotFoundException($"The folder of the private folders does not exist: {privateFolders}");
        }

        return OpenRoot(Path.Combine(privateFolders, account), PrivateLayout, Files);
    }

    public string DocumentPath(DocumentKey key) => Path.Combine(Root, Known(key).Folder ?? "", key.Id + Extension);

    /// <summary>The history slot of one version of a document (M2.4).</summary>
    public string HistoryPath(DocumentKey key, int version)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(version, 1);
        var slot = "v" + version.ToString(CultureInfo.InvariantCulture) + Extension;
        return Path.Combine(Root, History, Known(key).Folder ?? "", key.Id, slot);
    }

    public string TrashPath(DocumentKey key) => Path.Combine(Root, Trash, Known(key).Folder ?? "", key.Id + Extension);

    /// <summary>
    /// Reads a document (M2.2): <c>null</c> when its file is not there, else the document or why the file is damaged. A file
    /// that cannot be opened throws, as <see cref="IFileSystem.OpenRead"/> says. Neither the folder nor the file is checked
    /// for a link yet: how a read refuses one through the handle comes with the writes (M2.3).
    /// </summary>
    internal DocumentRead? Read(DocumentKey key)
    {
        using var stream = Files.OpenRead(DocumentPath(key));
        return stream is null ? null : DocumentFormat.Read(key, stream);
    }

    /// <summary>
    /// The documents of a document folder, or of the root when <paramref name="folder"/> is <c>null</c>, from one listing:
    /// the files and their attributes come with it. Links, and <c>.json</c> files whose name fails the check, are skipped
    /// and given apart to be reported. Other files and folders are not documents and are left out. The folder that is
    /// listed is checked first, so a link in its place is not listed: one request for the folder, not one per file.
    /// </summary>
    internal FolderListing List(string? folder)
    {
        if (folder is not null && !_layout.DocumentFolders.Contains(folder))
        {
            throw new ArgumentException($"Not a document folder of this {Kind}.", nameof(folder));
        }

        var path = folder is null ? Root : Path.Combine(Root, folder);
        if (!FolderExists(Files, path))
        {
            throw new DirectoryNotFoundException($"The folder does not exist: {path}");
        }

        var documents = new List<ListedDocument>();
        var links = new List<FileEntry>();
        var badNames = new List<FileEntry>();
        foreach (var entry in Files.List(path))
        {
            if (entry.IsLink)
            {
                links.Add(entry);
                continue;
            }

            if (entry.IsFolder || !entry.Name.EndsWith(Extension, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var id = entry.Name[..^Extension.Length];
            if (!entry.Name.EndsWith(Extension, StringComparison.Ordinal) || !StorageName.IsValid(id))
            {
                badNames.Add(entry);
            }
            else if (folder is not null)
            {
                documents.Add(new ListedDocument(DocumentKey.InFolder(folder, id), entry));
            }
            else if (_layout.RootDocuments.Contains(id))
            {
                documents.Add(new ListedDocument(DocumentKey.AtRoot(id), entry));
            }
        }

        return new FolderListing(documents, links, badNames);
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

    private static DataFolder OpenRoot(string root, Layout layout, IFileSystem files) =>
        FolderExists(files, root) ? new DataFolder(root, layout, files) : throw new DirectoryNotFoundException($"The folder does not exist: {root}");

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

    /// <summary>The key, when its folder or its root document belongs to this root.</summary>
    private DocumentKey Known(DocumentKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var known = key.Folder is null ? _layout.RootDocuments.Contains(key.Id) : _layout.DocumentFolders.Contains(key.Folder);
        return known ? key : throw new ArgumentException($"Not a document of this {Kind}: {key}", nameof(key));
    }

    /// <summary>The document folders, the documents at the root, and the other folders that a new root gets.</summary>
    private sealed record Layout(string[] DocumentFolders, string[] RootDocuments, string[] OtherFolders)
    {
        /// <summary>
        /// The folders of a new root. <c>history\</c> and <c>trash\</c> are made empty; their subfolders, such as
        /// <c>history\records\</c>, come with the first write or delete that needs them (M2.4, M2.5).
        /// </summary>
        public string[] Folders { get; } = [.. DocumentFolders, History, Trash, .. OtherFolders];
    }
}

namespace Kiler.App;

/// <summary>The files that the local copy looks at and makes. Tests replace them.</summary>
internal interface ICopyFiles
{
    /// <summary>True for a UNC path and for a mapped network drive.</summary>
    bool IsOnNetwork(string path);

    bool Exists(string path);
}

/// <summary>
/// The files that the local copy reads and makes when it looks for a new version (M3.6), and a start from the shared folder
/// when it makes the first copy (M3.8). Tests replace them.
/// </summary>
internal interface IUpdateFiles
{
    /// <summary>Opens a file of the shared folder to read. While it is open, no one can write it. It throws as a <see cref="FileStream"/> does.</summary>
    Stream OpenShared(string path);

    /// <summary>
    /// Makes the file of a copy of the exe under a name that is not there yet, to write and read: the <c>.new</c> of a new
    /// version, the <c>.tmp</c> of a first copy, whose folder it makes when it is not there. While it is open, no one else can
    /// write it, but it can be renamed (M3.7). It throws as a <see cref="FileStream"/> does, also when a file of that name is
    /// there: a file that the check did not make is never taken over.
    /// </summary>
    Stream CreateUpdate(string path);

    /// <summary>
    /// Writes a stream of <see cref="CreateUpdate"/> through to the disk, so a crash or a power cut after its rename into
    /// <c>Kiler.exe</c> never leaves a file of the right length without its bytes (security review of M3.7). It throws as a
    /// <see cref="FileStream"/> does.
    /// </summary>
    void FlushToDisk(Stream update);

    /// <summary>
    /// Renames a file in the folder of the local copy, never over a file that is there (M3.7). It throws as
    /// <see cref="File.Move(string, string, bool)"/> does: a taken name is an <see cref="IOException"/> whose HResult is
    /// ERROR_ALREADY_EXISTS or ERROR_FILE_EXISTS, a missing file a <see cref="FileNotFoundException"/>.
    /// </summary>
    void Rename(string from, string to);

    /// <summary>Deletes a file; false when it cannot. A file that is not there counts as deleted.</summary>
    bool TryDelete(string path);
}

/// <summary>A file of a folder as <see cref="ILeftoverFiles.List"/> found it, with the times in UTC.</summary>
internal sealed record FolderFile(string Path, FileAttributes Attributes, DateTimeOffset Created, DateTimeOffset Written)
{
    /// <summary>A symbolic link or another reparse point: a delete through a handle would follow it (M3.6, security review).</summary>
    public bool IsLink => (Attributes & FileAttributes.ReparsePoint) != 0;
}

/// <summary>The files that a start deletes or moves back in the folder of the local copy (M3.7a). Tests replace them.</summary>
internal interface ILeftoverFiles
{
    /// <summary>The files in <paramref name="folder"/>, not in its subfolders. A folder that is not there throws <see cref="DirectoryNotFoundException"/>.</summary>
    IReadOnlyList<FolderFile> List(string folder);

    bool Exists(string path);

    /// <inheritdoc cref="IUpdateFiles.Rename"/>
    void Rename(string from, string to);

    /// <summary>
    /// Deletes a file by its name, a read-only one too, and also one that others hold while they share the delete. It throws
    /// as <see cref="File.Delete"/> does: Windows refuses an exe that a process runs, with an
    /// <see cref="UnauthorizedAccessException"/>. A link throws an <see cref="IOException"/> and stays.
    /// </summary>
    void Delete(string path);

    /// <summary>
    /// Deletes a file only when no one holds it, a read-only one too: one that another handle holds throws a sharing
    /// violation, an <see cref="IOException"/>, and stays; one that is not there throws <see cref="FileNotFoundException"/>.
    /// Its open would follow a symbolic link and delete the target, so a caller skips a reparse point, and a link that a
    /// file has become since its listing throws an <see cref="IOException"/> and stays.
    /// </summary>
    void DeleteUnheld(string path);
}

internal sealed class CopyFiles : ICopyFiles, IUpdateFiles, ILeftoverFiles
{
    /// <summary>The ending of the temporary file of a first copy: <c>Kiler.exe.&lt;random&gt;.tmp</c>.</summary>
    public const string TmpEnding = ".tmp";

    private const int BufferSize = 81920;

    public bool IsOnNetwork(string path) => IsNetworkPath(path);

    public bool Exists(string path) => File.Exists(path);

    /// <summary>
    /// Others can still read it, and rename or delete it, so a release is not held up; whatever they do, the copy reads one
    /// file, and its hash is taken of the local file anyway.
    /// </summary>
    public Stream OpenShared(string path) =>
        new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, BufferSize, FileOptions.SequentialScan);

    /// <summary>
    /// The hash of the file is read through this stream, and no one can write the file until it is disposed (M3.4, security
    /// review). Its name can still be deleted or renamed, as the swap of M3.7 and the first copy (M3.8) rename it by path
    /// while it is held; the name is new for each check, so it is that check's file (security review of M3.6). The file is
    /// new, so it has none of the attributes of the exe that it copies, such as read-only (security review of M3.7a).
    /// </summary>
    public Stream CreateUpdate(string path)
    {
        // The path is always a file in a folder: the folder of the local copy, or a test folder.
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read | FileShare.Delete, BufferSize);
    }

    /// <summary>
    /// NTFS keeps a rename through a power cut, but not always the bytes that the cache still holds, as the local copy of the
    /// data knows (<c>DataCopy</c>, Kiler.Storage). The stream is one of <see cref="CreateUpdate"/>, so it is a <see cref="FileStream"/>.
    /// </summary>
    public void FlushToDisk(Stream update) => ((FileStream)update).Flush(flushToDisk: true);

    /// <summary>
    /// Both names are in the folder of the local copy, so the move is a rename and never the copy that
    /// <see cref="File.Move(string, string, bool)"/> allows between volumes. A running exe can be renamed (M1.0b).
    /// </summary>
    public void Rename(string from, string to) => File.Move(from, to, overwrite: false);

    public bool TryDelete(string path) => DeleteQuietly(path);

    /// <summary>The attributes and the times come with the names, so a reparse point is known without opening it.</summary>
    public IReadOnlyList<FolderFile> List(string folder) =>
    [
        .. new DirectoryInfo(folder)
            .EnumerateFiles("*", new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false })
            .Select(file => new FolderFile(file.FullName, file.Attributes, file.CreationTimeUtc, file.LastWriteTimeUtc)),
    ];

    public void Delete(string path)
    {
        AllowDelete(path);
        File.Delete(path);
    }

    /// <summary>
    /// <see cref="FileShare.None"/> refuses the open while any other handle holds the file, as a check holds its
    /// <c>.new</c> and a first copy its <c>.tmp</c>; once open, no one else can open it until it is deleted with the handle.
    /// </summary>
    public void DeleteUnheld(string path)
    {
        AllowDelete(path);
        using var unheld = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None, 1, FileOptions.DeleteOnClose);
    }

    /// <summary>
    /// Looks at the file again just before its delete (security review of M3.7a). One that has become a link since it was
    /// listed throws, so the open with <see cref="FileOptions.DeleteOnClose"/> never deletes its target, and the attributes of
    /// a target are never changed. A read-only one loses the attribute: <see cref="File.Delete"/> refuses it, and that open
    /// leaves it without an error. A first copy was read-only before M3.7a when the exe on the shared folder was.
    /// </summary>
    private static void AllowDelete(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException($"{path} is a link, not a file that Kiler made.");
        }

        if ((attributes & FileAttributes.ReadOnly) != 0)
        {
            File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
        }
    }

    public static bool IsNetworkPath(string path)
    {
        path = path.Replace('/', '\\');

        // A device path: \\?\UNC\server\share is a UNC path, and \\?\C:\ is a drive.
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal))
        {
            path = path[4..];
            if (path.StartsWith(@"UNC\", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        if (path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return true;
        }

        // Only a drive letter has a drive type. Anything else, such as a volume GUID path, is taken as local.
        return path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':'
            && new DriveInfo(path[..1]).DriveType == DriveType.Network;
    }

    /// <summary>Litter at worst, when it fails: the file never has the real name.</summary>
    private static bool DeleteQuietly(string file)
    {
        try
        {
            File.Delete(file);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}

namespace Kiler.Storage;

/// <summary>
/// The file operations of the data layer. <see cref="FileSystem"/> does them on System.IO and, to open a path as itself,
/// on Windows (<see cref="NativeFiles"/>). The tests put a fake in its place to make one step fail (M2.3), count the reads at
/// a time (M2.7) or take the shared folder away (M2.8). The operations are synchronous: the open of a file, which is what
/// costs over SMB, is synchronous in Windows anyway, and a write runs to its end once started (decided in M2.3).
/// </summary>
internal interface IFileSystem
{
    /// <summary>The attributes of a path as itself, so a link shows as a link; <c>null</c> when nothing is there.</summary>
    FileAttributes? Attributes(string path);

    /// <summary>The entries of a folder with their attributes, hidden and system ones too, without <c>.</c> and <c>..</c>.</summary>
    IReadOnlyList<FileEntry> List(string folder);

    /// <summary>
    /// Makes one folder in a folder that exists, never a chain. A folder that exists is left as it is; a link or a file in
    /// its place is an <see cref="IOException"/>.
    /// </summary>
    void CreateFolder(string path);

    /// <summary>Renames a folder in the same folder. It never replaces: when the new name is taken, an <see cref="IOException"/>.</summary>
    void MoveFolder(string from, string to);

    /// <summary>
    /// Holds a folder, opened as itself, until the result is disposed (<see cref="PinnedFolder"/>): no one can rename or
    /// delete it or a folder above it, so it cannot be swapped for a link; an empty one can still be made a link in place,
    /// which <see cref="PinnedFolder.ThrowIfLink"/> sees. A missing folder is a <see cref="DirectoryNotFoundException"/>; a
    /// link or a file in its place an <see cref="IOException"/>.
    /// </summary>
    PinnedFolder HoldFolder(string path);

    /// <summary>
    /// Opens a file to read, as itself, shared with writes and deletes; <c>null</c> when no file is there. A link in its place
    /// is an <see cref="IOException"/>. A rename over the file still fails while it is open (measured in M2.2), which the
    /// writes retry. A file that cannot be opened, such as a folder in its place or a file that someone holds without
    /// sharing, is an <see cref="UnauthorizedAccessException"/> or an <see cref="IOException"/>.
    /// </summary>
    Stream? OpenRead(string path);

    /// <summary>
    /// Makes a new file with the bytes, flushed to the disk, and keeps it open, shared to be read, renamed or deleted but not
    /// written, so no one changes it before it is renamed into place, and the document it becomes can be read at once by a
    /// reader that shares writes, as <see cref="OpenRead"/> does; one that shares only reads waits until it is closed.
    /// <c>null</c> when the name is taken, a link too: it never replaces a file and never follows a link.
    /// </summary>
    NewFile? CreateFile(string path, ReadOnlySpan<byte> bytes);

    /// <summary>
    /// Sets the creation time of a new file to its write time, through its handle, and the write time to itself, which keeps
    /// the file system from moving it when the file is closed. Gives the size and the times.
    /// </summary>
    FileStamp SetCreationToWriteTime(NewFile file);

    /// <summary>
    /// Renames a file in the same folder, in one step also over SMB. Without <paramref name="replace"/>, <c>false</c> when the
    /// new name is taken, a link too. With it, an <see cref="UnauthorizedAccessException"/> while someone holds the file at
    /// the new name open, with or without delete sharing (M2.2), or when that file is read-only; a file server may give a
    /// sharing violation instead.
    /// </summary>
    bool MoveFile(string from, string to, bool replace);

    /// <summary>Deletes a file; nothing when no file is there. A file that someone holds open with delete sharing goes too.</summary>
    void DeleteFile(string path);

    /// <summary>
    /// Holds a file, opened as itself to read and to be deleted, shared only to be read: no one else can open it to write or
    /// delete it, or rename it, until the result is disposed (M2.4). <c>null</c> when no file is there. A link in its place
    /// is an <see cref="IOException"/>, a folder an <see cref="UnauthorizedAccessException"/>. A file that someone else holds
    /// open without sharing the delete, or to write or delete it, another hold too, is an <see cref="IOException"/> with a
    /// sharing violation.
    /// </summary>
    HeldFile? HoldFile(string path);

    /// <summary>
    /// Deletes a held file through its handle, so the file that was read goes, never another one put at its name. Its name
    /// goes when the hold is let go, also while others read the file. A file system that does not delete so keeps the name
    /// until the last handle to the file is closed, and until then a rename to that name finds it taken. A file that may not
    /// be deleted, such as a read-only one, is an <see cref="UnauthorizedAccessException"/>.
    /// </summary>
    void DeleteHeldFile(HeldFile file);

    /// <summary>Waits between two tries of a step. The tests count the waits instead.</summary>
    void Wait(TimeSpan time);
}

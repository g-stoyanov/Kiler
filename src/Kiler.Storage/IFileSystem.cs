namespace Kiler.Storage;

/// <summary>
/// The file operations of the data layer. <see cref="FileSystem"/> does them on System.IO and, to open a path as itself,
/// on Windows (<see cref="NativeFiles"/>). The tests put a fake in its place to make one step fail (M2.3), count the reads at
/// a time (M2.7) or take the shared folder away (M2.8a). The operations are synchronous: the open of a file, which is what
/// costs over SMB, is synchronous in Windows anyway, and a write runs to its end once started (decided in M2.3).
/// </summary>
internal interface IFileSystem
{
    /// <summary>The attributes of a path as itself, so a link shows as a link; <c>null</c> when nothing is there.</summary>
    FileAttributes? Attributes(string path);

    /// <summary>
    /// The entries of a folder with their attributes, hidden and system ones too, without <c>.</c> and <c>..</c>; at most
    /// <paramref name="limit"/> of them, as anyone can put any number of names in a folder of the share (M2.7).
    /// </summary>
    IReadOnlyList<FileEntry> List(string folder, int limit = int.MaxValue);

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
    /// link or a file in its place an <see cref="IOException"/>. With <paramref name="readOwner"/>, <see cref="Owner(PinnedFolder)"/>
    /// can read its owner (M2.6); without it, the owner is <c>null</c>.
    /// </summary>
    PinnedFolder HoldFolder(string path, bool readOwner = false);

    /// <summary>
    /// Holds a folder as <see cref="HoldFolder"/> does, and makes it first when it is missing, in one call that never follows
    /// a link at its name (M2.5). The folder above must exist: it never makes a chain. A link or a file in its place is an
    /// <see cref="IOException"/>, a missing folder above a <see cref="DirectoryNotFoundException"/>. With
    /// <paramref name="readOwner"/>, as for <see cref="HoldFolder"/>.
    /// </summary>
    PinnedFolder HoldOrCreateFolder(string path, bool readOwner = false);

    /// <summary>
    /// Makes a new folder and holds it, as <see cref="HoldOrCreateFolder"/> does, but never takes one that is there (M2.6):
    /// <c>null</c> when the name is taken, by a folder, a file or a link, so a folder that someone made first is never taken
    /// for one that the layer made.
    /// </summary>
    PinnedFolder? HoldNewFolder(string path, bool readOwner = false);

    /// <summary>
    /// The id of a held folder, through its handle: the serial number of its volume and its index there, which a rename keeps
    /// on NTFS (M2.6), as for a file (<see cref="FileId"/>).
    /// </summary>
    FileId Id(PinnedFolder folder);

    /// <summary>
    /// Opens a file to read, as itself, shared with writes and deletes; <c>null</c> when no file is there. A link in its place
    /// is an <see cref="IOException"/>. A rename over the file still fails while it is open (measured in M2.2), which the
    /// writes retry. A file that cannot be opened, such as a folder in its place or a file that someone holds without
    /// sharing, is an <see cref="UnauthorizedAccessException"/> or an <see cref="IOException"/>.
    /// </summary>
    Stream? OpenRead(string path);

    /// <summary>The size and the times of a file that <see cref="OpenRead"/> opened, through its handle.</summary>
    FileStamp Stamp(Stream file);

    /// <summary>
    /// The owner of a file that <see cref="OpenRead"/> opened, through its handle, so it is that of the file that is read,
    /// whatever is at its name by then (M2.6); <c>null</c> when it cannot be read (<see cref="IFileOwner.Of"/>).
    /// </summary>
    string? Owner(Stream file);

    /// <summary>
    /// The owner of a held folder, through its handle; <c>null</c> when it cannot be read (<see cref="IFileOwner.Of"/>), as when
    /// it was not held for it (<see cref="HoldFolder"/>).
    /// </summary>
    string? Owner(PinnedFolder folder);

    /// <summary>
    /// Opens a file as itself only to read and set its times (<see cref="FileTimes"/>), which stops no one from opening,
    /// moving or deleting it; <c>null</c> when no file is there. As any open handle, it stops a rename over the file (measured
    /// in M2.5), so it is held only for a move. When the times may not be set, they are only read
    /// (<see cref="FileTimes.CanSet"/>). A link in its place is an <see cref="IOException"/>, a folder an
    /// <see cref="UnauthorizedAccessException"/>.
    /// </summary>
    FileTimes? OpenTimes(string path);

    /// <summary>
    /// Puts back the creation time that the file had when it was opened, through its handle, wherever it is now; nothing when
    /// the times may not be set.
    /// </summary>
    void KeepCreationTime(FileTimes file);

    /// <summary>
    /// Makes a marker: a new empty file that keeps its folder from being empty while it is held, so no one can make the folder
    /// a mount point in place, as NTFS refuses one on a folder that is not empty (measured in M2.5). No one can delete, rename
    /// or write it, and it is deleted when it is let go, also when the process ends. <c>null</c> when the name is taken, a
    /// link too.
    /// </summary>
    IDisposable? CreateMarker(string path);

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
    /// Renames a file in its folder, or moves it to another folder of the same root, such as the trash (M2.5), in one step
    /// also over SMB. Without <paramref name="replace"/>, <c>false</c> when the new name is taken, a link too. With it, an
    /// <see cref="UnauthorizedAccessException"/> while someone holds the file at the new name open, with or without delete
    /// sharing (M2.2), or when that file is read-only; a file server may give a sharing violation instead. A file that someone
    /// holds open without delete sharing cannot be moved: a sharing violation.
    /// </summary>
    bool MoveFile(string from, string to, bool replace);

    /// <summary>Deletes a file; nothing when no file is there. A file that someone holds open with delete sharing goes too.</summary>
    void DeleteFile(string path);

    /// <summary>
    /// Deletes a file that <see cref="CreateFile"/> made, through its handle, wherever a rename has taken it: the file goes
    /// when it is let go, never another one at its name (M2.5). A file that may not be deleted is an
    /// <see cref="UnauthorizedAccessException"/>.
    /// </summary>
    void DeleteNewFile(NewFile file);

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

    /// <summary>
    /// Holds a folder as <see cref="HoldFolder"/> does with <c>readOwner</c>, opened also to be deleted through its handle
    /// (<see cref="DeleteHeldFolder"/>, M2.7e). A folder that someone holds open without sharing the delete, as every hold of
    /// the layer does, or that may not be deleted, is an <see cref="IOException"/>.
    /// </summary>
    PinnedFolder HoldFolderToDelete(string path);

    /// <summary>
    /// Deletes a folder that <see cref="HoldFolderToDelete"/> holds, through its handle, so the folder that was held goes,
    /// never what a link names: one made a mount point in place goes as the link (measured in M2.7e). It goes when the hold is
    /// let go. A folder that is not empty is an <see cref="IOException"/>, as the file system refuses it, so anything put in
    /// it after a check stops the delete.
    /// </summary>
    void DeleteHeldFolder(PinnedFolder folder);

    /// <summary>Waits between two tries of a step. The tests count the waits instead.</summary>
    void Wait(TimeSpan time);
}

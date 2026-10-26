namespace Kiler.Storage;

/// <summary>
/// The file operations of the data layer. <see cref="FileSystem"/> does them on System.IO. The tests put a fake in its place
/// to make one step fail (M2.3), count the reads at a time (M2.7) or take the shared folder away (M2.8).
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
    /// Opens a file to read, shared with writes and deletes; <c>null</c> when no file is there. A rename over the file still
    /// fails while it is open (measured in M2.2), which the writes of M2.3 handle. A file that cannot be opened, such as a
    /// folder in its place or a file that someone holds without sharing, is an <see cref="UnauthorizedAccessException"/> or
    /// an <see cref="IOException"/>.
    /// </summary>
    Stream? OpenRead(string path);
}

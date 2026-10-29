using Microsoft.Win32.SafeHandles;

namespace Kiler.Storage;

/// <summary>
/// A file opened as itself only to read and set its times (M2.5). Such a handle stops no one from opening, moving or deleting
/// the file, and it follows the file through a move, so the layer can put back the creation time that a move into a name
/// that another file left in the last 15 s takes from that file on NTFS (file tunneling, M2.3). As any open handle, it stops
/// a rename over the file (measured in M2.5), so the layer holds it only for the move.
/// </summary>
internal sealed class FileTimes(string path, SafeFileHandle handle, FileStamp stamp, FileId id, bool canSet) : IDisposable
{
    /// <summary>Where the file was opened. A move takes it elsewhere; the handle follows it.</summary>
    public string Path { get; } = path;

    /// <summary>The id of the file, which a move keeps, so the layer can tell that the file at the new name is this one.</summary>
    public FileId Id { get; } = id;

    /// <summary>
    /// Whether the times may be set. A file server may let anyone move a file in a folder they can write, but set its times
    /// only for its owner (POSIX rights on a NAS); then the move goes on, and the times stay as the move leaves them.
    /// </summary>
    public bool CanSet { get; } = canSet;

    /// <summary>The size and the times of the file when it was opened, on the clock of the file server.</summary>
    public FileStamp Stamp { get; } = stamp;

    public SafeFileHandle Handle { get; } = handle;

    public void Dispose() => Handle.Dispose();
}

/// <summary>
/// The id of a file: the serial number of its volume and its 64-bit index there. On NTFS a move in the volume keeps it, and no
/// other file of the volume has it at the same time (measured locally and over SMB in M2.5). Not so elsewhere (third security
/// review): FAT and exFAT make the index from the folder entry, so it changes with a move and every move to the trash would
/// be refused after it was done; ReFS has 128-bit ids, whose 64-bit form may not be unique; and a file system that gives 0
/// for every file makes the check pass without telling anything. M2.11a checks the real file server.
/// </summary>
internal readonly record struct FileId(uint Volume, ulong Index);

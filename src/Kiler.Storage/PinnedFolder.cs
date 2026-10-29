using Microsoft.Win32.SafeHandles;

namespace Kiler.Storage;

/// <summary>
/// A folder held open without delete sharing. While it is open, Windows refuses to rename or delete it, also on a network
/// share, so no one can swap it for a link to another folder (the folders above it: <see cref="NativeFiles.OpenFolder"/>). Anyone who can write it can still make it a mount point in place while it is empty (measured in M2.3), so the
/// layer checks its folders with <see cref="ThrowIfLink"/> after a read and before it renames into them. The reads and
/// writes of the data layer hold their folders so (M2.3), and <c>--diag</c> its temporary subfolder (M1.11).
/// </summary>
public sealed class PinnedFolder : IDisposable
{
    private readonly SafeFileHandle _handle;
    private readonly string _path;

    private PinnedFolder(SafeFileHandle handle, string path)
    {
        _handle = handle;
        _path = path;
    }

    /// <summary>
    /// Opens the folder as itself and holds it. A missing folder is a <see cref="DirectoryNotFoundException"/>; a link or a
    /// file in its place, or a folder that cannot be opened, an <see cref="IOException"/>.
    /// </summary>
    public static PinnedFolder Open(string path) => Open(path, readOwner: false);

    /// <summary>
    /// Holds the folder as <see cref="Open(string)"/> does; with <paramref name="readOwner"/> the handle can read its owner too
    /// (<c>READ_CONTROL</c>, M2.6), which only the holds that read it ask.
    /// </summary>
    internal static PinnedFolder Open(string path, bool readOwner) => new(NativeFiles.OpenFolder(path, readOwner), path);

    /// <summary>
    /// Holds the folder as <see cref="Open(string)"/> does, and makes it first when it is missing, in one call that never
    /// follows a link at its name (M2.5). The folder above must exist. With <paramref name="readOwner"/> the handle can read
    /// its owner too (M2.6).
    /// </summary>
    internal static PinnedFolder OpenOrCreate(string path, bool readOwner = false) => new(NativeFiles.OpenOrCreateFolder(path, readOwner), path);

    /// <summary>
    /// Makes a new folder and holds it, as <see cref="OpenOrCreate"/> does, but never takes one that is there (M2.6): <c>null</c>
    /// when the name is taken, by a folder, a file or a link.
    /// </summary>
    internal static PinnedFolder? CreateNew(string path, bool readOwner = false) =>
        NativeFiles.CreateNewFolder(path, readOwner) is { } handle ? new(handle, path) : null;

    /// <summary>
    /// Holds the folder as <see cref="Open(string)"/> does, with its owner, and opened to be deleted through the handle
    /// (M2.7e): the hold still shares no delete, so no one else can rename or delete it while it is held.
    /// </summary>
    internal static PinnedFolder OpenToDelete(string path) => new(NativeFiles.OpenFolder(path, readOwner: true, toDelete: true), path);

    /// <summary>Where the folder was held.</summary>
    internal string HeldPath => _path;

    /// <summary>The handle, which reads the owner of the folder when it was held for it (M2.6).</summary>
    internal SafeFileHandle Handle => _handle;

    /// <summary>An <see cref="IOException"/> when the held folder has become a link, read through the handle.</summary>
    public void ThrowIfLink()
    {
        if (NativeFiles.IsLink(_handle))
        {
            throw new IOException($"The folder has become a link, which Kiler does not follow: {_path}");
        }
    }

    public void Dispose() => _handle.Dispose();
}

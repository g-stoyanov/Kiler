using Microsoft.Win32.SafeHandles;

namespace Kiler.Storage;

/// <summary>
/// A folder held open without delete sharing. While it is open, Windows refuses to rename or delete it or any folder above
/// it, also on a network share, so no one can swap it for a link to another folder: the paths in it stay the ones Kiler
/// checked. Anyone who can write it can still make it a mount point in place while it is empty (measured in M2.3), so the
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
    public static PinnedFolder Open(string path) => new(NativeFiles.OpenFolder(path), path);

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

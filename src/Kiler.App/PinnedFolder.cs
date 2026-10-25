using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Kiler.App;

/// <summary>
/// A folder held open without delete sharing. While it is open, Windows refuses to rename or delete it or any folder above
/// it, also on a network share, so no one can swap it for a link to another folder: the paths in it stay the ones Kiler
/// made. <c>--diag</c> holds its temporary subfolder so on the shared folder, where everyone has Full control.
/// </summary>
internal sealed partial class PinnedFolder : IDisposable
{
    /// <summary><c>FILE_LIST_DIRECTORY</c>: a data access. A handle with only attribute access would not take part in the sharing check.</summary>
    private const uint ListDirectory = 0x1;

    private const uint ReadAttributes = 0x80;
    private const uint OpenExisting = 3;

    /// <summary><c>FILE_FLAG_BACKUP_SEMANTICS</c>, which a folder needs to be opened.</summary>
    private const uint BackupSemantics = 0x02000000;

    /// <summary><c>FILE_FLAG_OPEN_REPARSE_POINT</c>: a link is opened as itself, not as its target, and then refused.</summary>
    private const uint OpenReparsePoint = 0x00200000;

    private readonly SafeFileHandle _handle;

    private PinnedFolder(SafeFileHandle handle) => _handle = handle;

    /// <summary>Opens the folder and holds it; an <see cref="IOException"/> when it cannot, or when it is a link or a file.</summary>
    public static PinnedFolder Open(string path)
    {
        // Read and write sharing, so Kiler and others can still list it, but not delete sharing, which a rename needs.
        var handle = CreateFile(path, ListDirectory | ReadAttributes, (uint)(FileShare.Read | FileShare.Write), 0, OpenExisting, BackupSemantics | OpenReparsePoint, 0);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw new IOException($"Kiler cannot hold the folder open: {new Win32Exception(error).Message}");
        }

        try
        {
            var attributes = File.GetAttributes(handle);
            if (attributes.HasFlag(FileAttributes.ReparsePoint) || !attributes.HasFlag(FileAttributes.Directory))
            {
                throw new IOException($"Not a folder but a link or a file: {path}");
            }
        }
        catch
        {
            handle.Dispose();
            throw;
        }

        return new PinnedFolder(handle);
    }

    public void Dispose() => _handle.Dispose();

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFile(
        string fileName, uint desiredAccess, uint shareMode, nint securityAttributes, uint creationDisposition, uint flagsAndAttributes, nint templateFile);
}

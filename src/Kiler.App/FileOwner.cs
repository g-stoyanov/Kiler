using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Kiler.Storage;
using Microsoft.Win32.SafeHandles;

namespace Kiler.App;

/// <summary>
/// The owner that Windows records for a file or a folder (design 7.8). <c>--diag</c> checks it on the shared folder by path
/// (M1.11), and the data layer reads it through the handle it reads the file with (M2.6, <see cref="IFileOwner"/>).
/// </summary>
internal sealed partial class FileOwner : IFileOwner
{
    /// <summary><c>SE_FILE_OBJECT</c>, a file or a folder.</summary>
    private const int FileObject = 1;

    /// <summary><c>OWNER_SECURITY_INFORMATION</c>: only the owner, which <c>READ_CONTROL</c> lets the handle read.</summary>
    private const uint OwnerInformation = 1;

    private FileOwner()
    {
    }

    /// <summary>The owners of Windows for the data layer.</summary>
    public static FileOwner Instance { get; } = new();

    public static SecurityIdentifier Of(FileSystemInfo item)
    {
        FileSystemSecurity security = item switch
        {
            DirectoryInfo folder => folder.GetAccessControl(AccessControlSections.Owner),
            FileInfo file => file.GetAccessControl(AccessControlSections.Owner),
            _ => throw new ArgumentException($"Neither a file nor a folder: {item.GetType().Name}", nameof(item)),
        };
        return security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier
            ?? throw new InvalidOperationException($"Windows records no owner for {item.FullName}.");
    }

    /// <summary>
    /// The owner of the file or the folder of an open handle, through GetSecurityInfo, which asks the handle and never a path
    /// (M2.6). <c>null</c> when it cannot be read: the handle has no <c>READ_CONTROL</c>, the file system keeps no owners or
    /// gives another error, none is recorded, or it is not a valid SID. The last two are only defended against, not seen.
    /// </summary>
    public static SecurityIdentifier? Of(SafeFileHandle file)
    {
        if (GetSecurityInfo(file, FileObject, OwnerInformation, out var owner, 0, 0, 0, out var descriptor) != 0)
        {
            return null;
        }

        try
        {
            return owner == 0 ? null : new SecurityIdentifier(owner);
        }
        catch (ArgumentException)
        {
            // Not a SID of revision 1 with at most 15 parts.
            return null;
        }
        finally
        {
            LocalFree(descriptor);
        }
    }

    string? IFileOwner.Of(SafeFileHandle file) => Of(file)?.Value;

    /// <summary>The owner points into the descriptor, which <see cref="LocalFree"/> lets go.</summary>
    [LibraryImport("advapi32.dll")]
    private static partial uint GetSecurityInfo(
        SafeFileHandle handle, int objectType, uint securityInformation, out nint owner, nint group, nint dacl, nint sacl, out nint descriptor);

    [LibraryImport("kernel32.dll")]
    private static partial nint LocalFree(nint memory);
}

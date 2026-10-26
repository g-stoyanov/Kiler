using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Kiler.Storage;

/// <summary>
/// Opens a folder or a file as itself (M2.3): a link is opened as the link, not as what it points to, and then refused
/// through the handle. Anyone can put a link on the shared folder, and .NET cannot open a path so, hence CreateFileW. Kiler
/// runs on Windows only (PLAN section 2); the guard keeps the rest of Kiler.Storage free of the platform analyzer.
/// </summary>
internal static partial class NativeFiles
{
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint CreateNew = 1;

    /// <summary><c>FILE_LIST_DIRECTORY</c>: a data access. A handle with only attribute access does not count in the sharing check that refuses a rename (M1.11).</summary>
    private const uint ListDirectory = 0x1;

    private const uint ReadAttributes = 0x80;
    private const uint OpenExisting = 3;

    /// <summary><c>FILE_FLAG_BACKUP_SEMANTICS</c>, which a folder needs to be opened.</summary>
    private const uint BackupSemantics = 0x02000000;

    /// <summary><c>FILE_FLAG_OPEN_REPARSE_POINT</c>: the last part of the path is opened as itself, a link too.</summary>
    private const uint OpenReparsePoint = 0x00200000;

    /// <summary>
    /// <c>SECURITY_SQOS_PRESENT</c> with <c>SECURITY_ANONYMOUS</c>, as .NET opens its files: a named pipe in place of the path
    /// cannot act as the user.
    /// </summary>
    private const uint AnonymousToPipes = 0x00100000;

    private const string ExtendedPrefix = "\\\\?\\";
    private const string ExtendedUncPrefix = "\\\\?\\UNC\\";
    private const string DevicePrefix = "\\\\.\\";
    private const string UncPrefix = "\\\\";

    /// <summary>The length from which .NET adds the prefix to a folder's path; shorter paths stay as M1.11 and M2.1 measured them.</summary>
    private const int MaxShortFolderPath = 248;

    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;
    private const int ErrorAccessDenied = 5;
    private const int ErrorFileExists = 80;
    private const int ErrorAlreadyExists = 183;

    /// <summary>
    /// Opens a folder to hold it: shared to read and write, so others can still list it and work in it, but not to delete,
    /// which a rename needs. While the handle is open, Windows refuses to rename or delete the folder or a folder above it,
    /// also over SMB (M1.11). It does not stop anyone who can write the folder from making an empty one a mount point in
    /// place (measured in M2.3), which <see cref="IsLink"/> sees through the handle. A missing folder is a
    /// <see cref="DirectoryNotFoundException"/>; a link or a file in its place, or a folder that cannot be opened, an
    /// <see cref="IOException"/>.
    /// </summary>
    public static SafeFileHandle OpenFolder(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw NotWindows();
        }

        var handle = CreateFile(
            Extended(path), ListDirectory | ReadAttributes, (uint)(FileShare.Read | FileShare.Write), 0, OpenExisting, BackupSemantics | OpenReparsePoint | AnonymousToPipes, 0);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw error is ErrorFileNotFound or ErrorPathNotFound
                ? new DirectoryNotFoundException($"The folder does not exist: {path}")
                : new IOException($"Kiler cannot hold the folder open: {new Win32Exception(error).Message}", HResult(error));
        }

        return Checked(handle, attributes => !attributes.HasFlag(FileAttributes.ReparsePoint) && attributes.HasFlag(FileAttributes.Directory), $"Not a folder but a link or a file: {path}");
    }

    /// <summary>
    /// Opens a file to read, shared with writes and deletes; <c>null</c> when no file is there. A link in its place is an
    /// <see cref="IOException"/>. A folder in its place, or a file that someone holds without sharing, is an
    /// <see cref="UnauthorizedAccessException"/> or an <see cref="IOException"/>.
    /// </summary>
    public static SafeFileHandle? OpenFileToRead(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw NotWindows();
        }

        var handle = CreateFile(
            Extended(path), GenericRead, (uint)(FileShare.ReadWrite | FileShare.Delete), 0, OpenExisting, OpenReparsePoint | AnonymousToPipes, 0);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            return error switch
            {
                ErrorFileNotFound or ErrorPathNotFound => null,

                // A folder in place of the file: without BackupSemantics, CreateFileW refuses it so.
                ErrorAccessDenied => throw new UnauthorizedAccessException($"Kiler cannot open the file: {new Win32Exception(error).Message} {path}"),
                _ => throw new IOException($"Kiler cannot open the file: {new Win32Exception(error).Message} {path}", HResult(error)),
            };
        }

        return Checked(handle, attributes => !attributes.HasFlag(FileAttributes.ReparsePoint), $"The file is a link, which Kiler does not follow: {path}");
    }

    /// <summary>
    /// Makes a new file to read and write, shared to read and delete; <c>null</c> when the name is taken, a link at it too.
    /// <see cref="FileMode.CreateNew"/> of .NET followed a symbolic link whose target was missing and made the target on the
    /// CI machine (Windows Server, an administrator), though not on the developer machine (M2.3). The last part of the path
    /// opened as itself is never followed. A missing folder is a <see cref="DirectoryNotFoundException"/>; a folder that
    /// cannot be written an <see cref="UnauthorizedAccessException"/>.
    /// </summary>
    public static SafeFileHandle? CreateNewFile(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw NotWindows();
        }

        var handle = CreateFile(
            Extended(path), GenericRead | GenericWrite, (uint)(FileShare.Read | FileShare.Delete), 0, CreateNew, OpenReparsePoint | AnonymousToPipes, 0);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            return error switch
            {
                ErrorFileExists or ErrorAlreadyExists => null,
                ErrorPathNotFound => throw new DirectoryNotFoundException($"The folder of the file does not exist: {path}"),
                ErrorAccessDenied => throw new UnauthorizedAccessException($"Kiler cannot make the file: {new Win32Exception(error).Message} {path}"),
                _ => throw new IOException($"Kiler cannot make the file: {new Win32Exception(error).Message} {path}", HResult(error)),
            };
        }

        return handle;
    }

    /// <summary>Whether the file or folder of an open handle is a link now.</summary>
    public static bool IsLink(SafeFileHandle handle) => File.GetAttributes(handle).HasFlag(FileAttributes.ReparsePoint);

    /// <summary>
    /// The full path, with the prefix that lifts the limit of 260 characters when it is long, as .NET adds it in its own
    /// calls: without it, CreateFileW gives ERROR_PATH_NOT_FOUND for a longer path (measured in M2.3), and a document would
    /// look missing. The prefix turns off the normalization of the path, so the path is made full first.
    /// </summary>
    private static string Extended(string path)
    {
        if (path.StartsWith(ExtendedPrefix, StringComparison.Ordinal) || path.StartsWith(DevicePrefix, StringComparison.Ordinal))
        {
            return path;
        }

        var full = Path.GetFullPath(path);
        if (full.Length < MaxShortFolderPath)
        {
            return full;
        }

        return full.StartsWith(UncPrefix, StringComparison.Ordinal) ? ExtendedUncPrefix + full[UncPrefix.Length..] : ExtendedPrefix + full;
    }

    /// <summary>The handle when its attributes pass the check, else it is closed and refused with the message.</summary>
    private static SafeFileHandle Checked(SafeFileHandle handle, Func<FileAttributes, bool> check, string refused)
    {
        try
        {
            if (!check(File.GetAttributes(handle)))
            {
                throw new IOException(refused);
            }

            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static PlatformNotSupportedException NotWindows() => new("Kiler.Storage opens files through Windows.");

    /// <summary>The HRESULT of a Win32 error, as .NET gives it in <see cref="Exception.HResult"/>.</summary>
    private static int HResult(int error) => unchecked((int)0x80070000) | (error & 0xFFFF);

    [SupportedOSPlatform("windows")]
    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFile(
        string fileName, uint desiredAccess, uint shareMode, nint securityAttributes, uint creationDisposition, uint flagsAndAttributes, nint templateFile);
}

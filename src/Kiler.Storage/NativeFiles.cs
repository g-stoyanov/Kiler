using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Kiler.Storage;

/// <summary>
/// Opens a folder or a file as itself (M2.3): a link is opened as the link, not as what it points to, and then refused
/// through the handle. Anyone can put a link on the shared folder, and .NET cannot open a path so, hence CreateFileW. A file
/// is deleted through its handle (M2.4), which .NET cannot do either; a folder is made as itself through NtCreateFile, which
/// CreateDirectoryW does not do, and a file is moved without the copy that File.Move allows (M2.5). Kiler runs on Windows
/// only (PLAN section 2); the guard keeps the rest of Kiler.Storage free of the platform analyzer.
/// </summary>
internal static partial class NativeFiles
{
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint CreateNew = 1;

    /// <summary><c>FILE_WRITE_ATTRIBUTES</c>, which setting the times needs.</summary>
    private const uint WriteAttributes = 0x100;

    /// <summary><c>FILE_FLAG_DELETE_ON_CLOSE</c>: the file goes when its last handle is closed.</summary>
    private const uint DeleteOnCloseFlag = 0x04000000;

    /// <summary>
    /// <c>MOVEFILE_REPLACE_EXISTING</c>. A move never gets <c>MOVEFILE_COPY_ALLOWED</c>, which <see cref="File.Move(string, string, bool)"/>
    /// of .NET sets.
    /// </summary>
    private const uint ReplaceExisting = 0x1;

    /// <summary><c>SYNCHRONIZE</c>, which <c>FILE_SYNCHRONOUS_IO_NONALERT</c> needs.</summary>
    private const uint Synchronize = 0x00100000;

    /// <summary><c>FILE_ATTRIBUTE_NORMAL</c>, as CreateDirectoryW makes a folder.</summary>
    private const uint NormalAttributes = 0x80;

    /// <summary><c>FILE_OPEN_IF</c> of NtCreateFile: open what is there, else make it.</summary>
    private const uint OpenIf = 3;

    /// <summary><c>FILE_CREATE</c>: only a new folder; a name that is taken, a link too, is refused.</summary>
    private const uint CreateOnly = 2;

    /// <summary>
    /// <c>FILE_DIRECTORY_FILE</c>, <c>FILE_SYNCHRONOUS_IO_NONALERT</c> and <c>FILE_OPEN_REPARSE_POINT</c>: a folder, opened
    /// as itself, so a link at the name is opened as the link and never followed, also when it is made.
    /// </summary>
    private const uint FolderAsItself = 0x1 | 0x20 | 0x00200000;

    /// <summary><c>OBJ_CASE_INSENSITIVE</c>, as CreateFileW opens a path.</summary>
    private const uint CaseInsensitive = 0x40;

    /// <summary>The longest NT path that a <c>UNICODE_STRING</c> holds, in characters.</summary>
    private const int MaxNtPath = short.MaxValue;

    private const string NtPrefix = "\\??\\";
    private const string NtUncPrefix = "\\??\\UNC\\";

    private const int StatusObjectNameNotFound = unchecked((int)0xC0000034);
    private const int StatusObjectPathNotFound = unchecked((int)0xC000003A);
    private const int StatusAccessDenied = unchecked((int)0xC0000022);
    private const int StatusNotADirectory = unchecked((int)0xC0000103);
    private const int StatusObjectNameCollision = unchecked((int)0xC0000035);

    /// <summary><c>DELETE</c>, which a delete through the handle needs.</summary>
    private const uint Delete = 0x00010000;

    /// <summary><c>FileDispositionInfo</c> of <c>FILE_INFO_BY_HANDLE_CLASS</c>.</summary>
    private const int FileDispositionInfo = 4;

    /// <summary><c>FileDispositionInfoEx</c> of <c>FILE_INFO_BY_HANDLE_CLASS</c>.</summary>
    private const int FileDispositionInfoEx = 21;

    /// <summary><c>FILE_DISPOSITION_FLAG_DELETE</c> with <c>FILE_DISPOSITION_FLAG_POSIX_SEMANTICS</c>.</summary>
    private const uint DeletePosix = 0x1 | 0x2;

    /// <summary><c>FILE_LIST_DIRECTORY</c>: a data access. A handle with only attribute access does not count in the sharing check that refuses a rename (M1.11).</summary>
    private const uint ListDirectory = 0x1;

    private const uint ReadAttributes = 0x80;

    /// <summary>
    /// <c>READ_CONTROL</c>, which reading the owner through the handle needs (M2.6). <c>GENERIC_READ</c> has it, so a file opened
    /// to read has it too; a folder is held with it only where its owner is read, as a share may refuse it where it lets a
    /// folder be listed (code and security reviews of M2.6). No sharing check counts it.
    /// </summary>
    private const uint ReadControl = 0x00020000;

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
    /// place (measured in M2.3), which <see cref="IsLink"/> sees through the handle. With <paramref name="readOwner"/>, the
    /// handle can read the owner of the folder too (M2.6); with <paramref name="toDelete"/>, it can delete the folder
    /// (<see cref="DeleteOnClose"/>, M2.7e), which a folder that someone holds open without sharing the delete refuses. A
    /// missing folder is a <see cref="DirectoryNotFoundException"/>; a link or a file in its place, or a folder that cannot be
    /// opened, an <see cref="IOException"/>.
    /// </summary>
    public static SafeFileHandle OpenFolder(string path, bool readOwner = false, bool toDelete = false)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw NotWindows();
        }

        var access = ListDirectory | ReadAttributes | (readOwner ? ReadControl : 0) | (toDelete ? Delete : 0);
        var handle = CreateFile(
            Extended(path), access, (uint)(FileShare.Read | FileShare.Write), 0, OpenExisting, BackupSemantics | OpenReparsePoint | AnonymousToPipes, 0);
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
    /// Holds a folder as <see cref="OpenFolder"/> does, and makes it first when it is missing, in one call (M2.5): NtCreateFile
    /// with <c>FILE_OPEN_IF</c>, as a folder and as itself. A link at the name is opened as the link and refused, and its
    /// target is never made, which CreateDirectoryW, with no flag for it, may do: the CI machine followed such a link with a
    /// create-new of a file (M2.3). No one can put a link at the name between the create and the hold, as they are one call.
    /// Only the last folder is made, never a chain: a missing folder above is a <see cref="DirectoryNotFoundException"/>. A
    /// link or a file in its place is an <see cref="IOException"/>; a folder that may not be made or opened an
    /// <see cref="UnauthorizedAccessException"/>. With <paramref name="readOwner"/>, the handle can read the owner too (M2.6).
    /// </summary>
    public static SafeFileHandle OpenOrCreateFolder(string path, bool readOwner = false) => MakeFolder(path, readOwner, OpenIf)!;

    /// <summary>
    /// Makes a new folder and holds it, as <see cref="OpenOrCreateFolder"/> does, but never opens one that is there
    /// (<c>FILE_CREATE</c>, M2.6): <c>null</c> when the name is taken, by a folder, a file or a link, so a folder that someone
    /// else made first is never taken for one that Kiler made.
    /// </summary>
    public static SafeFileHandle? CreateNewFolder(string path, bool readOwner = false) => MakeFolder(path, readOwner, CreateOnly);

    /// <summary>The call of <see cref="OpenOrCreateFolder"/> and <see cref="CreateNewFolder"/>; <c>null</c> when a new folder's name is taken.</summary>
    private static unsafe SafeFileHandle? MakeFolder(string path, bool readOwner, uint disposition)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw NotWindows();
        }

        var name = NtPath(path);
        if (name.Length > MaxNtPath)
        {
            throw new PathTooLongException($"The path is too long for Windows: {path}");
        }

        SafeFileHandle handle;
        int status;
        fixed (char* buffer = name)
        {
            var bytes = (ushort)(name.Length * sizeof(char));
            var unicode = new UnicodeString { Length = bytes, MaximumLength = bytes, Buffer = buffer };

            // SECURITY_ANONYMOUS with static tracking, as CreateFileW opens with SECURITY_SQOS_PRESENT.
            var quality = new SecurityQualityOfService { Length = (uint)sizeof(SecurityQualityOfService) };
            var attributes = new ObjectAttributes
            {
                Length = (uint)sizeof(ObjectAttributes),
                ObjectName = &unicode,
                Attributes = CaseInsensitive,
                SecurityQualityOfService = &quality,
            };
            IoStatusBlock io;
            var access = ListDirectory | ReadAttributes | (readOwner ? ReadControl : 0) | Synchronize;
            status = NtCreateFile(
                out handle, access, &attributes, &io, null, NormalAttributes, (uint)(FileShare.Read | FileShare.Write), disposition, FolderAsItself, null, 0);
        }

        if (status == StatusObjectNameCollision && disposition == CreateOnly)
        {
            handle.Dispose();
            return null;
        }

        if (status < 0)
        {
            handle.Dispose();
            var error = (int)RtlNtStatusToDosError(status);
            throw status switch
            {
                StatusObjectNameNotFound or StatusObjectPathNotFound => new DirectoryNotFoundException($"The folder above it does not exist: {path}"),
                StatusNotADirectory => new IOException($"A file or a link to a file is where a folder should be: {path}", HResult(error)),
                StatusAccessDenied => new UnauthorizedAccessException($"Kiler cannot make or hold the folder: {new Win32Exception(error).Message} {path}"),
                _ => new IOException($"Kiler cannot make or hold the folder: {new Win32Exception(error).Message} {path}", HResult(error)),
            };
        }

        return Checked(handle, attributes => !attributes.HasFlag(FileAttributes.ReparsePoint) && attributes.HasFlag(FileAttributes.Directory), $"A link is where a folder should be: {path}");
    }

    /// <summary>
    /// Opens a file only for its times, to read them and, with <paramref name="toSet"/>, to set them, shared with everything
    /// (M2.5). A handle with only attribute access counts in no sharing check (M1.11), so it stops no one from opening, moving
    /// or deleting the file, and it follows the file through a move. It still stops a rename over the file, as any open handle
    /// does (measured in M2.5). <c>null</c> when no file is there. A link in its place is an <see cref="IOException"/>; a
    /// folder, or times that may not be set, an <see cref="UnauthorizedAccessException"/>.
    /// </summary>
    public static SafeFileHandle? OpenFileForTimes(string path, bool toSet) =>
        OpenFile(path, toSet ? ReadAttributes | WriteAttributes : ReadAttributes, FileShare.ReadWrite | FileShare.Delete);

    /// <summary>
    /// Renames a file, or moves it to another folder of the same volume, in one step, also over SMB (M2.5). It never copies:
    /// <see cref="File.Move(string, string, bool)"/> of .NET allows MoveFileExW to copy, so a move whose new folder is on
    /// another volume, as through a mount point, would be a copy and then a delete, and a crash between them would leave the
    /// file in both places. Such a move fails here with ERROR_NOT_SAME_DEVICE, an <see cref="IOException"/>. Like
    /// <see cref="File.Move(string, string, bool)"/>, a taken name without <paramref name="replace"/> is an
    /// <see cref="IOException"/> with ERROR_ALREADY_EXISTS or ERROR_FILE_EXISTS, a missing file a
    /// <see cref="FileNotFoundException"/>, access denied an <see cref="UnauthorizedAccessException"/>, and a sharing violation
    /// an <see cref="IOException"/>. A rename never follows a link at the new name: it replaces it or finds the name taken.
    /// Unlike <see cref="File.Move(string, string, bool)"/>, which looks for a file first, it moves whatever is at
    /// <paramref name="from"/>, a folder or a link too, as itself (second security review of M2.5).
    /// </summary>
    public static void MoveFile(string from, string to, bool replace)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw NotWindows();
        }

        if (MoveFileEx(Extended(from), Extended(to), replace ? ReplaceExisting : 0))
        {
            return;
        }

        var error = Marshal.GetLastPInvokeError();
        var message = $"Kiler cannot move the file: {new Win32Exception(error).Message} {from}";
        throw error switch
        {
            ErrorFileNotFound => new FileNotFoundException(message, from),
            ErrorPathNotFound => new DirectoryNotFoundException(message),
            ErrorAccessDenied => new UnauthorizedAccessException(message),
            _ => new IOException(message, HResult(error)),
        };
    }

    /// <summary>
    /// Opens a file to read, shared with writes and deletes; <c>null</c> when no file is there. A link in its place is an
    /// <see cref="IOException"/>. A folder in its place, or a file that someone holds without sharing, is an
    /// <see cref="UnauthorizedAccessException"/> or an <see cref="IOException"/>.
    /// </summary>
    public static SafeFileHandle? OpenFileToRead(string path) => OpenFile(path, GenericRead, FileShare.ReadWrite | FileShare.Delete);

    /// <summary>
    /// Opens a file to read and to be deleted through the handle (<see cref="DeleteOnClose"/>), shared only to be read: no one
    /// else can open it to write, rename or delete it while it is open (M2.4). <c>null</c> when no file is there. A link in
    /// its place is an <see cref="IOException"/>, a folder an <see cref="UnauthorizedAccessException"/>, and a file that
    /// someone holds open without sharing a delete, or to write or delete it, an <see cref="IOException"/> with a sharing
    /// violation.
    /// </summary>
    public static SafeFileHandle? OpenFileToDelete(string path) => OpenFile(path, GenericRead | Delete, FileShare.Read);

    /// <summary>
    /// Marks the file of a handle opened with delete access to be deleted, so the file that was opened goes, whatever is at
    /// its name by then (M2.4). Its name goes when this handle is closed, also while others read the file, as DeleteFileW
    /// deletes since Windows 10 (POSIX semantics, measured in M2.4). A file system that does not take them, as a file
    /// server may answer with any error, gets the classic delete: the file goes when the last handle to it is closed, and
    /// until then its name stays taken. A file that may not be deleted, such as a read-only one, is an
    /// <see cref="UnauthorizedAccessException"/>. A folder goes so too (M2.7e), and one that is not empty is an
    /// <see cref="IOException"/> with ERROR_DIR_NOT_EMPTY: the file system refuses it in both ways.
    /// </summary>
    public static void DeleteOnClose(SafeFileHandle handle)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw NotWindows();
        }

        // FILE_DISPOSITION_INFO_EX: the flags, a DWORD.
        var flags = DeletePosix;
        if (SetFileInformationByHandle(handle, FileDispositionInfoEx, ref flags, sizeof(uint)))
        {
            return;
        }

        // FILE_DISPOSITION_INFO: one BOOLEAN. What refuses the delete, such as the read-only attribute, refuses it again.
        byte deleteFile = 1;
        if (SetFileInformationByHandle(handle, FileDispositionInfo, ref deleteFile, sizeof(byte)))
        {
            return;
        }

        var error = Marshal.GetLastPInvokeError();
        throw error == ErrorAccessDenied
            ? new UnauthorizedAccessException($"Kiler cannot delete the file or folder: {new Win32Exception(error).Message}")
            : new IOException($"Kiler cannot delete the file or folder: {new Win32Exception(error).Message}", HResult(error));
    }

    /// <summary>Opens a file that exists, as itself, as <see cref="OpenFileToRead"/> says.</summary>
    private static SafeFileHandle? OpenFile(string path, uint access, FileShare share)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw NotWindows();
        }

        var handle = CreateFile(Extended(path), access, (uint)share, 0, OpenExisting, OpenReparsePoint | AnonymousToPipes, 0);
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
    /// Makes a new file to read, write and delete through its handle (<see cref="DeleteOnClose"/>, M2.5), shared to read and
    /// delete; <c>null</c> when the name is taken, a link at it too. <see cref="FileMode.CreateNew"/> of .NET followed a
    /// symbolic link whose target was missing and made the target on the CI machine (Windows Server, an administrator), though
    /// not on the developer machine (M2.3). The last part of the path opened as itself is never followed. A missing folder is
    /// a <see cref="DirectoryNotFoundException"/>; a folder that cannot be written an <see cref="UnauthorizedAccessException"/>.
    /// </summary>
    public static SafeFileHandle? CreateNewFile(string path) =>
        CreateNewFile(path, GenericRead | GenericWrite | Delete, FileShare.Read | FileShare.Delete, OpenReparsePoint | AnonymousToPipes);

    /// <summary>
    /// Makes a marker, a new empty file that keeps its folder from being empty while the handle is open (M2.5): NTFS refuses
    /// to make a folder that is not empty a mount point (ERROR_DIR_NOT_EMPTY, measured). It has only delete access and is
    /// shared only to read, so no one can delete, rename or write it, and Windows deletes it when the handle is closed, also
    /// when the process ends (<c>FILE_FLAG_DELETE_ON_CLOSE</c>). <c>null</c> when the name is taken, a link at it too; the
    /// errors are those of <see cref="CreateNewFile(string)"/>.
    /// </summary>
    public static SafeFileHandle? CreateMarkerFile(string path) =>
        CreateNewFile(path, Delete, FileShare.Read, DeleteOnCloseFlag | OpenReparsePoint | AnonymousToPipes);

    /// <summary>
    /// The id of the file of a handle (M2.5): the serial number of its volume and its index there, through
    /// GetFileInformationByHandle, which needs only attribute access. A move keeps it; another file has another one.
    /// </summary>
    public static FileId Id(SafeFileHandle handle)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw NotWindows();
        }

        if (!GetFileInformationByHandle(handle, out var information))
        {
            var error = Marshal.GetLastPInvokeError();
            throw new IOException($"Kiler cannot read the id of the file: {new Win32Exception(error).Message}", HResult(error));
        }

        return new FileId(information.VolumeSerialNumber, ((ulong)information.FileIndexHigh << 32) | information.FileIndexLow);
    }

    /// <summary>Makes a new file as itself, with the access, sharing and flags given; as <see cref="CreateNewFile(string)"/> says.</summary>
    private static SafeFileHandle? CreateNewFile(string path, uint access, FileShare share, uint flags)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw NotWindows();
        }

        var handle = CreateFile(Extended(path), access, (uint)share, 0, CreateNew, flags, 0);
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

    /// <summary>
    /// The NT path of a path, as CreateFileW makes it: <c>\??\C:\…</c>, or <c>\??\UNC\server\share\…</c> for a network path.
    /// A path with the <c>\\?\</c> or <c>\\.\</c> prefix is taken as it is; any other is made full first.
    /// </summary>
    private static string NtPath(string path)
    {
        if (path.StartsWith(ExtendedPrefix, StringComparison.Ordinal) || path.StartsWith(DevicePrefix, StringComparison.Ordinal))
        {
            return NtPrefix + path[ExtendedPrefix.Length..];
        }

        var full = Path.GetFullPath(path);
        return full.StartsWith(UncPrefix, StringComparison.Ordinal) ? NtUncPrefix + full[UncPrefix.Length..] : NtPrefix + full;
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

    [SupportedOSPlatform("windows")]
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetFileInformationByHandle(SafeFileHandle file, int informationClass, ref byte information, uint size);

    [SupportedOSPlatform("windows")]
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetFileInformationByHandle(SafeFileHandle file, int informationClass, ref uint information, uint size);

    [SupportedOSPlatform("windows")]
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation information);

    [SupportedOSPlatform("windows")]
    [LibraryImport("kernel32.dll", EntryPoint = "MoveFileExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool MoveFileEx(string existingFileName, string newFileName, uint flags);

    [SupportedOSPlatform("windows")]
    [LibraryImport("ntdll.dll")]
    private static unsafe partial int NtCreateFile(
        out SafeFileHandle fileHandle,
        uint desiredAccess,
        ObjectAttributes* objectAttributes,
        IoStatusBlock* ioStatusBlock,
        long* allocationSize,
        uint fileAttributes,
        uint shareAccess,
        uint createDisposition,
        uint createOptions,
        void* eaBuffer,
        uint eaLength);

    [SupportedOSPlatform("windows")]
    [LibraryImport("ntdll.dll")]
    private static partial uint RtlNtStatusToDosError(int status);

    /// <summary><c>UNICODE_STRING</c>: the lengths in bytes, without a null at the end.</summary>
    private unsafe struct UnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public char* Buffer;
    }

    /// <summary><c>OBJECT_ATTRIBUTES</c>.</summary>
    private unsafe struct ObjectAttributes
    {
        public uint Length;
        public nint RootDirectory;
        public UnicodeString* ObjectName;
        public uint Attributes;
        public void* SecurityDescriptor;
        public void* SecurityQualityOfService;
    }

    /// <summary><c>IO_STATUS_BLOCK</c>.</summary>
    private struct IoStatusBlock
    {
        public nint Status;
        public nuint Information;
    }

    /// <summary><c>BY_HANDLE_FILE_INFORMATION</c>: each <c>FILETIME</c> is two DWORDs, so nothing is padded.</summary>
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public uint CreationTimeLow;
        public uint CreationTimeHigh;
        public uint LastAccessTimeLow;
        public uint LastAccessTimeHigh;
        public uint LastWriteTimeLow;
        public uint LastWriteTimeHigh;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    /// <summary><c>SECURITY_QUALITY_OF_SERVICE</c>: zeros are <c>SecurityAnonymous</c> and static tracking.</summary>
    private struct SecurityQualityOfService
    {
        public uint Length;
        public int ImpersonationLevel;
        public byte ContextTrackingMode;
        public byte EffectiveOnly;
    }
}

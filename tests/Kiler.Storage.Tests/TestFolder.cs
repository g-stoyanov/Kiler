using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Kiler.Storage.Tests;

/// <summary>
/// A new empty folder for one test, deleted with everything in it afterwards. Every test that touches the file system takes
/// its folder from here, so the tests can run on another disk: <see cref="Variable"/> names a folder that exists, such as a
/// temporary subfolder of the real shared folder (M2.11), and the test folders are made right in it. Without it they go to
/// <c>Kiler.Tests</c> in the temporary folder.
/// </summary>
internal sealed class TestFolder : IDisposable
{
    public const string Variable = "KILER_TEST_FOLDER";

    private const FileAccess GenericWrite = (FileAccess)0x40000000;
    private const FileAccess ReadAttributes = (FileAccess)0x80;
    private const uint SupportsPosixUnlinkRename = 0x400;
    private const int MaxPath = 260;
    private const FileAttributes BackupSemantics = (FileAttributes)0x02000000;
    private const FileAttributes OpenReparsePoint = (FileAttributes)0x00200000;
    private const uint MountPointTag = 0xA0000003;
    private const uint SetReparsePoint = 0x000900A4;
    private const string NtPrefix = @"\??\";
    private const string NtUncPrefix = @"\??\UNC\";

    public TestFolder()
    {
        Root = Path.Combine(Base(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    /// <summary>Deletes the folder. A junction in it goes as a link, without what it points to.</summary>
    public void Dispose()
    {
        // Directory.Delete with recursive unmounts a junction through DeleteVolumeMountPoint, which an ordinary user may not
        // call, so it fails. The links go first, each as itself.
        DeleteLinks(Root);
        Directory.Delete(Root, recursive: true);
    }

    /// <summary>
    /// A junction, which needs no special rights, unlike a symbolic link. Windows makes one only on a local disk, so a test
    /// that needs one is skipped elsewhere.
    /// </summary>
    public static void CreateJunction(string link, string target)
    {
        SkipWithoutJunctions(link);
        var start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        foreach (var argument in new[] { "/c", "mklink", "/J", link, target })
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
        Assert.True(File.GetAttributes(link).HasFlag(FileAttributes.ReparsePoint), "The junction is not a link.");
    }

    /// <summary>
    /// Makes an empty folder a mount point to another folder in place, as anyone who can write the folder can do while Kiler
    /// holds it (measured in M2.3). Windows makes one only on a local disk, so the test is skipped elsewhere.
    /// </summary>
    public static void MakeMountPoint(string folder, string target)
    {
        var error = TryMakeMountPoint(folder, target);
        Assert.True(error == 0, $"Windows made no mount point: error {error}");
    }

    /// <summary>
    /// Tries to make a folder a mount point in place, as <see cref="MakeMountPoint"/> does, and gives the error of Windows, or
    /// 0 when it is made: a folder that is not empty gives ERROR_DIR_NOT_EMPTY, 145 (measured in M2.5).
    /// </summary>
    public static int TryMakeMountPoint(string folder, string target)
    {
        SkipWithoutJunctions(folder);
        return TrySetMountPoint(folder, target);
    }

    /// <summary>
    /// Tries to make a folder a mount point in place as <see cref="TryMakeMountPoint"/> does, also where Windows makes no
    /// junction, such as on a file server, and gives the error of Windows, or 0 when it is made (M2.11a).
    /// </summary>
    public static int TrySetMountPoint(string folder, string target)
    {
        using var handle = CreateFileW(folder, GenericWrite, FileShare.ReadWrite | FileShare.Delete, 0, FileMode.Open, BackupSemantics | OpenReparsePoint, 0);
        Assert.False(handle.IsInvalid, $"The folder could not be opened to write: error {Marshal.GetLastPInvokeError()}");

        // REPARSE_DATA_BUFFER of a mount point: the tag, the length of the data, then the offsets and lengths of the two
        // names in the path buffer, and the names, each with a null after it. A network path is \??\UNC\server\share\….
        var substitute = Encoding.Unicode.GetBytes(target.StartsWith(@"\\", StringComparison.Ordinal) ? NtUncPrefix + target[2..] : NtPrefix + target);
        var print = Encoding.Unicode.GetBytes(target);
        var data = new byte[8 + substitute.Length + 2 + print.Length + 2];
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(2), (ushort)substitute.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(4), (ushort)(substitute.Length + 2));
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(6), (ushort)print.Length);
        substitute.CopyTo(data, 8);
        print.CopyTo(data, 8 + substitute.Length + 2);
        var buffer = new byte[8 + data.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, MountPointTag);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(4), (ushort)data.Length);
        data.CopyTo(buffer, 8);

        return DeviceIoControl(handle, SetReparsePoint, buffer, buffer.Length, 0, 0, out _, 0) ? 0 : Marshal.GetLastPInvokeError();
    }

    /// <summary>
    /// A symbolic link to a file. Windows makes one for an ordinary user only in developer mode, and on a network folder
    /// only when the server allows it, so the test is skipped when it cannot.
    /// </summary>
    public static void CreateFileLink(string link, string target)
    {
        try
        {
            File.CreateSymbolicLink(link, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Assert.Skip($"Windows made no symbolic link here: {ex.Message}");
        }

        Assert.True(File.GetAttributes(link).HasFlag(FileAttributes.ReparsePoint), "The symbolic link is not a link.");
    }

    /// <summary>
    /// Denies the current user the right to set the attributes and times of a file, as a file server may for a file of
    /// another account that it still lets be moved (M2.5). The file can still be read, moved and deleted.
    /// </summary>
    public static void DenyWriteAttributes(string path)
    {
        // CA1416 does not take Assert.Skip for the end of the method, so the Windows calls stand inside the guard.
        if (OperatingSystem.IsWindows())
        {
            using var identity = WindowsIdentity.GetCurrent();
            var file = new FileInfo(path);
            var security = file.GetAccessControl();
            security.AddAccessRule(new FileSystemAccessRule(identity.User!, FileSystemRights.WriteAttributes, AccessControlType.Deny));
            SetAccessControl(file, security);
        }
        else
        {
            Assert.Skip("The rights of a file are set through Windows.");
        }
    }

    /// <summary>
    /// Denies the current user the right to read the data of a file, as an ACL on the share can (M2.7): an open to read is
    /// refused, while the file is still listed and can be deleted. <see cref="AllowRead"/> takes the rule away.
    /// </summary>
    public static void DenyRead(string path) => SetReadDenied(path, denied: true);

    /// <summary>Takes away the rule that <see cref="DenyRead"/> put on a file.</summary>
    public static void AllowRead(string path) => SetReadDenied(path, denied: false);

    /// <summary>A FILETIME in the year 30828, which Windows takes and a <see cref="DateTime"/> cannot hold.</summary>
    public const long FarFuture = 0x7FFF_0000_0000_0000;

    /// <summary>
    /// Writes a file with the times that a write of Kiler leaves, its creation time equal to its write time (M2.3). A new file
    /// that <see cref="File.WriteAllBytes(string, byte[])"/> writes often gets a write time a moment after its creation time
    /// (29 of 50 on the developer machine, M2.7a), so it would look edited in place.
    /// </summary>
    public static void WriteAsKiler(string path, byte[] bytes)
    {
        File.WriteAllBytes(path, bytes);
        File.SetCreationTimeUtc(path, File.GetLastWriteTimeUtc(path));
    }

    /// <summary>
    /// Writes a file in place, as Notepad does, so it looks edited in place: its write time a minute after its creation time,
    /// which a file that is there keeps.
    /// </summary>
    public static void EditInPlace(string path, byte[] bytes)
    {
        File.WriteAllBytes(path, bytes);
        File.SetLastWriteTimeUtc(path, File.GetCreationTimeUtc(path).AddMinutes(1));
    }

    /// <summary>
    /// Sets the creation and write times of a file or a folder (M2.7e) as raw FILETIME values, also those after the year 9999
    /// that a <see cref="DateTime"/> cannot hold, as anyone on the share can set them through Windows (third security review of
    /// M2.7).
    /// </summary>
    public static void SetRawTimes(string path, long creation, long lastWrite)
    {
        using var handle = CreateFileW(path, GenericWrite | ReadAttributes, FileShare.ReadWrite | FileShare.Delete, 0, FileMode.Open, BackupSemantics, 0);
        Assert.False(handle.IsInvalid, $"The file could not be opened to write: error {Marshal.GetLastPInvokeError()}");
        var access = lastWrite;
        Assert.True(SetFileTime(handle, ref creation, ref access, ref lastWrite), $"Windows did not set the times: error {Marshal.GetLastPInvokeError()}");

        // The file server of the shared folder takes such a time and keeps another one, of the 20th century (M2.11). A local
        // disk keeps it, so it fails the test rather than skip it.
        var latest = Math.Max(creation, lastWrite);
        Assert.True(GetFileTime(handle, out var kept, out _, out var keptWrite), $"Windows did not give the times: error {Marshal.GetLastPInvokeError()}");
        if (latest > DateTime.MaxValue.ToFileTimeUtc() && Math.Max(kept, keptWrite) != latest)
        {
            Assert.False(IsOnLocalDisk(path), "The local disk did not keep a time after the year 9999.");
            Assert.Skip("The file server keeps no time after the year 9999.");
        }
    }

    /// <summary>
    /// Whether the file system of a folder takes the POSIX delete, which frees the name of a file at once, also while others
    /// read it (M2.4). The file server of the shared folder does not (M2.11): its delete keeps the name taken until the last
    /// handle to the file is closed.
    /// </summary>
    public static bool HasPosixDeletes(string folder) => (Volume(folder).Flags & SupportsPosixUnlinkRename) != 0;

    /// <summary>The name of the file system of the volume of a folder, such as NTFS, and the flags of the volume (M2.11a).</summary>
    public static (string Name, uint Flags) Volume(string folder)
    {
        using var handle = CreateFileW(folder, ReadAttributes, FileShare.ReadWrite | FileShare.Delete, 0, FileMode.Open, BackupSemantics, 0);
        Assert.False(handle.IsInvalid, $"The folder could not be opened: error {Marshal.GetLastPInvokeError()}");
        var name = new char[MaxPath + 1];
        Assert.True(
            GetVolumeInformationByHandleW(handle, 0, 0, 0, 0, out var flags, name, name.Length),
            $"Windows did not describe the volume: error {Marshal.GetLastPInvokeError()}");
        return (new string(name, 0, Array.IndexOf(name, '\0')), flags);
    }

    /// <summary>
    /// Opens a folder to list it, shared with everything, as another program that reads the folder may hold it while the
    /// layer deletes it (M2.11a).
    /// </summary>
    public static SafeFileHandle OpenFolderToList(string folder)
    {
        var handle = CreateFileW(folder, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 0, FileMode.Open, BackupSemantics, 0);
        Assert.False(handle.IsInvalid, $"The folder could not be opened: error {Marshal.GetLastPInvokeError()}");
        return handle;
    }

    /// <summary>The owner of a file or a folder as a SID, read by its path (M2.11a).</summary>
    [SupportedOSPlatform("windows")]
    public static string OwnerOf(string path)
    {
        FileSystemSecurity security = Directory.Exists(path)
            ? new DirectoryInfo(path).GetAccessControl(AccessControlSections.Owner)
            : new FileInfo(path).GetAccessControl(AccessControlSections.Owner);
        return security.GetOwner(typeof(SecurityIdentifier))!.Value;
    }

    /// <summary>
    /// The path of a test folder through the administrative share of its drive on this machine,
    /// <c>\\localhost\C$\…</c>, so a test goes through SMB and a UNC path. The test is skipped where the share cannot be
    /// reached, as for a user who is not an administrator.
    /// </summary>
    public static string ThroughLoopbackShare(string path)
    {
        var root = Path.GetPathRoot(path);
        if (root is null || root.Length != 3 || root[1] != ':')
        {
            Assert.Skip("The folder is not on a drive of this machine.");
        }

        var unc = $@"\\localhost\{root[0]}$\{path[root.Length..]}";
        if (!Directory.Exists(unc))
        {
            Assert.Skip("The administrative share of the drive cannot be reached.");
        }

        return unc;
    }

    /// <summary>A symbolic link to a folder, skipped as <see cref="CreateFileLink"/> is.</summary>
    public static void CreateFolderLink(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Assert.Skip($"Windows made no symbolic link here: {ex.Message}");
        }

        Assert.True(File.GetAttributes(link).HasFlag(FileAttributes.ReparsePoint), "The symbolic link is not a link.");
    }

    /// <summary>
    /// Skips the test when no junction can be made at the path. A test that makes one inside code under test calls it first:
    /// the skip there would come out as the wrong exception.
    /// </summary>
    public static void SkipWithoutJunctions(string path)
    {
        if (!IsOnLocalDisk(path))
        {
            Assert.Skip("A junction can be made only on a local disk.");
        }
    }

    private static string Base()
    {
        var folder = Environment.GetEnvironmentVariable(Variable);
        if (string.IsNullOrEmpty(folder))
        {
            return Path.Combine(Path.GetTempPath(), "Kiler.Tests");
        }

        // A mistyped folder must not be made somewhere by the tests.
        if (!Path.IsPathFullyQualified(folder) || !Directory.Exists(folder))
        {
            throw new InvalidOperationException($"{Variable} must name a folder that exists, with its full path: {folder}");
        }

        return folder;
    }

    private static void SetReadDenied(string path, bool denied)
    {
        // CA1416 does not take Assert.Skip for the end of the method, so the Windows calls stand inside the guard.
        if (OperatingSystem.IsWindows())
        {
            using var identity = WindowsIdentity.GetCurrent();
            var rule = new FileSystemAccessRule(identity.User!, FileSystemRights.ReadData, AccessControlType.Deny);
            var file = new FileInfo(path);
            var security = file.GetAccessControl();
            if (denied)
            {
                security.AddAccessRule(rule);
            }
            else
            {
                security.RemoveAccessRule(rule);
            }

            SetAccessControl(file, security);
        }
        else
        {
            Assert.Skip("The rights of a file are set through Windows.");
        }
    }

    // The file server of the shared folder lets no one change the rights of a file, its owner neither (M2.11). On a local
    // disk the owner can, so a refusal there fails the test.
    [SupportedOSPlatform("windows")]
    private static void SetAccessControl(FileInfo file, FileSecurity security)
    {
        try
        {
            file.SetAccessControl(security);
        }
        catch (UnauthorizedAccessException) when (!IsOnLocalDisk(file.FullName))
        {
            Assert.Skip("The file server lets no rights of a file be set here.");
        }
    }

    private static void DeleteLinks(string folder)
    {
        foreach (var subfolder in new DirectoryInfo(folder).EnumerateDirectories("*", new EnumerationOptions { AttributesToSkip = 0 }))
        {
            if (subfolder.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                subfolder.Delete();
            }
            else
            {
                DeleteLinks(subfolder.FullName);
            }
        }
    }

    /// <summary>Whether a path is on a fixed disk of this machine, not on a network folder.</summary>
    public static bool IsOnLocalDisk(string path) =>
        !path.StartsWith(@"\\", StringComparison.Ordinal) && new DriveInfo(Path.GetPathRoot(path)!).DriveType == DriveType.Fixed;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(
        string fileName, FileAccess access, FileShare share, nint securityAttributes, FileMode mode, FileAttributes flags, nint template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileTime(SafeFileHandle file, ref long creation, ref long lastAccess, ref long lastWrite);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileTime(SafeFileHandle file, out long creation, out long lastAccess, out long lastWrite);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeInformationByHandleW(
        SafeFileHandle file, nint volumeName, int volumeNameSize, nint serialNumber, nint maxComponentLength, out uint flags, [Out] char[] fileSystemName, int fileSystemNameSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandle device, uint code, byte[] input, int inputSize, nint output, int outputSize, out int returned, nint overlapped);
}

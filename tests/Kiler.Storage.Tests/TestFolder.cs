using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
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
    private const FileAttributes BackupSemantics = (FileAttributes)0x02000000;
    private const FileAttributes OpenReparsePoint = (FileAttributes)0x00200000;
    private const uint MountPointTag = 0xA0000003;
    private const uint SetReparsePoint = 0x000900A4;
    private const string NtPrefix = @"\??\";

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
        using var handle = CreateFileW(folder, GenericWrite, FileShare.ReadWrite | FileShare.Delete, 0, FileMode.Open, BackupSemantics | OpenReparsePoint, 0);
        Assert.False(handle.IsInvalid, $"The folder could not be opened to write: error {Marshal.GetLastPInvokeError()}");

        // REPARSE_DATA_BUFFER of a mount point: the tag, the length of the data, then the offsets and lengths of the two
        // names in the path buffer, and the names, each with a null after it.
        var substitute = Encoding.Unicode.GetBytes(NtPrefix + target);
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
            file.SetAccessControl(security);
        }
        else
        {
            Assert.Skip("The rights of a file are set through Windows.");
        }
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

    private static bool IsOnLocalDisk(string path) =>
        !path.StartsWith(@"\\", StringComparison.Ordinal) && new DriveInfo(Path.GetPathRoot(path)!).DriveType == DriveType.Fixed;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(
        string fileName, FileAccess access, FileShare share, nint securityAttributes, FileMode mode, FileAttributes flags, nint template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandle device, uint code, byte[] input, int inputSize, nint output, int outputSize, out int returned, nint overlapped);
}

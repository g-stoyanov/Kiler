using System.Diagnostics;

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
}

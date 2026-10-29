namespace Kiler.App.Tests;

/// <summary>
/// A new empty folder under the temporary folder, deleted with everything in it after the test. <see cref="OnTheTestDisk"/>
/// makes it in the folder that <see cref="Variable"/> names instead, as the file tests of Kiler.Storage do, so a test of the
/// owners of Windows can run on a temporary subfolder of the real shared folder (M2.11).
/// </summary>
internal sealed class TempFolder : IDisposable
{
    public const string Variable = "KILER_TEST_FOLDER";

    public TempFolder()
        : this(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Kiler.Tests"))
    {
    }

    private TempFolder(string parent)
    {
        Path = System.IO.Path.Combine(parent, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    /// <summary>A folder in the one that <see cref="Variable"/> names, or under the temporary folder without it.</summary>
    public static TempFolder OnTheTestDisk()
    {
        var folder = Environment.GetEnvironmentVariable(Variable);
        if (string.IsNullOrEmpty(folder))
        {
            return new TempFolder();
        }

        // A mistyped folder must not be made somewhere by the tests.
        if (!System.IO.Path.IsPathFullyQualified(folder) || !Directory.Exists(folder))
        {
            throw new InvalidOperationException($"{Variable} must name a folder that exists, with its full path: {folder}");
        }

        return new TempFolder(folder);
    }

    /// <summary>
    /// Whether <see cref="Variable"/> names a folder on a file server, not on a fixed disk of this machine (M2.11c). A value
    /// that is not a full path counts too, so <see cref="OnTheTestDisk"/> refuses it with its message.
    /// </summary>
    public static bool OnAFileServer =>
        Environment.GetEnvironmentVariable(Variable) is { Length: > 0 } folder
        && !(System.IO.Path.IsPathFullyQualified(folder)
            && !folder.StartsWith(@"\\", StringComparison.Ordinal)
            && new DriveInfo(System.IO.Path.GetPathRoot(folder)!).DriveType == DriveType.Fixed);

    public void Dispose() => Directory.Delete(Path, recursive: true);
}

using System.Diagnostics;

namespace Kiler.App.Tests;

public sealed class PinnedFolderTests : IDisposable
{
    private readonly TempFolder _folder = new();
    private readonly string _pinned;

    public PinnedFolderTests() => _pinned = Directory.CreateDirectory(Path.Combine(_folder.Path, "above", "pinned")).FullName;

    public void Dispose() => _folder.Dispose();

    [Fact]
    public void Open_NoOneCanRenameOrDeleteTheFolderOrAFolderAboveIt_UntilItIsLetGo()
    {
        var above = Path.GetDirectoryName(_pinned)!;

        using (PinnedFolder.Open(_pinned))
        {
            AssertRefused(() => Directory.Move(_pinned, _pinned + "-moved"));
            AssertRefused(() => Directory.Move(above, above + "-moved"));
            AssertRefused(() => Directory.Delete(_pinned));
        }

        Directory.Move(above, above + "-moved");
    }

    [Fact]
    public void Open_FilesInTheFolderCanStillBeMadeListedRenamedReadAndDeleted()
    {
        var file = Path.Combine(_pinned, "r0000.json");

        using (PinnedFolder.Open(_pinned))
        {
            File.WriteAllText(file + ".tmp", "{}");
            File.Move(file + ".tmp", file);
            Assert.Equal([file], Directory.GetFiles(_pinned));
            Assert.Equal("{}", File.ReadAllText(file));
            File.Delete(file);
        }

        Assert.Empty(Directory.GetFileSystemEntries(_pinned));
    }

    [Fact]
    public void Open_TwiceTheSameFolder_Works()
    {
        // The run lists the folder while it holds it, and Windows opens it for that.
        using var first = PinnedFolder.Open(_pinned);
        using var second = PinnedFolder.Open(_pinned);
    }

    [Fact]
    public void Open_ALinkToAFolder_IsRefused()
    {
        var link = Path.Combine(_folder.Path, "link");
        CreateJunction(link, _pinned);
        try
        {
            var refused = Assert.Throws<IOException>(() => PinnedFolder.Open(link));

            Assert.Equal($"Not a folder but a link or a file: {link}", refused.Message);
            // Not held: the link can go.
            Directory.Delete(link);
        }
        finally
        {
            if (Directory.Exists(link))
            {
                Directory.Delete(link);
            }
        }
    }

    [Fact]
    public void Open_AFile_IsRefused()
    {
        var file = Path.Combine(_pinned, "file.json");
        File.WriteAllText(file, "{}");

        Assert.Throws<IOException>(() => PinnedFolder.Open(file));
        File.Delete(file);
    }

    [Fact]
    public void Open_AFolderThatIsNotThere_IsRefused()
    {
        var refused = Assert.Throws<IOException>(() => PinnedFolder.Open(Path.Combine(_folder.Path, "missing")));

        Assert.StartsWith("Kiler cannot hold the folder open: ", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>A junction, which needs no special rights, unlike a symbolic link.</summary>
    private static void CreateJunction(string link, string target)
    {
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

    private static void AssertRefused(Action action) =>
        Assert.True(Record.Exception(action) is IOException or UnauthorizedAccessException, "Windows allowed it while the folder was held.");
}

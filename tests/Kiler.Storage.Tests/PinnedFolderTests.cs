namespace Kiler.Storage.Tests;

public sealed class PinnedFolderTests : IDisposable
{
    private readonly TestFolder _test = new();
    private readonly string _pinned;

    public PinnedFolderTests() => _pinned = Directory.CreateDirectory(Path.Combine(_test.Root, "above", "pinned")).FullName;

    public void Dispose() => _test.Dispose();

    /// <summary>
    /// A local NTFS disk also refuses to rename a folder above a held folder. The file server of the shared folder lets it (M2.11), so
    /// there only the folders that a write holds itself stay where they are: a folder above the data folder can be renamed.
    /// </summary>
    [Fact]
    public void Open_NoOneCanRenameOrDeleteTheFolderOrAFolderAboveIt_UntilItIsLetGo()
    {
        var above = Path.GetDirectoryName(_pinned)!;
        var movedAbove = false;

        using (PinnedFolder.Open(_pinned))
        {
            AssertRefused(() => Directory.Move(_pinned, _pinned + "-moved"));
            AssertRefused(() => Directory.Delete(_pinned));
            if (TestFolder.IsOnLocalDisk(above))
            {
                AssertRefused(() => Directory.Move(above, above + "-moved"));
            }
            else
            {
                movedAbove = Record.Exception(() => Directory.Move(above, above + "-moved")) is null;
            }
        }

        if (movedAbove)
        {
            Directory.Move(above + "-moved", above);
            Assert.Skip("The file server lets a folder above a held folder be renamed.");
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
        // Two writes, or a write and a listing, hold the same folder at once.
        using var first = PinnedFolder.Open(_pinned);
        using var second = PinnedFolder.Open(_pinned);
    }

    [Fact]
    public void Open_ALinkToAFolder_IsRefused()
    {
        var link = Path.Combine(_test.Root, "link");
        TestFolder.CreateJunction(link, _pinned);

        var refused = Assert.Throws<IOException>(() => PinnedFolder.Open(link));

        Assert.Equal($"Not a folder but a link or a file: {link}", refused.Message);
        // Not held: the link can go.
        Directory.Delete(link);
    }

    [Fact]
    public void ThrowIfLink_AHeldEmptyFolderMadeAMountPointInPlace_IsALink()
    {
        var elsewhere = Directory.CreateDirectory(Path.Combine(_test.Root, "elsewhere")).FullName;
        using var held = PinnedFolder.Open(_pinned);
        held.ThrowIfLink();

        // The hold shares writes, which a rename into the folder needs, so it does not stop this.
        TestFolder.MakeMountPoint(_pinned, elsewhere);

        var refused = Assert.Throws<IOException>(held.ThrowIfLink);
        Assert.Equal($"The folder has become a link, which Kiler does not follow: {_pinned}", refused.Message);
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
        Assert.Throws<DirectoryNotFoundException>(() => PinnedFolder.Open(Path.Combine(_test.Root, "missing")));
        Assert.Throws<DirectoryNotFoundException>(() => PinnedFolder.Open(Path.Combine(_test.Root, "missing", "records")));
    }

    private static void AssertRefused(Action action) =>
        Assert.True(Record.Exception(action) is IOException or UnauthorizedAccessException, "Windows allowed it while the folder was held.");
}

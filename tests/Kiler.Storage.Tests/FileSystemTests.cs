namespace Kiler.Storage.Tests;

public sealed class FileSystemTests : IDisposable
{
    private readonly TestFolder _test = new();

    public void Dispose() => _test.Dispose();

    [Fact]
    public void CreateFolder_WhenTheFolderAboveIsMissing_IsRefusedAndMakesNothing()
    {
        var above = Path.Combine(_test.Root, "data");

        Assert.Throws<DirectoryNotFoundException>(() => FileSystem.Instance.CreateFolder(Path.Combine(above, "private")));

        Assert.False(Directory.Exists(above));
    }

    [Fact]
    public void CreateFolder_AFolderThatExists_IsLeftAsItIs()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_test.Root, "records")).FullName;
        File.WriteAllText(Path.Combine(folder, "8f3a1c.json"), "{}");

        FileSystem.Instance.CreateFolder(folder);

        Assert.True(File.Exists(Path.Combine(folder, "8f3a1c.json")));
    }

    [Fact]
    public void CreateFolder_WhereAJunctionIs_IsRefused()
    {
        var link = Path.Combine(_test.Root, "records");
        TestFolder.CreateJunction(link, Directory.CreateDirectory(Path.Combine(_test.Root, "elsewhere")).FullName);

        Assert.Throws<IOException>(() => FileSystem.Instance.CreateFolder(link));
    }

    [Fact]
    public void CreateFolder_WhereAFileIs_IsRefused()
    {
        var file = Path.Combine(_test.Root, "records");
        File.WriteAllText(file, "made up");

        Assert.Throws<IOException>(() => FileSystem.Instance.CreateFolder(file));
    }

    [Fact]
    public void MoveFolder_OntoAFolderThatExists_IsRefusedAndReplacesNothing()
    {
        var from = Directory.CreateDirectory(Path.Combine(_test.Root, "demo.user.1a2b3c4d.tmp")).FullName;
        var to = Directory.CreateDirectory(Path.Combine(_test.Root, "demo.user")).FullName;
        File.WriteAllText(Path.Combine(to, "key.json"), "{}");

        Assert.Throws<IOException>(() => FileSystem.Instance.MoveFolder(from, to));

        Assert.True(Directory.Exists(from));
        Assert.Equal([Path.Combine(to, "key.json")], Directory.GetFileSystemEntries(to));
    }

    [Fact]
    public void Attributes_OfAPathThatIsNotThere_AreNull()
    {
        Assert.Null(FileSystem.Instance.Attributes(Path.Combine(_test.Root, "missing")));
        Assert.Null(FileSystem.Instance.Attributes(Path.Combine(_test.Root, "missing", "records")));
    }

    [Fact]
    public void Attributes_OfAJunction_AreThoseOfTheLink()
    {
        var link = Path.Combine(_test.Root, "link");
        TestFolder.CreateJunction(link, Directory.CreateDirectory(Path.Combine(_test.Root, "elsewhere")).FullName);

        Assert.True(FileSystem.Instance.Attributes(link)?.HasFlag(FileAttributes.ReparsePoint));
    }
}

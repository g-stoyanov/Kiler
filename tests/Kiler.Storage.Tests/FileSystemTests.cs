namespace Kiler.Storage.Tests;

public sealed class FileSystemTests : IDisposable
{
    private readonly TestFolder _test = new();

    public void Dispose() => _test.Dispose();

    [Fact]
    public void OpenRead_AMissingFile_GivesNothing()
    {
        Assert.Null(FileSystem.Instance.OpenRead(Path.Combine(_test.Root, "8f3a1c.json")));
        Assert.Null(FileSystem.Instance.OpenRead(Path.Combine(_test.Root, "records", "8f3a1c.json")));
    }

    [Fact]
    public void OpenRead_AFolder_Throws()
    {
        var path = Path.Combine(_test.Root, "8f3a1c.json");
        Directory.CreateDirectory(path);

        Assert.Throws<UnauthorizedAccessException>(() => FileSystem.Instance.OpenRead(path));
    }

    [Fact]
    public void OpenRead_AFileOpenToRead_CanStillBeWrittenAndDeleted()
    {
        var path = Path.Combine(_test.Root, "8f3a1c.json");
        File.WriteAllText(path, "{}");

        using var stream = FileSystem.Instance.OpenRead(path);

        Assert.NotNull(stream);
        File.WriteAllText(path, "[]");
        File.Delete(path);
    }

    [Fact]
    public void OpenRead_ASymbolicLink_IsRefusedThroughItsHandle()
    {
        var target = Path.Combine(_test.Root, "target.json");
        var link = Path.Combine(_test.Root, "8f3a1c.json");
        File.WriteAllText(target, "{}");
        TestFolder.CreateFileLink(link, target);

        var refused = Assert.Throws<IOException>(() => FileSystem.Instance.OpenRead(link));

        Assert.Equal($"The file is a link, which Kiler does not follow: {link}", refused.Message);
    }

    [Fact]
    public void CreateFile_WritesTheBytesAndHoldsTheFile_SoOthersCanReadButNotWriteIt()
    {
        var path = Path.Combine(_test.Root, "8f3a1c.1a2b3c4d.tmp");

        using (var file = FileSystem.Instance.CreateFile(path, "{}\n"u8))
        {
            Assert.NotNull(file);
            Assert.Equal(path, file.Path);
            using (var reader = FileSystem.Instance.OpenRead(path))
            {
                Assert.Equal(3, reader?.Length);
            }

            Assert.Throws<IOException>(() => new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete));
        }

        Assert.Equal("{}\n"u8.ToArray(), File.ReadAllBytes(path));
    }

    [Fact]
    public void CreateFile_WhereALinkWithoutTargetIs_GivesNothingAndMakesNoTarget()
    {
        var target = Path.Combine(_test.Root, "outside.json");
        var link = Path.Combine(_test.Root, "v6.json");
        TestFolder.CreateFileLink(link, target);

        Assert.Null(FileSystem.Instance.CreateFile(link, "new"u8));

        Assert.False(File.Exists(target));
    }

    [Fact]
    public void MoveFile_WithoutReplace_OntoALinkWithoutTarget_GivesFalseAndMakesNoTarget()
    {
        var target = Path.Combine(_test.Root, "outside.json");
        var link = Path.Combine(_test.Root, "v6.json");
        var from = Path.Combine(_test.Root, "v6.1a2b3c4d.tmp");
        File.WriteAllText(from, "new");
        TestFolder.CreateFileLink(link, target);

        Assert.False(FileSystem.Instance.MoveFile(from, link, replace: false));

        Assert.False(File.Exists(target));
        Assert.True(File.Exists(from));
    }

    [Fact]
    public void CreateFile_WhileItIsHeld_CanBeRenamedAndDeleted()
    {
        var path = Path.Combine(_test.Root, "8f3a1c.1a2b3c4d.tmp");
        var other = Path.Combine(_test.Root, "b72e09.1a2b3c4d.tmp");

        using var file = FileSystem.Instance.CreateFile(path, "{}"u8);
        using var second = FileSystem.Instance.CreateFile(other, "{}"u8);

        Assert.True(FileSystem.Instance.MoveFile(path, Path.Combine(_test.Root, "8f3a1c.json"), replace: false));
        FileSystem.Instance.DeleteFile(other);
        Assert.False(File.Exists(other));
    }

    [Fact]
    public void CreateFile_ANameThatIsTaken_GivesNothingAndLeavesTheFile()
    {
        var path = Path.Combine(_test.Root, "v6.json");
        File.WriteAllText(path, "old");

        Assert.Null(FileSystem.Instance.CreateFile(path, "new"u8));

        Assert.Equal("old", File.ReadAllText(path));
    }

    [Fact]
    public void CreateFile_WhereASymbolicLinkIs_GivesNothingAndDoesNotFollowIt()
    {
        var target = Path.Combine(_test.Root, "target.json");
        var link = Path.Combine(_test.Root, "v6.json");
        File.WriteAllText(target, "old");
        TestFolder.CreateFileLink(link, target);

        Assert.Null(FileSystem.Instance.CreateFile(link, "new"u8));

        Assert.Equal("old", File.ReadAllText(target));
    }

    [Fact]
    public void SetCreationToWriteTime_GivesTheFileTheCreationTimeOfItsWriteTime()
    {
        var path = Path.Combine(_test.Root, "8f3a1c.json");
        var written = new DateTime(2026, 10, 26, 8, 0, 0, DateTimeKind.Utc);
        FileStamp stamp;
        using (var file = FileSystem.Instance.CreateFile(path, "{}\n"u8)!)
        {
            File.SetCreationTimeUtc(file.Handle, written.AddDays(-30));
            File.SetLastWriteTimeUtc(file.Handle, written);

            stamp = FileSystem.Instance.SetCreationToWriteTime(file);
        }

        Assert.Equal(new FileStamp(3, written, written), stamp);
        Assert.Equal(written, File.GetCreationTimeUtc(path));
        Assert.Equal(written, File.GetLastWriteTimeUtc(path));
    }

    [Fact]
    public void MoveFile_WithoutReplace_OntoATakenName_GivesFalseAndMovesNothing()
    {
        var from = Path.Combine(_test.Root, "8f3a1c.1a2b3c4d.tmp");
        var to = Path.Combine(_test.Root, "8f3a1c.json");
        File.WriteAllText(from, "new");
        File.WriteAllText(to, "old");

        Assert.False(FileSystem.Instance.MoveFile(from, to, replace: false));

        Assert.Equal("new", File.ReadAllText(from));
        Assert.Equal("old", File.ReadAllText(to));
    }

    [Fact]
    public void MoveFile_WithReplace_ReplacesTheFile()
    {
        var from = Path.Combine(_test.Root, "8f3a1c.1a2b3c4d.tmp");
        var to = Path.Combine(_test.Root, "8f3a1c.json");
        File.WriteAllText(from, "new");
        File.WriteAllText(to, "old");

        Assert.True(FileSystem.Instance.MoveFile(from, to, replace: true));

        Assert.False(File.Exists(from));
        Assert.Equal("new", File.ReadAllText(to));
    }

    [Fact]
    public void MoveFile_WithReplace_OverAFileThatIsOpenToRead_IsRefused()
    {
        var from = Path.Combine(_test.Root, "8f3a1c.1a2b3c4d.tmp");
        var to = Path.Combine(_test.Root, "8f3a1c.json");
        File.WriteAllText(from, "new");
        File.WriteAllText(to, "old");

        using (FileSystem.Instance.OpenRead(to))
        {
            Assert.Throws<UnauthorizedAccessException>(() => FileSystem.Instance.MoveFile(from, to, replace: true));
        }

        Assert.Equal("old", File.ReadAllText(to));
    }

    [Fact]
    public void DeleteFile_AFileThatIsNotThere_DoesNothing() => FileSystem.Instance.DeleteFile(Path.Combine(_test.Root, "8f3a1c.json"));

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

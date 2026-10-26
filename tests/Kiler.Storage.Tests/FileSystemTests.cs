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
    public void HoldOrCreateFolder_AMissingFolder_IsMadeAndHeld()
    {
        var folder = Path.Combine(_test.Root, "records");

        using (FileSystem.Instance.HoldOrCreateFolder(folder))
        {
            Assert.True(Directory.Exists(folder));
            Assert.Throws<IOException>(() => Directory.Move(folder, folder + "-moved"));
        }

        Directory.Move(folder, folder + "-moved");
    }

    [Fact]
    public void HoldOrCreateFolder_AFolderThatExists_IsHeldAsItIs()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_test.Root, "records")).FullName;
        File.WriteAllText(Path.Combine(folder, "8f3a1c.json"), "{}");

        using (FileSystem.Instance.HoldOrCreateFolder(folder))
        {
            Assert.Throws<IOException>(() => Directory.Move(folder, folder + "-moved"));
        }

        Assert.True(File.Exists(Path.Combine(folder, "8f3a1c.json")));
    }

    [Fact]
    public void HoldOrCreateFolder_WhenTheFolderAboveIsMissing_IsRefusedAndMakesNothing()
    {
        var above = Path.Combine(_test.Root, "history");

        Assert.Throws<DirectoryNotFoundException>(() => FileSystem.Instance.HoldOrCreateFolder(Path.Combine(above, "records")));

        Assert.False(Directory.Exists(above));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HoldOrCreateFolder_WhereAJunctionOrAFileIs_IsRefused(bool junction)
    {
        var path = Path.Combine(_test.Root, "records");
        var elsewhere = Directory.CreateDirectory(Path.Combine(_test.Root, "elsewhere")).FullName;
        if (junction)
        {
            TestFolder.CreateJunction(path, elsewhere);
        }
        else
        {
            File.WriteAllText(path, "made up");
        }

        Assert.Throws<IOException>(() => FileSystem.Instance.HoldOrCreateFolder(path));

        Assert.Empty(Directory.GetFileSystemEntries(elsewhere));
    }

    /// <summary>
    /// A symbolic link without a target, to a file or to a folder, is opened as itself and refused, and its target is not
    /// made (M2.5). <c>Directory.CreateDirectory</c> did not follow one on the developer machine either; the CI machine, where
    /// the tests run as an administrator, followed such a link with a create-new of a file (M2.3).
    /// </summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void HoldOrCreateFolderAndCreateFolder_WhereASymbolicLinkWithoutTargetIs_AreRefusedAndMakeNoTarget(bool hold, bool linkToAFolder)
    {
        var link = Path.Combine(_test.Root, "records");
        var target = Path.Combine(_test.Root, "outside");
        if (linkToAFolder)
        {
            TestFolder.CreateFolderLink(link, target);
        }
        else
        {
            TestFolder.CreateFileLink(link, target);
        }

        Assert.Throws<IOException>(() =>
        {
            if (hold)
            {
                FileSystem.Instance.HoldOrCreateFolder(link).Dispose();
            }
            else
            {
                FileSystem.Instance.CreateFolder(link);
            }
        });

        Assert.False(Path.Exists(target), "The target of the link was made.");
        Assert.True(File.GetAttributes(link).HasFlag(FileAttributes.ReparsePoint), "The link is gone.");
    }

    [Fact]
    public void HoldOrCreateFolder_ALongPath_Works()
    {
        var folder = _test.Root;
        while (folder.Length < 300)
        {
            folder = Path.Combine(folder, new string('d', 40));
            FileSystem.Instance.HoldOrCreateFolder(folder).Dispose();
        }

        Assert.True(Directory.Exists(folder));
    }

    [Fact]
    public void Stamp_OfAFileOpenToRead_IsItsSizeAndTimesThroughItsHandle()
    {
        var path = Path.Combine(_test.Root, "8f3a1c.json");
        File.WriteAllText(path, "{}\n");
        var created = new DateTime(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc);
        var written = new DateTime(2026, 10, 26, 8, 0, 0, DateTimeKind.Utc);
        File.SetCreationTimeUtc(path, created);
        File.SetLastWriteTimeUtc(path, written);

        using var stream = FileSystem.Instance.OpenRead(path)!;

        Assert.Equal(new FileStamp(3, created, written), FileSystem.Instance.Stamp(stream));
    }

    /// <summary>
    /// The handle has only attribute access, which counts in no sharing check: others open the file without sharing it, and
    /// move it, while it is open. It follows the file, and puts back its creation time there, as a move into a name that
    /// another file left in the last 15 s takes that file's creation time (file tunneling, M2.3).
    /// </summary>
    [Fact]
    public void OpenTimes_StopsNoOneFromOpeningOrMovingTheFile_AndKeepCreationTimePutsItBackWhereTheFileWent()
    {
        var path = Path.Combine(_test.Root, "8f3a1c.json");
        var moved = Path.Combine(Directory.CreateDirectory(Path.Combine(_test.Root, "trash")).FullName, "8f3a1c.json");
        File.WriteAllText(path, "{}\n");
        var created = new DateTime(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc);
        var written = new DateTime(2026, 10, 26, 8, 0, 0, DateTimeKind.Utc);
        File.SetCreationTimeUtc(path, created);
        File.SetLastWriteTimeUtc(path, written);

        using (var times = FileSystem.Instance.OpenTimes(path)!)
        {
            new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None).Dispose();
            File.Move(path, moved);
            File.SetCreationTimeUtc(moved, created.AddYears(-6));

            FileSystem.Instance.KeepCreationTime(times);

            Assert.Equal(new FileStamp(3, created, written), times.Stamp);
        }

        Assert.Equal(created, File.GetCreationTimeUtc(moved));
        Assert.Equal(written, File.GetLastWriteTimeUtc(moved));
    }

    /// <summary>
    /// A rename over a file that is open fails, with or without delete sharing (M2.2), and a handle with only attribute access
    /// counts too (measured in M2.5), so the layer holds one only for a move.
    /// </summary>
    [Fact]
    public void OpenTimes_StopsARenameOverTheFile_AsAnyOpenHandleDoes()
    {
        var path = Path.Combine(_test.Root, "8f3a1c.json");
        var from = Path.Combine(_test.Root, "8f3a1c.1a2b3c4d.tmp");
        File.WriteAllText(path, "old");
        File.WriteAllText(from, "new");

        using (FileSystem.Instance.OpenTimes(path))
        {
            Assert.Throws<UnauthorizedAccessException>(() => FileSystem.Instance.MoveFile(from, path, replace: true));
        }

        Assert.True(FileSystem.Instance.MoveFile(from, path, replace: true));
        Assert.Equal("new", File.ReadAllText(path));
    }

    /// <summary>
    /// A file server may let anyone move a file in a folder they can write, but set its times only for its owner (code review
    /// of M2.5). The times are then only read, and the move goes on.
    /// </summary>
    [Fact]
    public void OpenTimes_WhenTheTimesMayNotBeSet_ReadsThem_AndKeepCreationTimeLeavesThem()
    {
        var path = Path.Combine(_test.Root, "8f3a1c.json");
        var moved = Path.Combine(_test.Root, "8f3a1c.moved");
        File.WriteAllText(path, "{}\n");
        var created = new DateTime(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc);
        var written = new DateTime(2026, 10, 26, 8, 0, 0, DateTimeKind.Utc);
        File.SetCreationTimeUtc(path, created);
        File.SetLastWriteTimeUtc(path, written);
        TestFolder.DenyWriteAttributes(path);

        using (var times = FileSystem.Instance.OpenTimes(path)!)
        {
            Assert.False(times.CanSet);
            Assert.Equal(new FileStamp(3, created, written), times.Stamp);
            File.Move(path, moved);

            FileSystem.Instance.KeepCreationTime(times);
        }

        Assert.Equal(created, File.GetCreationTimeUtc(moved));
    }

    /// <summary>
    /// A move to a folder on another volume, as through a mount point, is refused: <c>File.Move</c> of .NET would copy the file
    /// and then delete it, and a crash in between would leave it in both places (security review of M2.5).
    /// </summary>
    [Fact]
    public void MoveFile_ToAnotherVolume_IsRefusedAndCopiesNothing()
    {
        var from = Path.Combine(_test.Root, "8f3a1c.json");
        File.WriteAllText(from, "{}");
        var other = Path.Combine(AppContext.BaseDirectory, "move-" + Guid.NewGuid().ToString("N"));
        if (string.Equals(Path.GetPathRoot(other), Path.GetPathRoot(from), StringComparison.OrdinalIgnoreCase))
        {
            Assert.Skip("The tests and their temporary folder are on one drive.");
        }

        Directory.CreateDirectory(other);
        try
        {
            var to = Path.Combine(other, "8f3a1c.json");

            var refused = Assert.Throws<IOException>(() => FileSystem.Instance.MoveFile(from, to, replace: true));

            Assert.Equal(unchecked((int)0x80070011), refused.HResult);
            Assert.False(File.Exists(to), "The file was copied to the other volume.");
            Assert.True(File.Exists(from));
        }
        finally
        {
            Directory.Delete(other, recursive: true);
        }
    }

    /// <summary>
    /// A network path becomes <c>\??\UNC\server\share\…</c> for NtCreateFile. Through the administrative share of this
    /// machine, which an administrator can reach, as on the CI machine.
    /// </summary>
    [Fact]
    public void HoldOrCreateFolder_ThroughANetworkPath_IsMadeAndHeld()
    {
        var shared = TestFolder.ThroughLoopbackShare(_test.Root);
        var folder = Path.Combine(shared, "records");

        using (FileSystem.Instance.HoldOrCreateFolder(folder))
        {
            Assert.True(Directory.Exists(Path.Combine(_test.Root, "records")));
            Assert.Throws<IOException>(() => Directory.Move(folder, folder + "-moved"));
        }

        Assert.Throws<DirectoryNotFoundException>(() => FileSystem.Instance.HoldOrCreateFolder(Path.Combine(shared, "missing", "records")));
    }

    [Fact]
    public void OpenTimes_OfAMissingFileOrASymbolicLink_GivesNothingOrIsRefused()
    {
        var target = Path.Combine(_test.Root, "target.json");
        var link = Path.Combine(_test.Root, "8f3a1c.json");
        File.WriteAllText(target, "{}");
        Assert.Null(FileSystem.Instance.OpenTimes(Path.Combine(_test.Root, "b72e09.json")));
        TestFolder.CreateFileLink(link, target);

        Assert.Throws<IOException>(() => FileSystem.Instance.OpenTimes(link));
    }

    /// <summary>
    /// A marker keeps its folder from being made a mount point in place, as NTFS refuses one on a folder that is not empty;
    /// no one can delete or rename it, a file can still be moved into the folder, and the marker goes when it is let go
    /// (second security review of M2.5).
    /// </summary>
    [Fact]
    public void CreateMarker_KeepsItsFolderFromBeingMadeAMountPoint_AndGoesWhenLetGo()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_test.Root, "trash")).FullName;
        var elsewhere = Directory.CreateDirectory(Path.Combine(_test.Root, "elsewhere")).FullName;
        var path = Path.Combine(folder, "8f3a1c.1a2b3c4d.tmp");
        var document = Path.Combine(_test.Root, "8f3a1c.json");
        File.WriteAllText(document, "{}");

        using (var marker = FileSystem.Instance.CreateMarker(path))
        {
            Assert.NotNull(marker);
            Assert.Equal(145, TestFolder.TryMakeMountPoint(folder, elsewhere));
            Assert.Throws<IOException>(() => File.Delete(path));
            Assert.Throws<IOException>(() => File.Move(path, path + ".away"));
            Assert.True(FileSystem.Instance.MoveFile(document, Path.Combine(folder, "8f3a1c.json"), replace: false));
        }

        Assert.Equal(["8f3a1c.json"], Directory.GetFileSystemEntries(folder).Select(entry => Path.GetFileName(entry)));
        File.Delete(Path.Combine(folder, "8f3a1c.json"));
        Assert.Equal(0, TestFolder.TryMakeMountPoint(folder, elsewhere));
    }

    [Fact]
    public void CreateMarker_ANameThatIsTakenOrALink_GivesNothing()
    {
        var path = Path.Combine(_test.Root, "8f3a1c.1a2b3c4d.tmp");
        var link = Path.Combine(_test.Root, "b72e09.1a2b3c4d.tmp");
        var target = Path.Combine(_test.Root, "outside.json");
        File.WriteAllText(path, "made up");
        TestFolder.CreateFileLink(link, target);

        Assert.Null(FileSystem.Instance.CreateMarker(path));
        Assert.Null(FileSystem.Instance.CreateMarker(link));

        Assert.Equal("made up", File.ReadAllText(path));
        Assert.False(File.Exists(target));
    }

    /// <summary>The id of a file stays with it through a move, and another file has another one (measured in M2.5).</summary>
    [Fact]
    public void OpenTimes_GivesTheIdOfTheFile_WhichAMoveKeeps()
    {
        var path = Path.Combine(_test.Root, "8f3a1c.json");
        var moved = Path.Combine(Directory.CreateDirectory(Path.Combine(_test.Root, "trash")).FullName, "8f3a1c.json");
        File.WriteAllText(path, "{}");
        using var before = FileSystem.Instance.OpenTimes(path)!;
        File.Move(path, moved);
        File.WriteAllText(path, "{}");

        using var after = FileSystem.Instance.OpenTimes(moved)!;
        using var other = FileSystem.Instance.OpenTimes(path)!;

        Assert.Equal(before.Id, after.Id);
        Assert.NotEqual(before.Id, other.Id);
        Assert.NotEqual(default, before.Id);
    }

    /// <summary>The handle follows the file through its rename, so the file that was made goes, never another one at a name.</summary>
    [Fact]
    public void DeleteNewFile_DeletesTheFileWhereARenameTookIt_AndNotAnotherAtItsName()
    {
        var path = Path.Combine(_test.Root, "v1.1a2b3c4d.tmp");
        var slot = Path.Combine(_test.Root, "v1.json");

        using (var file = FileSystem.Instance.CreateFile(path, "{}\n"u8)!)
        {
            Assert.True(FileSystem.Instance.MoveFile(path, slot, replace: false));
            File.WriteAllText(path, "another");

            FileSystem.Instance.DeleteNewFile(file);
        }

        Assert.False(File.Exists(slot));
        Assert.Equal("another", File.ReadAllText(path));
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

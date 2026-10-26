namespace Kiler.Storage.Tests;

public sealed class DataFolderTests : IDisposable
{
    private readonly TestFolder _test = new();
    private readonly string _data;

    public DataFolderTests() => _data = Path.Combine(_test.Root, "data");

    public void Dispose() => _test.Dispose();

    [Fact]
    public void Create_MakesTheFoldersOfADataFolder()
    {
        var data = DataFolder.Create(_data);

        Assert.Equal(_data, data.Root);
        Assert.False(data.IsPrivate);
        Assert.Equal(["audit", "history", "private", "records", "stashes", "templates", "trash", "users"], EmptyFolders(_data));
    }

    [Fact]
    public void Create_InAnEmptyFolder_MakesTheFolders()
    {
        Directory.CreateDirectory(_data);

        DataFolder.Create(_data);

        Assert.Equal(["audit", "history", "private", "records", "stashes", "templates", "trash", "users"], EmptyFolders(_data));
    }

    [Fact]
    public void Create_InAFolderWithSomethingInIt_IsRefusedAndAddsNothing()
    {
        Directory.CreateDirectory(_data);
        File.WriteAllText(Path.Combine(_data, "notes.txt"), "made up");

        Assert.Throws<IOException>(() => DataFolder.Create(_data));

        Assert.Equal(["notes.txt"], Names(_data));
    }

    [Fact]
    public void Create_InAFolderLeftHalfMade_FinishesIt()
    {
        Directory.CreateDirectory(Path.Combine(_data, "records"));
        Directory.CreateDirectory(Path.Combine(_data, "users"));

        DataFolder.Create(_data);

        Assert.Equal(["audit", "history", "private", "records", "stashes", "templates", "trash", "users"], EmptyFolders(_data));
    }

    [Fact]
    public void Create_WhenItFailsHalfway_FinishesOnTheNextTry()
    {
        var creates = 0;
        var files = new TestFileSystem { BeforeCreate = _ => FailOn(++creates == 4) };

        Assert.Throws<IOException>(() => DataFolder.Create(_data, files));
        Assert.Equal(2, Names(_data).Length);

        DataFolder.Create(_data);

        Assert.Equal(["audit", "history", "private", "records", "stashes", "templates", "trash", "users"], EmptyFolders(_data));
    }

    [Fact]
    public void Create_InAFolderWithAFileInAFolderOfTheLayout_IsRefusedAndAddsNothing()
    {
        Directory.CreateDirectory(Path.Combine(_data, "records"));
        File.WriteAllText(Path.Combine(_data, "records", "8f3a1c.json"), "{}");

        Assert.Throws<IOException>(() => DataFolder.Create(_data));

        Assert.Equal(["records"], Names(_data));
        Assert.Equal(["8f3a1c.json"], Names(Path.Combine(_data, "records")));
    }

    [Fact]
    public void Create_InAFolderWithAnotherEmptyFolder_IsRefusedAndAddsNothing()
    {
        Directory.CreateDirectory(Path.Combine(_data, "other"));

        Assert.Throws<IOException>(() => DataFolder.Create(_data));

        Assert.Equal(["other"], Names(_data));
    }

    [Fact]
    public void Create_InAFolderWhereAFolderOfTheLayoutIsAFile_IsRefused()
    {
        Directory.CreateDirectory(_data);
        File.WriteAllText(Path.Combine(_data, "records"), "made up");

        Assert.Throws<IOException>(() => DataFolder.Create(_data));

        Assert.Equal(["records"], Names(_data));
    }

    [Fact]
    public void Create_InAFolderWhereAFolderOfTheLayoutIsAJunction_IsRefusedAndDoesNotFollowIt()
    {
        var elsewhere = Directory.CreateDirectory(Path.Combine(_test.Root, "elsewhere")).FullName;
        Directory.CreateDirectory(_data);
        TestFolder.CreateJunction(Path.Combine(_data, "records"), elsewhere);

        Assert.Throws<IOException>(() => DataFolder.Create(_data));

        Assert.Equal(["records"], Names(_data));
        Assert.Empty(Names(elsewhere));
    }

    [Fact]
    public void Create_WhenAFolderOfTheLayoutBecomesAJunctionJustBeforeItIsMade_IsRefused()
    {
        TestFolder.SkipWithoutJunctions(_test.Root);
        var elsewhere = Directory.CreateDirectory(Path.Combine(_test.Root, "elsewhere")).FullName;
        var records = Path.Combine(_data, "records");
        var files = new TestFileSystem
        {
            BeforeCreate = path =>
            {
                if (path == records)
                {
                    TestFolder.CreateJunction(records, elsewhere);
                }
            },
        };

        Assert.Throws<IOException>(() => DataFolder.Create(_data, files));

        Assert.Empty(Names(elsewhere));
    }

    [Fact]
    public void Create_WhenTheFolderAboveIsMissing_IsRefusedAndCreatesNothing()
    {
        var above = Path.Combine(_test.Root, "missing");

        Assert.Throws<DirectoryNotFoundException>(() => DataFolder.Create(Path.Combine(above, "data")));

        Assert.False(Directory.Exists(above));
    }

    [Fact]
    public void Create_WhenTheFolderAboveIsAFile_IsRefused()
    {
        var above = Path.Combine(_test.Root, "file");
        File.WriteAllText(above, "made up");

        Assert.Throws<DirectoryNotFoundException>(() => DataFolder.Create(Path.Combine(above, "data")));
    }

    [Fact]
    public void Create_InAFolderThatIsAJunction_IsRefusedAndDoesNotFollowIt()
    {
        var elsewhere = Directory.CreateDirectory(Path.Combine(_test.Root, "elsewhere")).FullName;
        TestFolder.CreateJunction(_data, elsewhere);

        Assert.Throws<IOException>(() => DataFolder.Create(_data));

        Assert.Empty(Names(elsewhere));
    }

    [Fact]
    public void Create_ARelativePath_IsRefused() => Assert.Throws<ArgumentException>(() => DataFolder.Create("data"));

    [Fact]
    public void Open_AFolderThatDoesNotExist_FailsAndCreatesNothing()
    {
        Assert.Throws<DirectoryNotFoundException>(() => DataFolder.Open(_data));

        Assert.False(Directory.Exists(_data));
    }

    [Fact]
    public void Open_AnExistingFolder_CreatesNothing()
    {
        Directory.CreateDirectory(_data);

        var data = DataFolder.Open(_data);

        Assert.Equal(_data, data.Root);
        Assert.False(data.IsPrivate);
        Assert.Empty(Names(_data));
    }

    [Fact]
    public void Open_APathWithDotsAndATrailingSeparator_GivesTheFullPath()
    {
        Directory.CreateDirectory(_data);

        Assert.Equal(_data, DataFolder.Open(Path.Combine(_data, "..", "data") + Path.DirectorySeparatorChar).Root);
    }

    [Fact]
    public void Open_AFile_IsRefused()
    {
        File.WriteAllText(_data, "made up");

        Assert.Throws<IOException>(() => DataFolder.Open(_data));
    }

    [Fact]
    public void Open_AJunction_IsRefusedAndNotFollowed()
    {
        var elsewhere = Directory.CreateDirectory(Path.Combine(_test.Root, "elsewhere")).FullName;
        TestFolder.CreateJunction(_data, elsewhere);

        Assert.Throws<IOException>(() => DataFolder.Open(_data));
    }

    [Fact]
    public void Open_ARelativePath_IsRefused() => Assert.Throws<ArgumentException>(() => DataFolder.Open("data"));

    [Fact]
    public void CreatePrivate_MakesTheFoldersOfAPrivateFolder()
    {
        var data = DataFolder.Create(_data);

        var mine = data.CreatePrivate("demo.user");

        Assert.Equal(Path.Combine(_data, "private", "demo.user"), mine.Root);
        Assert.True(mine.IsPrivate);
        Assert.Equal(["demo.user"], Names(Path.Combine(_data, "private")));
        Assert.Equal(["history", "records", "trash"], EmptyFolders(mine.Root));
    }

    [Fact]
    public void CreatePrivate_WhenTheFolderOfThePrivateFoldersIsMissing_MakesItToo()
    {
        Directory.CreateDirectory(_data);

        var mine = DataFolder.Open(_data).CreatePrivate("demo.user");

        Assert.Equal(["history", "records", "trash"], EmptyFolders(mine.Root));
    }

    [Theory]
    [InlineData("..")]
    [InlineData(@"..\demo.user")]
    [InlineData("con")]
    [InlineData("Demo.User")]
    public void CreatePrivate_ARefusedName_CreatesNothing(string account)
    {
        var data = DataFolder.Create(_data);

        Assert.Throws<ArgumentException>(() => data.CreatePrivate(account));

        Assert.Empty(Names(Path.Combine(_data, "private")));
        Assert.Equal(["audit", "history", "private", "records", "stashes", "templates", "trash", "users"], EmptyFolders(_data));
    }

    [Fact]
    public void CreatePrivate_AFolderWithSomethingInIt_IsRefused()
    {
        var data = DataFolder.Create(_data);
        var key = Path.Combine(_data, "private", "demo.user", "key.json");
        Directory.CreateDirectory(Path.GetDirectoryName(key)!);
        File.WriteAllText(key, "{}");

        Assert.Throws<IOException>(() => data.CreatePrivate("demo.user"));

        Assert.Equal(["key.json"], Names(Path.GetDirectoryName(key)!));
    }

    [Fact]
    public void CreatePrivate_AnEmptyFolderThatExists_IsRefused()
    {
        // Anyone can make it on the share before the user's first private record, and would then be its owner.
        var data = DataFolder.Create(_data);
        var mine = Directory.CreateDirectory(Path.Combine(_data, "private", "demo.user")).FullName;

        Assert.Throws<IOException>(() => data.CreatePrivate("demo.user"));

        Assert.Equal(["demo.user"], Names(Path.Combine(_data, "private")));
        Assert.Empty(Names(mine));
    }

    [Fact]
    public void CreatePrivate_WhenTheFolderAppearsJustBeforeTheRename_IsRefusedAndDoesNotReplaceIt()
    {
        var files = new TestFileSystem();
        var data = DataFolder.Create(_data, files);
        files.BeforeMove = (_, to) =>
        {
            Directory.CreateDirectory(to);
            File.WriteAllText(Path.Combine(to, "key.json"), "{}");
        };

        Assert.Throws<IOException>(() => data.CreatePrivate("demo.user"));

        Assert.Equal(["key.json"], Names(Path.Combine(_data, "private", "demo.user")));
    }

    [Fact]
    public void CreatePrivate_WhenItFailsHalfway_LeavesNoPrivateFolder_AndTheNextTryMakesIt()
    {
        var creates = 0;
        var files = new TestFileSystem();
        var data = DataFolder.Create(_data, files);
        files.BeforeCreate = _ => FailOn(++creates == 2);

        Assert.Throws<IOException>(() => data.CreatePrivate("demo.user"));
        Assert.False(Directory.Exists(Path.Combine(_data, "private", "demo.user")));

        var mine = data.CreatePrivate("demo.user");

        Assert.Equal(["history", "records", "trash"], EmptyFolders(mine.Root));
    }

    [Fact]
    public void CreatePrivate_WhenTheDataFolderIsGone_FailsAndCreatesNothing()
    {
        var data = DataFolder.Create(_data);
        Directory.Delete(_data, recursive: true);

        Assert.Throws<DirectoryNotFoundException>(() => data.CreatePrivate("demo.user"));

        Assert.False(Directory.Exists(_data));
    }

    [Fact]
    public void CreatePrivate_WhenTheFolderOfThePrivateFoldersIsAJunction_IsRefusedAndDoesNotFollowIt()
    {
        var data = DataFolder.Create(_data);
        var elsewhere = Directory.CreateDirectory(Path.Combine(_test.Root, "elsewhere")).FullName;
        Directory.Delete(Path.Combine(_data, "private"));
        TestFolder.CreateJunction(Path.Combine(_data, "private"), elsewhere);

        Assert.Throws<IOException>(() => data.CreatePrivate("demo.user"));

        Assert.Empty(Names(elsewhere));
    }

    [Fact]
    public void CreatePrivate_InAPrivateFolder_IsRefused()
    {
        var mine = DataFolder.Create(_data).CreatePrivate("demo.user");

        Assert.Throws<InvalidOperationException>(() => mine.CreatePrivate("other.user"));
    }

    [Fact]
    public void OpenPrivate_AnExistingFolder_IsAPrivateFolder()
    {
        var data = DataFolder.Create(_data);
        data.CreatePrivate("demo.user");

        var mine = data.OpenPrivate("demo.user");

        Assert.Equal(Path.Combine(_data, "private", "demo.user"), mine.Root);
        Assert.True(mine.IsPrivate);
    }

    [Fact]
    public void OpenPrivate_AFolderThatDoesNotExist_FailsAndCreatesNothing()
    {
        var data = DataFolder.Create(_data);

        Assert.Throws<DirectoryNotFoundException>(() => data.OpenPrivate("demo.user"));

        Assert.Empty(Names(Path.Combine(_data, "private")));
    }

    [Fact]
    public void OpenPrivate_WhenTheFolderOfThePrivateFoldersIsMissing_FailsAndCreatesNothing()
    {
        Directory.CreateDirectory(_data);

        Assert.Throws<DirectoryNotFoundException>(() => DataFolder.Open(_data).OpenPrivate("demo.user"));

        Assert.Empty(Names(_data));
    }

    [Fact]
    public void OpenPrivate_WhenTheFolderOfThePrivateFoldersIsAFile_IsRefused()
    {
        Directory.CreateDirectory(_data);
        File.WriteAllText(Path.Combine(_data, "private"), "made up");

        Assert.Throws<IOException>(() => DataFolder.Open(_data).OpenPrivate("demo.user"));
    }

    [Theory]
    [InlineData("..")]
    [InlineData(@"..\..\data")]
    [InlineData("nul")]
    public void OpenPrivate_ARefusedName_IsRefused(string account)
    {
        var data = DataFolder.Create(_data);

        Assert.Throws<ArgumentException>(() => data.OpenPrivate(account));
    }

    [Fact]
    public void OpenPrivate_AJunction_IsRefusedAndNotFollowed()
    {
        var data = DataFolder.Create(_data);
        var elsewhere = Directory.CreateDirectory(Path.Combine(_test.Root, "elsewhere")).FullName;
        TestFolder.CreateJunction(Path.Combine(_data, "private", "demo.user"), elsewhere);

        Assert.Throws<IOException>(() => data.OpenPrivate("demo.user"));
    }

    [Fact]
    public void OpenPrivate_WhenTheFolderOfThePrivateFoldersIsAJunction_IsRefusedAndNotFollowed()
    {
        var data = DataFolder.Create(_data);
        var elsewhere = Directory.CreateDirectory(Path.Combine(_test.Root, "elsewhere")).FullName;
        Directory.CreateDirectory(Path.Combine(elsewhere, "demo.user"));
        Directory.Delete(Path.Combine(_data, "private"));
        TestFolder.CreateJunction(Path.Combine(_data, "private"), elsewhere);

        Assert.Throws<IOException>(() => data.OpenPrivate("demo.user"));
    }

    [Fact]
    public void Paths_OfADocumentInAFolder()
    {
        var data = DataFolder.Open(Directory.CreateDirectory(_data).FullName);
        var record = DocumentKey.InFolder("records", "8f3a1c");

        Assert.Equal(Path.Combine(_data, "records", "8f3a1c.json"), data.DocumentPath(record));
        Assert.Equal(Path.Combine(_data, "history", "records", "8f3a1c", "v6.json"), data.HistoryPath(record, 6));
        Assert.Equal(Path.Combine(_data, "trash", "records", "8f3a1c.json"), data.TrashPath(record));
    }

    [Fact]
    public void Paths_OfADocumentAtTheRoot()
    {
        var data = DataFolder.Open(Directory.CreateDirectory(_data).FullName);
        var settings = DocumentKey.AtRoot("settings");

        Assert.Equal(Path.Combine(_data, "settings.json"), data.DocumentPath(settings));
        Assert.Equal(Path.Combine(_data, "history", "settings", "v3.json"), data.HistoryPath(settings, 3));
        Assert.Equal(Path.Combine(_data, "trash", "settings.json"), data.TrashPath(settings));
    }

    [Fact]
    public void Paths_InAPrivateFolder()
    {
        var mine = DataFolder.Create(_data).CreatePrivate("demo.user");
        var record = DocumentKey.InFolder("records", "4c1d9e");
        var key = DocumentKey.AtRoot("key");

        Assert.Equal(Path.Combine(mine.Root, "records", "4c1d9e.json"), mine.DocumentPath(record));
        Assert.Equal(Path.Combine(mine.Root, "history", "records", "4c1d9e", "v2.json"), mine.HistoryPath(record, 2));
        Assert.Equal(Path.Combine(mine.Root, "trash", "records", "4c1d9e.json"), mine.TrashPath(record));
        Assert.Equal(Path.Combine(mine.Root, "key.json"), mine.DocumentPath(key));
        Assert.Equal(Path.Combine(mine.Root, "history", "key", "v1.json"), mine.HistoryPath(key, 1));
    }

    [Theory]
    [InlineData("history", "8f3a1c")]
    [InlineData("trash", "8f3a1c")]
    [InlineData("audit", "8f3a1c")]
    [InlineData("private", "demo.user")]
    [InlineData("other", "8f3a1c")]
    [InlineData(null, "records")]
    [InlineData(null, "key")]
    [InlineData(null, "share")]
    public void Paths_OfADocumentOutsideTheDataFolder_AreRefused(string? folder, string id)
    {
        var data = DataFolder.Open(Directory.CreateDirectory(_data).FullName);
        var key = folder is null ? DocumentKey.AtRoot(id) : DocumentKey.InFolder(folder, id);

        Assert.Throws<ArgumentException>(() => data.DocumentPath(key));
        Assert.Throws<ArgumentException>(() => data.HistoryPath(key, 1));
        Assert.Throws<ArgumentException>(() => data.TrashPath(key));
    }

    [Theory]
    [InlineData("users", "demo.user")]
    [InlineData("templates", "db")]
    [InlineData(null, "settings")]
    [InlineData(null, "levels")]
    public void Paths_OfADocumentOutsideAPrivateFolder_AreRefused(string? folder, string id)
    {
        var mine = DataFolder.Create(_data).CreatePrivate("demo.user");
        var key = folder is null ? DocumentKey.AtRoot(id) : DocumentKey.InFolder(folder, id);

        Assert.Throws<ArgumentException>(() => mine.DocumentPath(key));
    }

    [Fact]
    public void HistoryPath_AVersionBelowOne_IsRefused()
    {
        var data = DataFolder.Open(Directory.CreateDirectory(_data).FullName);

        Assert.Throws<ArgumentOutOfRangeException>(() => data.HistoryPath(DocumentKey.InFolder("records", "8f3a1c"), 0));
    }

    [Theory]
    [InlineData("records", "..")]
    [InlineData("records", @"..\..\x")]
    [InlineData("records", "8F3A1C")]
    [InlineData("..", "8f3a1c")]
    [InlineData(@"records\..", "8f3a1c")]
    [InlineData("records", "")]
    public void DocumentKey_InFolder_ARefusedName_IsRefused(string folder, string id) =>
        Assert.Throws<ArgumentException>(() => DocumentKey.InFolder(folder, id));

    [Theory]
    [InlineData("..")]
    [InlineData("con")]
    [InlineData(@"..\settings")]
    public void DocumentKey_AtRoot_ARefusedName_IsRefused(string name) => Assert.Throws<ArgumentException>(() => DocumentKey.AtRoot(name));

    [Fact]
    public void List_GivesTheDocumentsOfAFolderWithTheInfoFromTheListing()
    {
        var data = DataFolder.Create(_data);
        var file = Path.Combine(_data, "records", "8f3a1c.json");
        File.WriteAllText(file, """{"id":"8f3a1c","version":1}""");
        File.WriteAllText(Path.Combine(_data, "records", "b72e09.json"), "{}");

        var listing = data.List("records");

        Assert.Equal(
            [DocumentKey.InFolder("records", "8f3a1c"), DocumentKey.InFolder("records", "b72e09")],
            listing.Documents.Select(document => document.Key).OrderBy(key => key.Id, StringComparer.Ordinal));
        var info = new FileInfo(file);
        var entry = listing.Documents.Single(document => document.Key.Id == "8f3a1c").File;
        Assert.Equal("8f3a1c.json", entry.Name);
        Assert.Equal(info.Length, entry.Size);
        Assert.Equal(info.CreationTimeUtc, entry.CreationTime.UtcDateTime);
        Assert.Equal(info.LastWriteTimeUtc, entry.LastWriteTime.UtcDateTime);
        Assert.Empty(listing.Links);
        Assert.Empty(listing.BadNames);
    }

    [Fact]
    public void List_SkipsAndGivesApartLinksAndBadNames_AndLeavesOutWhatIsNotADocument()
    {
        var data = DataFolder.Create(_data);
        var records = Path.Combine(_data, "records");
        var elsewhere = Directory.CreateDirectory(Path.Combine(_test.Root, "elsewhere")).FullName;
        File.WriteAllText(Path.Combine(elsewhere, "b72e09.json"), "{}");
        File.WriteAllText(Path.Combine(records, "8f3a1c.json"), "{}");
        // Bad names that Windows can make; the device names, which it cannot always make, are in StorageNameTests.
        File.WriteAllText(Path.Combine(records, "Demo.json"), "{}");
        File.WriteAllText(Path.Combine(records, "a1b2c3.JSON"), "{}");
        File.WriteAllText(Path.Combine(records, "a..b.json"), "{}");
        File.WriteAllText(Path.Combine(records, ".json"), "{}");
        File.WriteAllText(Path.Combine(records, "notes.txt"), "made up");
        File.WriteAllText(Path.Combine(records, "8f3a1c.ab12.tmp"), "{}");
        Directory.CreateDirectory(Path.Combine(records, "sub.json"));
        TestFolder.CreateJunction(Path.Combine(records, "b72e09.json"), elsewhere);
        File.SetAttributes(Path.Combine(records, "8f3a1c.json"), FileAttributes.Hidden);

        var listing = data.List("records");

        Assert.Equal("8f3a1c", Assert.Single(listing.Documents).Key.Id);
        Assert.Equal("b72e09.json", Assert.Single(listing.Links).Name);
        Assert.Equal([".json", "Demo.json", "a..b.json", "a1b2c3.JSON"], listing.BadNames.Select(entry => entry.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void List_TheRoot_GivesItsDocumentsAndTheFoldersThatAreLinks()
    {
        var data = DataFolder.Create(_data);
        var elsewhere = Directory.CreateDirectory(Path.Combine(_test.Root, "elsewhere")).FullName;
        File.WriteAllText(Path.Combine(_data, "settings.json"), "{}");
        File.WriteAllText(Path.Combine(_data, "levels.json"), "{}");
        File.WriteAllText(Path.Combine(_data, "other.json"), "{}");
        File.WriteAllText(Path.Combine(_data, "Settings.JSON.json"), "{}");
        Directory.Delete(Path.Combine(_data, "records"));
        TestFolder.CreateJunction(Path.Combine(_data, "records"), elsewhere);

        var listing = data.List(null);

        Assert.Equal(
            [DocumentKey.AtRoot("levels"), DocumentKey.AtRoot("settings")],
            listing.Documents.Select(document => document.Key).OrderBy(key => key.Id, StringComparer.Ordinal));
        Assert.Equal("records", Assert.Single(listing.Links).Name);
        Assert.Equal("Settings.JSON.json", Assert.Single(listing.BadNames).Name);
    }

    [Fact]
    public void List_AFolderThatIsAJunction_IsRefusedAndNotFollowed()
    {
        var data = DataFolder.Create(_data);
        var elsewhere = Directory.CreateDirectory(Path.Combine(_test.Root, "elsewhere")).FullName;
        File.WriteAllText(Path.Combine(elsewhere, "8f3a1c.json"), "{}");
        Directory.Delete(Path.Combine(_data, "records"));
        TestFolder.CreateJunction(Path.Combine(_data, "records"), elsewhere);

        Assert.Throws<IOException>(() => data.List("records"));
    }

    [Fact]
    public void List_TheRootWhenItHasBecomeAJunction_IsRefusedAndNotFollowed()
    {
        var data = DataFolder.Create(_data);
        var moved = _data + "-moved";
        Directory.Move(_data, moved);
        File.WriteAllText(Path.Combine(moved, "settings.json"), "{}");
        TestFolder.CreateJunction(_data, moved);

        Assert.Throws<IOException>(() => data.List(null));
    }

    [Fact]
    public void List_InAPrivateFolder_AFolderOfTheDataFolder_IsRefused()
    {
        var mine = DataFolder.Create(_data).CreatePrivate("demo.user");

        Assert.Throws<ArgumentException>(() => mine.List("users"));
    }

    [Fact]
    public void List_AFolderThatIsMissing_Fails()
    {
        var data = DataFolder.Create(_data);
        Directory.Delete(Path.Combine(_data, "records"));

        Assert.Throws<DirectoryNotFoundException>(() => data.List("records"));
    }

    [Theory]
    [InlineData("history")]
    [InlineData("private")]
    [InlineData("other")]
    [InlineData("..")]
    public void List_AFolderThatHoldsNoDocuments_IsRefused(string folder)
    {
        var data = DataFolder.Create(_data);

        Assert.Throws<ArgumentException>(() => data.List(folder));
    }

    [Fact]
    public void List_AFolderOfManyFiles_AsksForTheAttributesOnlyOfTheFolder()
    {
        var files = new TestFileSystem();
        var data = DataFolder.Create(_data, files);
        for (var i = 0; i < 20; i++)
        {
            File.WriteAllText(Path.Combine(_data, "records", $"r{i:D4}.json"), "{}");
        }

        files.AttributeRequests = 0;
        var listing = data.List("records");

        Assert.Equal(20, listing.Documents.Count);
        Assert.Equal(1, files.AttributeRequests);
    }

    /// <summary>The names in a folder, files and folders, in order.</summary>
    private static string[] Names(string folder) =>
        [.. Directory.GetFileSystemEntries(folder).Select(entry => Path.GetFileName(entry)).Order(StringComparer.Ordinal)];

    /// <summary>The names in a folder that must hold only empty folders.</summary>
    private static string[] EmptyFolders(string folder)
    {
        Assert.Empty(Directory.GetFiles(folder));
        Assert.All(Directory.GetDirectories(folder), subfolder => Assert.Empty(Directory.GetFileSystemEntries(subfolder)));
        return Names(folder);
    }

    private static void FailOn(bool fail)
    {
        if (fail)
        {
            throw new IOException("The network name is no longer available.");
        }
    }
}

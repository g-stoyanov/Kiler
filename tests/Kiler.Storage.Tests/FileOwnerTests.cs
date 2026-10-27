using System.Text.Json.Nodes;

namespace Kiler.Storage.Tests;

/// <summary>
/// The owners of the files (M2.6, design 7.8). The layer reads the owner of a file through the handle it reads the content
/// with, as a SID string, or gives none when it cannot be read, and never fails a read for it. A new private folder must have
/// the owner of the temporary folder it was made as, read through the handle that made it. The owners come from a fake
/// (<see cref="TestOwners"/>), so the tests do not depend on the account that runs them.
/// </summary>
public sealed class FileOwnerTests : IDisposable
{
    private static readonly DocumentKey Record = DocumentKey.InFolder(DataFolder.Records, "8f3a1c");
    private static readonly DocumentKey OtherRecord = DocumentKey.InFolder(DataFolder.Records, "b72e09");

    private readonly TestFolder _test = new();
    private readonly TestOwners _owners = new();
    private readonly string _data;

    public FileOwnerTests() => _data = Path.Combine(_test.Root, "data");

    public void Dispose() => _test.Dispose();

    [Fact]
    public void Read_GivesTheOwnerOfEachFileThatIsRead()
    {
        var data = DataFolder.Create(_data, _owners);
        data.CreateDocument(Record, Content("mine"));
        data.CreateDocument(OtherRecord, Content("theirs"));
        _owners.Set(data.DocumentPath(OtherRecord), TestOwners.Other);

        Assert.Equal(TestOwners.Me, data.Read(Record)?.Owner);
        Assert.Equal(TestOwners.Other, data.Read(OtherRecord)?.Owner);
    }

    [Fact]
    public void Read_ADamagedFile_GivesItsOwner()
    {
        var data = DataFolder.Create(_data, _owners);
        File.WriteAllText(data.DocumentPath(Record), "not json");
        _owners.Set(data.DocumentPath(Record), TestOwners.Other);

        var read = data.Read(Record);

        Assert.NotNull(read);
        Assert.True(read.IsDamaged);
        Assert.Equal(TestOwners.Other, read.Owner);
    }

    [Fact]
    public void Read_WhenTheOwnerCannotBeRead_GivesTheDocumentWithoutAnOwner()
    {
        var data = DataFolder.Create(_data, _owners);
        data.CreateDocument(Record, Content("mine"));
        data.CreateDocument(OtherRecord, Content("theirs"));
        _owners.Set(data.DocumentPath(Record), null);

        var read = data.Read(Record);

        Assert.NotNull(read);
        Assert.False(read.IsDamaged, $"Damaged: {read.Damage}");
        Assert.Equal(1, read.Document.Version);
        Assert.Null(read.Owner);
        Assert.Equal(TestOwners.Me, data.Read(OtherRecord)?.Owner);
    }

    [Fact]
    public void Read_GivesTheOwnerOfTheFileItRead_NotOfAnotherFilePutAtItsNameSince()
    {
        var files = new TestFileSystem();
        var data = DataFolder.Create(_data, files);
        data.CreateDocument(Record, Content("mine"));
        var path = data.DocumentPath(Record);
        var swapped = false;
        files.Before = (operation, _) =>
        {
            if (operation == nameof(IFileSystem.Owner) && !swapped)
            {
                swapped = true;
                File.Move(path, Path.Combine(_test.Root, "away.json"));
                File.WriteAllBytes(path, DocumentFormat.New(Record, Content("theirs")));
                files.Owners.Set(path, TestOwners.Other);
            }
        };

        var read = data.Read(Record);

        Assert.True(swapped);
        Assert.Equal("mine", read?.Document?.Content["name"]?.GetValue<string>());
        Assert.Equal(TestOwners.Me, read?.Owner);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadVersion_GivesTheOwnerOfTheSlot(bool inPrivate)
    {
        var data = Root(inPrivate);
        data.CreateDocument(Record, Content("first"));
        data.ReplaceDocument(Record, 1, Content("second"));
        _owners.Set(data.HistoryPath(Record, 1), TestOwners.Other);

        Assert.Equal(TestOwners.Other, data.ReadVersion(Record, 1)?.Owner);
        Assert.Equal(TestOwners.Me, data.Read(Record)?.Owner);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Versions_GiveTheOwnerOfEachSlotAndOfTheCurrentVersion(bool inPrivate)
    {
        var data = Root(inPrivate);
        data.CreateDocument(Record, Content("first"));
        data.ReplaceDocument(Record, 1, Content("second"));
        data.ReplaceDocument(Record, 2, Content("third"));
        _owners.Set(data.HistoryPath(Record, 1), TestOwners.Other);
        _owners.Set(data.DocumentPath(Record), TestOwners.Other);

        Assert.Equal(
            new (int, string?)[] { (1, TestOwners.Other), (2, TestOwners.Me), (3, TestOwners.Other) },
            data.Versions(Record, DateTimeOffset.UtcNow).Select(version => (version.Version, version.Owner)));
    }

    [Fact]
    public void Versions_WhoseOwnersCannotBeRead_AreListedWithoutAnOwner()
    {
        var data = DataFolder.Create(_data, _owners);
        data.CreateDocument(Record, Content("first"));
        data.ReplaceDocument(Record, 1, Content("second"));
        data.ReplaceDocument(Record, 2, Content("third"));
        data.ReplaceDocument(Record, 3, Content("fourth"));
        _owners.Set(data.HistoryPath(Record, 1), null);
        _owners.Set(data.DocumentPath(Record), null);

        // A slot that someone holds open without sharing cannot be opened for its owner.
        using var held = new FileStream(data.HistoryPath(Record, 2), FileMode.Open, FileAccess.Read, FileShare.None);

        Assert.Equal(
            new (int, string?)[] { (1, null), (2, null), (3, TestOwners.Me), (4, null) },
            data.Versions(Record, DateTimeOffset.UtcNow).Select(version => (version.Version, version.Owner)));
    }

    [Fact]
    public void Versions_OpenOnlyTheNewestSlotsForTheirOwners()
    {
        var files = new TestFileSystem();
        var data = DataFolder.Create(_data, files);
        var slots = Path.GetDirectoryName(data.HistoryPath(Record, 1))!;
        Directory.CreateDirectory(slots);
        for (var version = 1; version <= DataFolder.SlotsWithOwners + 1; version++)
        {
            File.WriteAllText(data.HistoryPath(Record, version), "{}");
        }

        var opened = 0;
        files.Before = (operation, path) =>
        {
            if (operation == nameof(IFileSystem.OpenRead) && path.StartsWith(slots, StringComparison.Ordinal))
            {
                opened++;
            }
        };

        var versions = data.Versions(Record, DateTimeOffset.UtcNow);

        Assert.Equal(DataFolder.SlotsWithOwners, opened);
        Assert.Equal(DataFolder.SlotsWithOwners + 1, versions.Count);
        Assert.Null(versions[0].Owner);
        Assert.All(versions.Skip(1), version => Assert.Equal(TestOwners.Me, version.Owner));
    }

    [Fact]
    public void Versions_ChooseTheSlotsWithOwnersUpToTheVersionAfterTheCurrentOne()
    {
        var files = new TestFileSystem();
        var data = DataFolder.Create(_data, files);
        data.CreateDocument(Record, Content("first"));
        data.ReplaceDocument(Record, 1, Content("second"));
        data.ReplaceDocument(Record, 2, Content("third"));

        // Names that no write made, far above the current version 3, which would push the real slots out.
        for (var version = 1000; version < 1000 + DataFolder.SlotsWithOwners; version++)
        {
            File.WriteAllText(data.HistoryPath(Record, version), "{}");
        }

        var opened = 0;
        var slots = Path.GetDirectoryName(data.HistoryPath(Record, 1))!;
        files.Before = (operation, path) =>
        {
            if (operation == nameof(IFileSystem.OpenRead) && path.StartsWith(slots, StringComparison.Ordinal))
            {
                opened++;
            }
        };

        var versions = data.Versions(Record, DateTimeOffset.UtcNow);

        Assert.Equal(2, opened);
        Assert.Equal(
            new (int, string?)[] { (1, TestOwners.Me), (2, TestOwners.Me), (3, TestOwners.Me) },
            versions.Take(3).Select(version => (version.Version, version.Owner)));
        Assert.All(versions.Skip(3), version => Assert.Null(version.Owner));
    }

    [Fact]
    public void DeleteAndRestore_MoveTheFile_SoItKeepsItsOwner()
    {
        var data = DataFolder.Create(_data, _owners);
        data.CreateDocument(Record, Content("first"));
        _owners.Set(data.DocumentPath(Record), TestOwners.Other);

        Assert.Equal(WriteOutcome.Deleted, data.DeleteDocument(Record, 1).Outcome);
        Assert.Equal(WriteOutcome.Written, data.RestoreFromTrash(Record).Outcome);

        Assert.Equal(TestOwners.Other, data.Read(Record)?.Owner);
    }

    [Theory]
    [InlineData(TestOwners.Other)]
    [InlineData(null)]
    public void CreatePrivate_WhenSomeoneSwapsTheTemporaryFolderForOneOfTheirOwnBeforeTheRename_IsRefused(string? theirs)
    {
        var files = new TestFileSystem();
        var data = DataFolder.Create(_data, files);
        files.BeforeMove = (from, _) =>
        {
            Directory.Move(from, Path.Combine(_test.Root, "away"));
            Directory.CreateDirectory(from);
            files.Owners.Set(from, theirs);
        };

        Assert.Throws<IOException>(() => data.CreatePrivate("demo.user"));

        // The rename was done: the check came after it.
        Assert.True(Directory.Exists(Path.Combine(_data, DataFolder.Private, "demo.user")));
    }

    [Fact]
    public void CreatePrivate_WhenSomeoneMovesAnotherFolderOfTheSameOwnerInPlaceOfTheTemporaryOne_IsRefused()
    {
        var files = new TestFileSystem();
        var data = DataFolder.Create(_data, files);
        files.BeforeMove = (from, _) =>
        {
            // A folder that the same account made elsewhere on the share, such as a history folder of its own.
            var theirs = Directory.CreateDirectory(Path.Combine(_test.Root, "theirs")).FullName;
            Directory.Move(from, Path.Combine(_test.Root, "away"));
            Directory.Move(theirs, from);
        };

        Assert.Throws<IOException>(() => data.CreatePrivate("demo.user"));

        Assert.True(Directory.Exists(Path.Combine(_data, DataFolder.Private, "demo.user")));
    }

    [Fact]
    public void CreatePrivate_HoldsTheTemporaryFolderWhileItMakesItsFolders()
    {
        var files = new TestFileSystem();
        var data = DataFolder.Create(_data, files);
        var refused = new List<Exception?>();
        files.BeforeCreate = path =>
        {
            if (refused.Count == 0 && Path.GetFileName(path) == DataFolder.Records)
            {
                refused.Add(Xunit.Record.Exception(() => Directory.Move(Path.GetDirectoryName(path)!, Path.Combine(_test.Root, "away"))));
            }
        };

        var mine = data.CreatePrivate("demo.user");

        AssertRefused(refused, count: 1);
        Assert.True(mine.IsPrivate);
    }

    [Fact]
    public void CreatePrivate_HoldsTheDataFolderAndTheFolderOfThePrivateFolders()
    {
        var files = new TestFileSystem();
        var data = DataFolder.Create(_data, files);
        var refused = new List<Exception?>();
        files.BeforeMove = (_, _) =>
        {
            refused.Add(Xunit.Record.Exception(() => Directory.Move(Path.Combine(_data, DataFolder.Private), Path.Combine(_test.Root, "away"))));
            refused.Add(Xunit.Record.Exception(() => Directory.Move(_data, Path.Combine(_test.Root, "away"))));
        };

        var mine = data.CreatePrivate("demo.user");

        AssertRefused(refused, count: 2);
        Assert.True(mine.IsPrivate);
    }

    [Theory]
    [InlineData(TestOwners.Other)]
    [InlineData(null)]
    public void CreatePrivate_WhenTheOwnerOfTheTemporaryFolderChangesBeforeTheRename_IsRefused(string? theirs)
    {
        var files = new TestFileSystem();
        var data = DataFolder.Create(_data, files);

        // The same folder, so the same id, taken over by another account, or made unreadable.
        files.BeforeMove = (from, _) => files.Owners.Set(from, theirs);

        Assert.Throws<IOException>(() => data.CreatePrivate("demo.user"));

        Assert.True(Directory.Exists(Path.Combine(_data, DataFolder.Private, "demo.user")));
    }

    [Theory]
    [InlineData(".tmp")]
    [InlineData(@"\" + DataFolder.Records)]
    public void CreatePrivate_WhenSomeoneMakesOneOfItsFoldersFirst_IsRefusedAndLeavesNoPrivateFolder(string ending)
    {
        var files = new TestFileSystem();
        var data = DataFolder.Create(_data, files);
        files.BeforeCreate = path =>
        {
            if (path.EndsWith(ending, StringComparison.Ordinal))
            {
                Directory.CreateDirectory(path);
            }
        };

        Assert.Throws<IOException>(() => data.CreatePrivate("demo.user"));

        Assert.False(Directory.Exists(Path.Combine(_data, DataFolder.Private, "demo.user")));
    }

    [Fact]
    public void CreatePrivate_WhenTheFileSystemGivesTheFolderNoId_IsRefusedAndLeavesNoPrivateFolder()
    {
        var files = new TestFileSystem { ZeroIds = true };
        var data = DataFolder.Create(_data, files);

        Assert.Throws<IOException>(() => data.CreatePrivate("demo.user"));

        Assert.False(Directory.Exists(Path.Combine(_data, DataFolder.Private, "demo.user")));
    }

    [Fact]
    public void CreatePrivate_WhenTheOwnerOfTheFolderItMadeCannotBeRead_IsRefusedAndLeavesNoPrivateFolder()
    {
        var files = new TestFileSystem();
        var data = DataFolder.Create(_data, files);
        files.Owners.Default = null;

        Assert.Throws<IOException>(() => data.CreatePrivate("demo.user"));

        Assert.False(Directory.Exists(Path.Combine(_data, DataFolder.Private, "demo.user")));
    }

    [Fact]
    public void CreatePrivate_WithTheOwnerOfTheFolderItMade_MakesThePrivateFolder()
    {
        var files = new TestFileSystem();
        var mine = DataFolder.Create(_data, files).CreatePrivate("demo.user");

        Assert.True(mine.IsPrivate);
        Assert.Equal(Path.Combine(_data, DataFolder.Private, "demo.user"), mine.Root);
    }

    [Fact]
    public void CreateAndOpen_WithoutTheOwners_AreRefused()
    {
        Assert.Throws<ArgumentNullException>(() => DataFolder.Create(_data, (IFileOwner)null!));
        Assert.Throws<ArgumentNullException>(() => DataFolder.Open(_data, (IFileOwner)null!));
        Assert.False(Directory.Exists(_data));
    }

    /// <summary>Each rename was refused, as Windows refuses one of a held folder (<c>PinnedFolderTests</c>).</summary>
    private static void AssertRefused(List<Exception?> refused, int count)
    {
        Assert.Equal(count, refused.Count);
        Assert.All(refused, exception => Assert.True(exception is IOException or UnauthorizedAccessException, "Windows allowed it while the folder was held."));
    }

    private static JsonObject Content(string name) => new() { ["template"] = "db", ["name"] = name };

    /// <summary>The data folder with the fake owners, or the private folder of a made-up account in it.</summary>
    private DataFolder Root(bool inPrivate)
    {
        var shared = DataFolder.Create(_data, _owners);
        return inPrivate ? shared.CreatePrivate("demo.user") : shared;
    }
}

using System.Text.Json.Nodes;
using Microsoft.Extensions.Time.Testing;

namespace Kiler.Storage.Tests;

/// <summary>
/// The private folder of the current user in the local copy (M2.7b): loaded and written through the copy as the data folder
/// is, in the same places of the copy under <c>private\&lt;account&gt;\</c>, and in the same budget; no other private folder.
/// </summary>
public sealed class DataCopyPrivateTests : IDisposable
{
    private const string Me = "demo.user";
    private const string Other = "demo.other";

    private static readonly DocumentKey Record = DocumentKey.InFolder(DataFolder.Records, "8f3a1c");
    private static readonly DocumentKey OtherRecord = DocumentKey.InFolder(DataFolder.Records, "b72e09");
    private static readonly DocumentKey ThirdRecord = DocumentKey.InFolder(DataFolder.Records, "c3d4e5");
    private static readonly DocumentKey Key = DocumentKey.AtRoot(DataFolder.Key);
    private static readonly DocumentKey MyRecord = Record.InPrivate(Me);

    private readonly TestFolder _test = new();
    private readonly TestFileSystem _files = new();
    private readonly FakeTimeProvider _clock = new(DateTimeOffset.UtcNow);
    private readonly DataFolder _share;
    private readonly string _copy;

    public DataCopyPrivateTests()
    {
        _share = DataFolder.Create(Path.Combine(_test.Root, "data"), _files);
        _copy = Directory.CreateDirectory(Path.Combine(_test.Root, "copy")).FullName;
    }

    public void Dispose() => _test.Dispose();

    [Fact]
    public void DocumentKey_InAPrivateFolder_CarriesItsAccount_AndIsAnotherKeyThanTheShared()
    {
        Assert.Null(Record.Account);
        Assert.Equal(Me, MyRecord.Account);
        Assert.Equal(Record.Folder, MyRecord.Folder);
        Assert.Equal(Record.Id, MyRecord.Id);
        Assert.NotEqual(Record, MyRecord);
        Assert.NotEqual(MyRecord, Record.InPrivate(Other));
        Assert.Equal(MyRecord, DocumentKey.InFolder(DataFolder.Records, "8f3a1c").InPrivate(Me));
        Assert.Equal(@"private\demo.user\records\8f3a1c", MyRecord.ToString());
        Assert.Equal(@"private\demo.user\key", Key.InPrivate(Me).ToString());
    }

    [Theory]
    [InlineData("Demo.User")]
    [InlineData("..")]
    [InlineData(@"a\b")]
    [InlineData("demo.user.tmp")]
    public void DocumentKey_InAPrivateFolderOfANameThatFailsTheCheck_IsRefused(string account) =>
        Assert.Throws<ArgumentException>(() => Record.InPrivate(account));

    [Fact]
    public void DataFolder_AKeyOfAnotherRoot_IsRefused()
    {
        var mine = _share.CreatePrivate(Me);
        var other = _share.CreatePrivate(Other);

        Assert.Equal(mine.DocumentPath(Record), mine.DocumentPath(MyRecord));
        Assert.Throws<ArgumentException>(() => _share.DocumentPath(MyRecord));
        Assert.Throws<ArgumentException>(() => other.DocumentPath(MyRecord));
        Assert.Throws<ArgumentException>(() => _share.CreateDocument(MyRecord, Content("made up")));
        Assert.False(File.Exists(_share.DocumentPath(Record)), "A private key was written into the data folder.");
    }

    [Fact]
    public void Load_ThePrivateFolderOfTheCurrentUser_IsLoaded_AndAnotherOneIsNot()
    {
        WriteShared(Record, "shared");
        var mine = _share.CreatePrivate(Me);
        var other = _share.CreatePrivate(Other);
        Create(mine, Record, "mine");
        Create(mine, Key, "key");
        Create(other, Record, "other");
        var copy = Open(Me);
        _files.Reads = 0;

        var load = copy.Load();

        Assert.Equal(3, _files.Reads);
        Assert.Equal(Keys([Record, MyRecord, Key.InPrivate(Me)]), Keys(load.Changed));
        Assert.Equal("shared", Name(load.Changed.Single(document => document.Key == Record)));
        Assert.Equal("mine", Name(load.Changed.Single(document => document.Key == MyRecord)));
        Assert.Empty(load.Skipped);
        Assert.Equal(File.ReadAllBytes(mine.DocumentPath(Record)), File.ReadAllBytes(PrivateCopyPath(Me, Record)));
        Assert.Equal(File.ReadAllBytes(mine.DocumentPath(Key)), File.ReadAllBytes(PrivateCopyPath(Me, Key)));
        Assert.False(Directory.Exists(PrivateCopyRoot(Other)), "The private folder of another user is in the copy.");
        Assert.Equal(Keys([Record, MyRecord, Key.InPrivate(Me)]), Keys(Open(Me).Documents()));
        Assert.Empty(copy.Load().Changed);
    }

    [Fact]
    public void Load_WithoutAnAccount_TakesNoPrivateFolder()
    {
        WriteShared(Record, "shared");
        Create(_share.CreatePrivate(Me), Record, "mine");

        var load = Open(null).Load();

        Assert.Equal(Record, Assert.Single(load.Changed).Key);
        Assert.False(Directory.Exists(Path.Combine(_copy, "data", DataFolder.Private)), "A private folder is in the copy.");
    }

    [Fact]
    public void Load_APrivateDocumentAndASharedOneOfTheSameId_AreKeptApart()
    {
        WriteShared(Record, "shared");
        Create(_share.CreatePrivate(Me), Record, "mine");
        var copy = Open(Me);
        copy.Load();
        File.Delete(_share.DocumentPath(Record));

        var load = copy.Load();

        Assert.Equal(Record, Assert.Single(load.Gone));
        var document = Assert.Single(Open(Me).Documents());
        Assert.Equal(MyRecord, document.Key);
        Assert.Equal("mine", Name(document));
        Assert.True(File.Exists(Path.Combine(_copy, "kept", DataFolder.Records, "8f3a1c.json")), "The last good bytes of the shared document are not kept.");
        Assert.True(File.Exists(PrivateCopyPath(Me, Record)), "The private document has left the copy.");
    }

    [Fact]
    public void Writes_ThroughTheCopy_GoToThePrivateFolderOfTheirKey_AndToItsPlacesInTheCopy()
    {
        var mine = _share.CreatePrivate(Me);
        var copy = Open(Me);

        Assert.Equal(WriteOutcome.Written, copy.Create(MyRecord, Content("first")).Outcome);
        Assert.Equal(WriteOutcome.Written, copy.Replace(MyRecord, 1, Content("second")).Outcome);

        Assert.False(File.Exists(_share.DocumentPath(Record)), "The private document is in the data folder.");
        Assert.Equal(File.ReadAllBytes(mine.DocumentPath(Record)), File.ReadAllBytes(PrivateCopyPath(Me, Record)));
        Assert.Equal(File.ReadAllBytes(mine.HistoryPath(Record, 1)), File.ReadAllBytes(PrivateSlotPath(Me, Record, 1)));
        Assert.Equal("second", Name(Assert.Single(Open(Me).Documents())));

        Assert.Equal(WriteOutcome.Deleted, copy.Delete(MyRecord, 2).Outcome);

        Assert.True(File.Exists(mine.TrashPath(Record)), "The private document is not in the trash of its folder.");
        Assert.Equal(File.ReadAllBytes(mine.TrashPath(Record)), File.ReadAllBytes(PrivateCopyPath(Me, Record, inTrash: true)));
        Assert.Equal(MyRecord, Assert.Single(Open(Me).Trash()).Key);
        Assert.Empty(Open(Me).Documents());

        Assert.Equal(WriteOutcome.Written, copy.RestoreFromTrash(MyRecord).Outcome);

        Assert.Equal(MyRecord, Assert.Single(Open(Me).Documents()).Key);
        Assert.Empty(Open(Me).Trash());
        var load = copy.Load();
        Assert.Empty(load.Outside);
        Assert.Empty(load.Skipped);
        Assert.False(Directory.Exists(Path.Combine(_copy, "data", DataFolder.Records)), "A private document is in the place of the shared ones.");
    }

    [Fact]
    public void Writes_ThroughTheCopy_ToAPrivateFolderOfAnotherUser_AreRefused_AndWriteNothing()
    {
        var other = _share.CreatePrivate(Other);
        Create(other, OtherRecord, "other");
        var mine = _share.CreatePrivate(Me);
        var theirs = Record.InPrivate(Other);
        foreach (var (copy, key) in (ValueTuple<DataCopy, DocumentKey>[])[(Open(Me), theirs), (Open(null), MyRecord)])
        {
            Assert.Throws<ArgumentException>(() => copy.Create(key, Content("made up")));
            Assert.Throws<ArgumentException>(() => copy.Replace(OtherRecord.InPrivate(Other), 1, Content("made up")));
            Assert.Throws<ArgumentException>(() => copy.Delete(OtherRecord.InPrivate(Other), 1));
            Assert.Throws<ArgumentException>(() => copy.RestoreFromTrash(key));
        }

        Assert.False(File.Exists(other.DocumentPath(Record)), "A write went to the private folder of another user.");
        Assert.False(File.Exists(mine.DocumentPath(Record)), "A write without an account went to a private folder.");
        Assert.True(File.Exists(other.DocumentPath(OtherRecord)), "A delete went to the private folder of another user.");
        Assert.False(File.Exists(other.HistoryPath(OtherRecord, 1)), "A replace went to the private folder of another user.");
    }

    [Fact]
    public void Load_APrivateFolderMadeAfterTheOpen_IsLoadedByTheNextLoad()
    {
        WriteShared(Record, "shared");
        var copy = Open(Me);
        var first = copy.Load();
        Assert.Equal(Record, Assert.Single(first.Changed).Key);
        Assert.Empty(first.Skipped);
        Create(_share.CreatePrivate(Me), Record, "mine");

        var load = copy.Load();

        Assert.Equal(MyRecord, Assert.Single(load.Changed).Key);
        Assert.Empty(load.Skipped);
    }

    [Fact]
    public void Load_ADocumentOfAPrivateFolderThatHasGone_IsGone_AndKeepsItsLastGoodBytes()
    {
        WriteShared(Record, "shared");
        var mine = _share.CreatePrivate(Me);
        Create(mine, Record, "mine");
        var copy = OpenAt(Me);
        copy.Load();
        var good = File.ReadAllBytes(mine.DocumentPath(Record));
        Directory.Delete(mine.Root, recursive: true);

        var load = copy.Load();

        Assert.Equal(MyRecord, Assert.Single(load.Gone));
        Assert.Empty(load.Skipped);
        Assert.Equal(good, File.ReadAllBytes(PrivateKeptPath(Me, Record)));
        Assert.False(File.Exists(PrivateCopyPath(Me, Record)), "The gone private document is still in data.");
        _clock.Advance(InPlaceEdits.SettleTime);
        Assert.Equal(new OutsideChange(MyRecord, InTrash: false, OutsideReason.Gone, LastGood: 1), Assert.Single(copy.Load().Outside));
        Assert.Equal(Record, Assert.Single(OpenAt(Me).Documents()).Key);
    }

    [Fact]
    public void Load_APrivateFolderThatIsALink_IsReported_AndNotFollowed_AndItsDocumentsStay()
    {
        var mine = _share.CreatePrivate(Me);
        Create(mine, Record, "mine");
        var copy = Open(Me);
        copy.Load();
        var elsewhere = Path.Combine(_test.Root, "elsewhere");
        TestFolder.SkipWithoutJunctions(mine.Root);
        Directory.Move(mine.Root, elsewhere);
        File.WriteAllBytes(Path.Combine(elsewhere, DataFolder.Records, "b72e09.json"), DocumentFormat.New(OtherRecord, Content("elsewhere")));
        TestFolder.CreateJunction(mine.Root, elsewhere);
        _files.Reads = 0;

        var load = copy.Load();

        Assert.Equal(0, _files.Reads);
        Assert.Empty(load.Changed);
        Assert.Empty(load.Gone);
        Assert.Equal(new SkippedEntry(@"private\demo.user", null, SkipReason.Unreadable), Assert.Single(load.Skipped));
        Assert.Equal(MyRecord, Assert.Single(Open(Me).Documents()).Key);
    }

    [Fact]
    public void Load_ThePrivateFolder_CountsInTheOneBudgetOfTheCopy_AndIsReportedByItsFolder()
    {
        WriteShared(Record, "shared");
        WriteShared(OtherRecord, "shared too");
        Create(_share.CreatePrivate(Me), Record, "mine");

        var load = Open(Me, DataCopyTests.Limits(documents: 2)).Load();

        Assert.Equal(Keys([Record, OtherRecord]), Keys(load.Changed));
        Assert.Equal(new SkippedEntry(@"private\demo.user\records", "8f3a1c.json", SkipReason.OverBudget), Assert.Single(load.Skipped));
    }

    [Fact]
    public void Load_FollowsAMoveToTheTrashOfThePrivateFolder()
    {
        var mine = _share.CreatePrivate(Me);
        Create(mine, Record, "mine");
        var copy = Open(Me);
        copy.Load();
        Assert.Equal(WriteOutcome.Deleted, mine.DeleteDocument(Record, 1).Outcome);

        var load = copy.Load();

        Assert.Equal(MyRecord, Assert.Single(load.Gone));
        Assert.Empty(load.Outside);
        Assert.Equal(MyRecord, Assert.Single(copy.Trash()).Key);
        Assert.Equal(File.ReadAllBytes(mine.TrashPath(Record)), File.ReadAllBytes(PrivateCopyPath(Me, Record, inTrash: true)));
        Assert.False(File.Exists(PrivateCopyPath(Me, Record)), "The private document is still in its folder in the copy.");
    }

    [Fact]
    public void Load_TakesTheHistoryOfAPrivateDocument_IntoThePrivateFolderOfTheCopy()
    {
        var mine = _share.CreatePrivate(Me);
        Create(mine, Record, "first");
        var copy = OpenAt(Me);
        copy.Load();
        Replace(mine, Record, 1, "second");
        Replace(mine, Record, 2, "third");

        var load = copy.Load();

        Assert.Equal(3, Assert.Single(load.Changed).Read.Document?.Version);
        Assert.Empty(load.Skipped);
        foreach (var version in (int[])[1, 2])
        {
            Assert.Equal(File.ReadAllBytes(mine.HistoryPath(Record, version)), File.ReadAllBytes(PrivateSlotPath(Me, Record, version)));
        }

        Assert.False(Directory.Exists(Path.Combine(_copy, "data", DataFolder.History)), "A private slot is in the history of the shared documents.");

        // The slot of the version that the copy had, and the one from the share, are named with their account after a restart.
        _clock.Advance(TimeSpan.FromMinutes(11));
        OpenAt(Me);
        Assert.All((int[])[1, 2], version => Assert.True(File.Exists(PrivateSlotPath(Me, Record, version)), $"The slot of version {version} has gone."));
    }

    [Fact]
    public void Load_TheHistoryOfAPrivateDocument_WaitsWhileItsFolderCannotBeOpened()
    {
        var mine = _share.CreatePrivate(Me);
        Create(mine, Record, "first");
        var copy = Open(Me);
        copy.Load();
        Replace(mine, Record, 1, "second");
        Replace(mine, Record, 2, "third");
        var history = Path.GetDirectoryName(mine.HistoryPath(Record, 1));
        _files.Before = (step, path) =>
        {
            if (step == nameof(IFileSystem.List) && path == history)
            {
                throw new IOException("Made up.");
            }
        };
        Assert.Contains(new SkippedEntry(@"private\demo.user\history\records\8f3a1c", null, SkipReason.Unreadable), copy.Load().Skipped);
        _files.Before = (step, path) =>
        {
            if (step == nameof(IFileSystem.Attributes) && path == mine.Root)
            {
                throw new IOException("Made up.");
            }
        };

        var load = copy.Load();

        Assert.Equal(new SkippedEntry(@"private\demo.user", null, SkipReason.Unreadable), Assert.Single(load.Skipped));
        Assert.Empty(load.Gone);
        Assert.False(File.Exists(PrivateSlotPath(Me, Record, 2)), "The slot was taken while its folder could not be opened.");
        _files.Before = null;
        Assert.Empty(copy.Load().Skipped);
        Assert.Equal(File.ReadAllBytes(mine.HistoryPath(Record, 2)), File.ReadAllBytes(PrivateSlotPath(Me, Record, 2)));
    }

    [Fact]
    public void Open_WithAnotherAccount_KeepsTheEntriesOfThePrivateFolder_AndGivesNoneOfThem()
    {
        WriteShared(Record, "shared");
        var mine = _share.CreatePrivate(Me);
        Create(mine, Record, "first");
        Create(mine, OtherRecord, "deleted");
        Create(mine, ThirdRecord, "gone");
        var copy = OpenAt(Me);
        copy.Load();
        Assert.Equal(WriteOutcome.Written, copy.Replace(MyRecord, 1, Content("second")).Outcome);
        Assert.Equal(WriteOutcome.Deleted, copy.Delete(OtherRecord.InPrivate(Me), 1).Outcome);
        File.Delete(mine.DocumentPath(ThirdRecord));
        copy.Load();
        string[] files =
        [
            PrivateCopyPath(Me, Record),
            PrivateSlotPath(Me, Record, 1),
            PrivateCopyPath(Me, OtherRecord, inTrash: true),
            PrivateSlotPath(Me, OtherRecord, 1),
            PrivateKeptPath(Me, ThirdRecord),
        ];
        Assert.All(files, file => Assert.True(File.Exists(file), $"{file} is not in the copy."));

        // Old, they stay while the entries and the slots of the index name them with their account.
        _clock.Advance(TimeSpan.FromMinutes(11));
        var again = OpenAt(Me);
        Assert.Equal(Keys([Record, MyRecord]), Keys(again.Documents()));
        Assert.Equal(OtherRecord.InPrivate(Me), Assert.Single(again.Trash()).Key);
        Assert.All(files, file => Assert.True(File.Exists(file), $"{file} has gone from the copy of its account."));

        // Another account, or none, gives none of them, and keeps them as the private folder of another user, for a restore (M2.7c).
        foreach (var account in (string?[])[Other, null])
        {
            var another = OpenAt(account);
            Assert.Equal(Record, Assert.Single(another.Documents()).Key);
            Assert.Empty(another.Trash());
            Assert.Equal(Record, Assert.Single(another.Load().Changed.Concat(another.Documents())).Key);
        }

        Assert.All(files, file => Assert.True(File.Exists(file), $"{file} of another account has gone from the copy."));
        var back = OpenAt(Me);
        Assert.Equal(Keys([Record, MyRecord]), Keys(back.Documents()));
        Assert.Equal(OtherRecord.InPrivate(Me), Assert.Single(back.Trash()).Key);
    }

    [Fact]
    public void Writes_ThroughTheCopy_ToAPrivateFolderThatIsNotThere_FailAndWriteNothing()
    {
        var copy = Open(Me);

        Assert.Throws<DirectoryNotFoundException>(() => copy.Create(MyRecord, Content("mine")));

        Assert.False(Directory.Exists(Path.Combine(_share.Root, DataFolder.Private, Me)), "The write made the private folder.");
        Assert.False(Directory.Exists(PrivateCopyRoot(Me)), "The write is in the copy.");
        Assert.Empty(Open(Me).Documents());
    }

    [Fact]
    public void Load_AFolderOfThePrivateFolderThatCannotBeListed_IsReported_AndItsDocumentsStay()
    {
        var mine = _share.CreatePrivate(Me);
        Create(mine, Record, "mine");
        var copy = Open(Me);
        copy.Load();
        var records = Path.Combine(mine.Root, DataFolder.Records);
        _files.Before = (step, path) =>
        {
            if (step == nameof(IFileSystem.List) && path == records)
            {
                throw new IOException("Made up.");
            }
        };

        var load = copy.Load();

        Assert.Equal(new SkippedEntry(@"private\demo.user\records", null, SkipReason.Unreadable), Assert.Single(load.Skipped));
        Assert.Empty(load.Gone);
        Assert.Equal(MyRecord, Assert.Single(Open(Me).Documents()).Key);
    }

    [Fact]
    public void Writes_ThroughTheCopy_WhenPrivateIsMadeALinkAfterItsCheck_FailAndWriteNothingThere()
    {
        var mine = _share.CreatePrivate(Me);
        var copy = Open(Me);
        TestFolder.SkipWithoutJunctions(_share.Root);
        string? elsewhere = null;
        SwapPrivateForALink(
            after: (step, path) => step == nameof(IFileSystem.Attributes) && path == mine.Root,
            at: (_, _) => true,
            plant: folder => elsewhere = folder);

        Assert.Throws<IOException>(() => copy.Create(MyRecord, Content("mine")));

        Assert.NotNull(elsewhere);
        Assert.False(File.Exists(Path.Combine(elsewhere, DataFolder.Records, "8f3a1c.json")), "The write followed the link.");
        Assert.Empty(Open(Me).Documents());
    }

    [Fact]
    public void Load_WhenPrivateIsMadeALinkAfterItsCheck_ReportsThePrivateFolder_AndTakesNothingThere()
    {
        var mine = _share.CreatePrivate(Me);
        Create(mine, Record, "mine");
        var copy = Open(Me);
        copy.Load();
        TestFolder.SkipWithoutJunctions(_share.Root);
        SwapPrivateForALink(
            after: (step, path) => step == nameof(IFileSystem.Attributes) && path == mine.Root,
            at: (_, _) => true,
            plant: folder => File.WriteAllBytes(Path.Combine(folder, DataFolder.Records, "b72e09.json"), DocumentFormat.New(OtherRecord, Content("elsewhere"))));

        var load = copy.Load();

        Assert.Empty(load.Changed);
        Assert.Empty(load.Gone);
        Assert.Equal(new SkippedEntry(@"private\demo.user", null, SkipReason.Unreadable), Assert.Single(load.Skipped));
        Assert.Equal(MyRecord, Assert.Single(Open(Me).Documents()).Key);
    }

    [Fact]
    public void Load_WhenPrivateIsMadeALinkBeforeTheHistoryIsListed_ReportsTheHistory_AndTakesNothingThere()
    {
        var mine = _share.CreatePrivate(Me);
        Create(mine, Record, "first");
        var copy = Open(Me);
        copy.Load();
        Replace(mine, Record, 1, "second");
        Replace(mine, Record, 2, "third");
        TestFolder.SkipWithoutJunctions(_share.Root);
        SwapPrivateForALink(
            after: (step, path) => step == nameof(IFileSystem.OpenRead) && path == mine.DocumentPath(Record),
            at: (step, _) => step == nameof(IFileSystem.HoldFolder),
            plant: folder => File.WriteAllBytes(
                Path.Combine(folder, DataFolder.History, DataFolder.Records, "8f3a1c", "v2.json"), DocumentFormat.Next(Record, 1, Content("planted"))));

        var load = copy.Load();

        Assert.Equal(3, Assert.Single(load.Changed).Read.Document?.Version);
        Assert.Contains(new SkippedEntry(@"private\demo.user\history\records\8f3a1c", null, SkipReason.Unreadable), load.Skipped);
        Assert.False(File.Exists(PrivateSlotPath(Me, Record, 2)), "A slot was taken through the link.");
    }

    [Theory]
    [InlineData("Demo.User")]
    [InlineData("..")]
    [InlineData("demo.user.tmp")]
    public void Open_WithAnAccountThatFailsTheNameCheck_IsRefused(string account) =>
        Assert.Throws<ArgumentException>(() => DataCopy.Open(_share, _copy, TimeProvider.System, account));

    private static JsonObject Content(string name) => new() { ["name"] = name };

    private static string? Name(LoadedDocument document) => (string?)document.Read.Document?.Content["name"];

    private static string[] Keys(IEnumerable<DocumentKey> keys) => [.. keys.Select(key => key.ToString()).Order(StringComparer.Ordinal)];

    private static string[] Keys(IEnumerable<LoadedDocument> documents) => Keys(documents.Select(document => document.Key));

    private static void Create(DataFolder root, DocumentKey key, string name) => Assert.Equal(WriteOutcome.Written, root.CreateDocument(key, Content(name)).Outcome);

    private static void Replace(DataFolder root, DocumentKey key, int version, string name) =>
        Assert.Equal(WriteOutcome.Written, root.ReplaceDocument(key, version, Content(name)).Outcome);

    private DataCopy Open(string? account) => DataCopy.Open(_share, _copy, TimeProvider.System, account);

    private DataCopy Open(string? account, CopyLimits limits) => DataCopy.Open(_share, _copy, TimeProvider.System, limits, account);

    /// <summary>Opens the copy on <see cref="_clock"/>.</summary>
    private DataCopy OpenAt(string? account) => DataCopy.Open(_share, _copy, _clock, account);

    /// <summary>
    /// Puts a junction at <c>private\</c> of the share, to where the folder goes, at the first step that <paramref name="at"/>
    /// takes after the step that <paramref name="after"/> names, as someone could between the check of
    /// <see cref="DataFolder.OpenPrivate"/> and the holds (security review of M2.7b), and gives the private folder of the
    /// account there to <paramref name="plant"/>. The test calls <see cref="TestFolder.SkipWithoutJunctions"/> first.
    /// </summary>
    private void SwapPrivateForALink(Func<string, string, bool> after, Func<string, string, bool> at, Action<string> plant)
    {
        var privateFolders = Path.Combine(_share.Root, DataFolder.Private);
        var elsewhere = Path.Combine(_test.Root, "elsewhere");
        var armed = false;
        _files.Before = (step, path) =>
        {
            if (armed && at(step, path))
            {
                _files.Before = null;
                Directory.Move(privateFolders, elsewhere);
                TestFolder.CreateJunction(privateFolders, elsewhere);
                plant(Path.Combine(elsewhere, Me));
                return;
            }

            armed |= after(step, path);
        };
    }

    /// <summary>A document file of the data folder, as a write of Kiler leaves it, at version 1.</summary>
    private void WriteShared(DocumentKey key, string name) => TestFolder.WriteAsKiler(_share.DocumentPath(key), DocumentFormat.New(key, Content(name)));

    /// <summary>Where the local copy keeps a private folder: <c>data\private\&lt;account&gt;\</c>, as the share has it.</summary>
    private string PrivateCopyRoot(string account) => Path.Combine(_copy, "data", DataFolder.Private, account);

    private string PrivateCopyPath(string account, DocumentKey key, bool inTrash = false) =>
        Path.Combine(PrivateCopyRoot(account), inTrash ? DataFolder.Trash : "", key.Folder ?? "", key.Id + ".json");

    private string PrivateKeptPath(string account, DocumentKey key) => Path.Combine(_copy, "kept", DataFolder.Private, account, key.Folder ?? "", key.Id + ".json");

    private string PrivateSlotPath(string account, DocumentKey key, int version) =>
        Path.Combine(PrivateCopyRoot(account), DataFolder.History, key.Folder ?? "", key.Id, $"v{version}.json");
}

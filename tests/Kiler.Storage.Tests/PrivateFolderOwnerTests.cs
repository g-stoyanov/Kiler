using System.Text.Json.Nodes;
using Microsoft.Extensions.Time.Testing;

namespace Kiler.Storage.Tests;

/// <summary>
/// The owner of a private folder (M2.7d, design 7.8): the owner and the id of its root, read through the handle of the folder
/// when it is made or first seen, are the reference that every later hold of it, and of each folder in it, is compared with,
/// and the local copy keeps it in its index (decided by the user). Another owner, or another folder put at the name, is
/// refused: a load reports it and takes nothing of it, its documents stay as the copy has them, and a write to it fails, until
/// the folder at the name is accepted.
/// </summary>
public sealed class PrivateFolderOwnerTests : IDisposable
{
    private const string Me = "demo.user";
    private const string Other = "demo.other";

    private static readonly DocumentKey Record = DocumentKey.InFolder(DataFolder.Records, "8f3a1c");
    private static readonly DocumentKey OtherRecord = DocumentKey.InFolder(DataFolder.Records, "b72e09");
    private static readonly DocumentKey ThirdRecord = DocumentKey.InFolder(DataFolder.Records, "c3d4e5");
    private static readonly DocumentKey MyRecord = Record.InPrivate(Me);

    private readonly TestFolder _test = new();
    private readonly TestFileSystem _files = new();
    private readonly FakeTimeProvider _clock = new(DateTimeOffset.UtcNow);
    private readonly DataFolder _share;
    private readonly string _copy;

    public PrivateFolderOwnerTests()
    {
        _share = DataFolder.Create(Path.Combine(_test.Root, "data"), _files);
        _copy = Directory.CreateDirectory(Path.Combine(_test.Root, "copy")).FullName;
    }

    /// <summary>What someone who can write the share does to a private folder.</summary>
    public enum Change
    {
        /// <summary>Renames it away and makes a folder of their own at its name.</summary>
        AnotherOwner,

        /// <summary>Renames it away and puts another folder of the same owner at its name.</summary>
        AnotherFolder,

        /// <summary>Takes ownership of it.</summary>
        TakenOver,
    }

    public void Dispose() => _test.Dispose();

    [Fact]
    public void CreatePrivate_KeepsTheOwnerAndTheIdOfTheFolderItMade()
    {
        var mine = _share.CreatePrivate(Me);

        Assert.Equal(new FolderIdentity(TestOwners.Me, IdOf(mine.Root)), mine.Identity);
        Assert.Null(_share.Identity);
    }

    [Fact]
    public void OpenPrivate_TakesTheOwnerAndTheIdOfTheFolderAtTheName()
    {
        var root = _share.CreatePrivate(Me).Root;
        _files.Owners.Set(root, TestOwners.Other);

        Assert.Equal(new FolderIdentity(TestOwners.Other, IdOf(root)), _share.OpenPrivate(Me).Identity);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OpenPrivate_WhenTheFolderCannotBeChecked_IsRefused(bool zeroIds)
    {
        var root = _share.CreatePrivate(Me).Root;
        if (zeroIds)
        {
            _files.ZeroIds = true;
        }
        else
        {
            _files.Owners.Set(root, null);
        }

        // An IOException and no ForeignFolderException: the folder is not known to be another.
        Assert.Throws<IOException>(() => _share.OpenPrivate(Me));
    }

    [Theory]
    [InlineData(Change.AnotherOwner)]
    [InlineData(Change.AnotherFolder)]
    [InlineData(Change.TakenOver)]
    public void Writes_WhenThePrivateFolderIsNoLongerTheOneThatWasOpened_AreRefused_AndWriteNothing(Change change)
    {
        var mine = _share.CreatePrivate(Me);
        Make(change, mine.Root);

        Assert.Throws<ForeignFolderException>(() => mine.CreateDocument(Record, Content("mine")));

        Assert.False(File.Exists(Path.Combine(mine.Root, DataFolder.Records, "8f3a1c.json")), "The write went into the folder at the name.");
    }

    [Theory]
    [InlineData(DataFolder.Records)]
    [InlineData(DataFolder.History)]
    [InlineData(DataFolder.Trash)]
    public void Writes_WhenAFolderInThePrivateFolderHasAnotherOwner_AreRefused(string folder)
    {
        var mine = _share.CreatePrivate(Me);
        Create(mine, Record, "first");
        var first = File.ReadAllBytes(mine.DocumentPath(Record));
        _files.Owners.Set(Path.Combine(mine.Root, folder), TestOwners.Other);

        Assert.Throws<ForeignFolderException>(() =>
        {
            switch (folder)
            {
                case DataFolder.Records:
                    mine.CreateDocument(OtherRecord, Content("second"));
                    break;
                case DataFolder.History:
                    mine.ReplaceDocument(Record, 1, Content("second"));
                    break;
                default:
                    mine.DeleteDocument(Record, 1);
                    break;
            }
        });

        Assert.False(File.Exists(mine.DocumentPath(OtherRecord)), "The write went into the folder of another owner.");
        Assert.Equal(first, File.ReadAllBytes(mine.DocumentPath(Record)));
        Assert.False(File.Exists(mine.TrashPath(Record)), "The document went to the trash of another owner.");
        Assert.Empty(Directory.GetFiles(mine.Root, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public void List_OfAPrivateFolderThatIsNoLongerTheOneThatWasOpened_IsRefused()
    {
        var mine = _share.CreatePrivate(Me);
        var theirs = _share.CreatePrivate(Other);
        Create(theirs, Record, "theirs");
        Make(Change.AnotherFolder, mine.Root);

        Assert.Throws<ForeignFolderException>(() => mine.List(null));
        Assert.Throws<ForeignFolderException>(() => mine.List(DataFolder.Records));
        Assert.Equal(Record.InPrivate(Other), Assert.Single(theirs.List(DataFolder.Records).Documents).Key);
    }

    [Theory]
    [InlineData(Change.AnotherOwner)]
    [InlineData(Change.AnotherFolder)]
    [InlineData(Change.TakenOver)]
    public void Load_WhenThePrivateFolderIsNoLongerTheOneTheCopyFirstSaw_ReportsIt_TakesNothing_AndItsDocumentsStay(Change change)
    {
        var mine = _share.CreatePrivate(Me);
        Create(mine, Record, "mine");
        OpenAt(Me).Load();
        Make(change, mine.Root);

        // The reference is in the index, so it holds after a restart.
        var copy = OpenAt(Me);
        _files.Reads = 0;
        var load = copy.Load();

        Assert.Equal(0, _files.Reads);
        Assert.Equal(new SkippedEntry(@"private\demo.user", null, SkipReason.ForeignFolder), Assert.Single(load.Skipped));
        Assert.Empty(load.Changed);
        Assert.Empty(load.Gone);
        _clock.Advance(InPlaceEdits.SettleTime);
        Assert.Empty(copy.Load().Outside);
        Assert.Equal(MyRecord, Assert.Single(OpenAt(Me).Documents()).Key);
    }

    [Fact]
    public void Load_AnEmptyPrivateFolderOfTheCurrentUser_IsKeptAsFirstSeen_AlsoAfterARestart()
    {
        var root = _share.CreatePrivate(Me).Root;
        Assert.Empty(OpenAt(Me).Load().Changed);
        Make(Change.AnotherOwner, root);

        var load = OpenAt(Me).Load();

        Assert.Equal(new SkippedEntry(@"private\demo.user", null, SkipReason.ForeignFolder), Assert.Single(load.Skipped));
        Assert.Empty(load.Changed);
    }

    [Fact]
    public void Writes_ThroughTheCopy_ToAPrivateFolderThatIsNotTheOneItKnows_AreRefused_AndWriteNothing()
    {
        var mine = _share.CreatePrivate(Me);
        Create(mine, Record, "mine");
        var copy = OpenAt(Me);
        copy.Load();
        Make(Change.AnotherOwner, mine.Root);

        Assert.Throws<ForeignFolderException>(() => copy.Create(ThirdRecord.InPrivate(Me), Content("third")));
        Assert.Throws<ForeignFolderException>(() => copy.Replace(MyRecord, 1, Content("second")));

        Assert.False(File.Exists(Path.Combine(mine.Root, DataFolder.Records, "8f3a1c.json")), "The write went into the folder at the name.");
        Assert.False(File.Exists(Path.Combine(mine.Root, DataFolder.Records, "c3d4e5.json")), "The write went into the folder at the name.");
        Assert.Equal("mine", Name(Assert.Single(OpenAt(Me).Documents())));
    }

    [Fact]
    public void Writes_ThroughTheCopy_BeforeAnyLoad_KeepTheFolderTheyFirstSaw()
    {
        var mine = _share.CreatePrivate(Me);
        Assert.Equal(WriteOutcome.Written, OpenAt(Me).Create(MyRecord, Content("mine")).Outcome);
        Make(Change.AnotherOwner, mine.Root);

        var load = OpenAt(Me).Load();

        Assert.Equal(new SkippedEntry(@"private\demo.user", null, SkipReason.ForeignFolder), Assert.Single(load.Skipped));
        Assert.Empty(load.Changed);
    }

    [Fact]
    public void Load_AFolderOfThePrivateFolderWithAnotherOwner_IsReported_AndItsDocumentsStay()
    {
        var mine = _share.CreatePrivate(Me);
        Create(mine, Record, "mine");
        var copy = OpenAt(Me);
        copy.Load();
        _files.Owners.Set(Path.Combine(mine.Root, DataFolder.Records), TestOwners.Other);

        var load = copy.Load();

        Assert.Equal(new SkippedEntry(@"private\demo.user\records", null, SkipReason.ForeignFolder), Assert.Single(load.Skipped));
        Assert.Empty(load.Gone);
        Assert.Equal(MyRecord, Assert.Single(OpenAt(Me).Documents()).Key);
    }

    [Fact]
    public void Load_TheHistoryFolderOfAPrivateDocumentWithAnotherOwner_IsReported_AndNoSlotIsTaken()
    {
        var mine = _share.CreatePrivate(Me);
        Create(mine, Record, "first");
        var copy = OpenAt(Me);
        copy.Load();
        Replace(mine, Record, 1, "second");
        Replace(mine, Record, 2, "third");
        _files.Owners.Set(Path.GetDirectoryName(mine.HistoryPath(Record, 1))!, TestOwners.Other);

        var load = copy.Load();

        Assert.Equal(new SkippedEntry(@"private\demo.user\history\records\8f3a1c", null, SkipReason.ForeignFolder), Assert.Single(load.Skipped));
        Assert.False(File.Exists(PrivateSlotPath(Me, Record, 2)), "A slot was taken from the folder of another owner.");
    }

    [Fact]
    public void Load_WhenTheOwnerOfThePrivateFolderCannotBeRead_ReportsItUnreadable_AndKeepsNoReference()
    {
        var mine = _share.CreatePrivate(Me);
        Create(mine, Record, "mine");
        var copy = OpenAt(Me);
        _files.Owners.Set(mine.Root, null);

        Assert.Equal(new SkippedEntry(@"private\demo.user", null, SkipReason.Unreadable), Assert.Single(copy.Load().Skipped));

        // Seen first once its owner can be read, as another's: no owner was kept to compare it with.
        SetOwner(mine.Root, TestOwners.Other);
        var load = copy.Load();
        Assert.Empty(load.Skipped);
        Assert.Equal(MyRecord, Assert.Single(load.Changed).Key);
    }

    [Fact]
    public void Load_WhenPrivateIsSwappedForAnotherFolderBetweenTwoLoads_ReportsThePrivateFolder()
    {
        var mine = _share.CreatePrivate(Me);
        Create(mine, Record, "mine");
        var copy = OpenAt(Me);
        copy.Load();
        Directory.Move(Path.Combine(_share.Root, DataFolder.Private), Path.Combine(_test.Root, "away"));

        // Another private\ with a folder of the same owner at the name.
        MakeFolder(mine.Root);
        var load = copy.Load();

        Assert.Equal(new SkippedEntry(@"private\demo.user", null, SkipReason.ForeignFolder), Assert.Single(load.Skipped));
        Assert.Empty(load.Changed);
        Assert.Empty(load.Gone);
    }

    [Theory]
    [InlineData("roots")]
    [InlineData("index")]
    [InlineData("owner")]
    public void Open_AnIndexWithoutWhatThePrivateFolderIs_TakesTheFolderAtTheNameAsFirstSeen(string missing)
    {
        var mine = _share.CreatePrivate(Me);
        Create(mine, Record, "mine");
        OpenAt(Me).Load();
        var path = Path.Combine(_copy, "index.json");
        var index = JsonNode.Parse(File.ReadAllBytes(path))!.AsObject();
        var root = index["roots"]![0]!.AsObject();
        switch (missing)
        {
            case "roots":
                index.Remove("roots");
                break;
            case "index":
                root["index"] = 0;
                break;
            default:
                root.Remove("owner");
                break;
        }

        File.WriteAllText(path, index.ToJsonString());
        Make(Change.AnotherOwner, mine.Root);

        var load = OpenAt(Me).Load();

        Assert.Empty(load.Skipped);
        Assert.Equal(OtherRecord.InPrivate(Me), Assert.Single(load.Changed).Key);
    }

    [Fact]
    public void Load_APrivateFolderThatWentAndCameBack_IsComparedWithTheFolderItHad()
    {
        var mine = _share.CreatePrivate(Me);
        Create(mine, Record, "mine");
        var copy = OpenAt(Me);
        copy.Load();
        Directory.Move(mine.Root, Path.Combine(_test.Root, "away"));
        Assert.Equal(MyRecord, Assert.Single(copy.Load().Gone));
        MakeFolderOfAnother(mine.Root);

        var load = copy.Load();

        Assert.Equal(new SkippedEntry(@"private\demo.user", null, SkipReason.ForeignFolder), Assert.Single(load.Skipped));
        Assert.Empty(load.Changed);
    }

    [Fact]
    public void CreatePrivate_ThroughTheCopy_KeepsTheFolderItMade_SoAnotherPutAtTheNameBeforeTheFirstLoadIsReported()
    {
        OpenAt(Me).CreatePrivate();
        var root = Path.Combine(_share.Root, DataFolder.Private, Me);
        Assert.True(Directory.Exists(Path.Combine(root, DataFolder.Records)), "The copy made no private folder.");
        Make(Change.AnotherOwner, root);

        var load = OpenAt(Me).Load();

        Assert.Equal(new SkippedEntry(@"private\demo.user", null, SkipReason.ForeignFolder), Assert.Single(load.Skipped));
        Assert.Empty(load.Changed);
    }

    [Fact]
    public void CreatePrivate_ThroughTheCopy_RefusedAfterTheRename_KeepsTheFolderItMade_SoTheFolderAtTheNameIsReported()
    {
        var copy = OpenAt(Me);
        _files.BeforeMove = (from, _) =>
        {
            // Someone swaps the temporary folder for one of their own just before the rename.
            _files.BeforeMove = null;
            Directory.Move(from, Path.Combine(_test.Root, "away"));
            Directory.CreateDirectory(from);
            _files.Owners.Set(from, TestOwners.Other);
        };

        Assert.Throws<IOException>(copy.CreatePrivate);

        Assert.True(Directory.Exists(Path.Combine(_share.Root, DataFolder.Private, Me)), "The check came before the rename.");
        Assert.Equal(new SkippedEntry(@"private\demo.user", null, SkipReason.ForeignFolder), Assert.Single(OpenAt(Me).Load().Skipped));
    }

    [Fact]
    public void CreatePrivate_ThroughTheCopy_WhenAFolderIsThere_IsRefused_AndWhatTheCopyKnewStays()
    {
        var mine = _share.CreatePrivate(Me);
        Create(mine, Record, "mine");
        var copy = OpenAt(Me);
        copy.Load();
        Make(Change.AnotherOwner, mine.Root);

        Assert.Throws<IOException>(copy.CreatePrivate);

        Assert.Equal(new SkippedEntry(@"private\demo.user", null, SkipReason.ForeignFolder), Assert.Single(OpenAt(Me).Load().Skipped));
    }

    [Fact]
    public void CreatePrivate_ThroughACopyWithoutAnAccount_IsRefused_AndMakesNothing()
    {
        Assert.Throws<InvalidOperationException>(() => OpenAt(null).CreatePrivate());

        Assert.False(Directory.Exists(Path.Combine(_share.Root, DataFolder.Private, Me)));
    }

    [Fact]
    public void AcceptPrivateFolder_TheNextLoadTakesTheFolderAtTheName_AndTheDocumentsItDoesNotHaveAreGone()
    {
        var mine = _share.CreatePrivate(Me);
        Create(mine, Record, "mine");
        var copy = OpenAt(Me);
        copy.Load();
        var good = File.ReadAllBytes(mine.DocumentPath(Record));
        Make(Change.AnotherOwner, mine.Root);
        Assert.Equal(SkipReason.ForeignFolder, Assert.Single(copy.Load().Skipped).Reason);

        copy.AcceptPrivateFolder(Me);

        // The index says so, so a restart does too.
        var load = OpenAt(Me).Load();
        Assert.Empty(load.Skipped);
        Assert.Equal(OtherRecord.InPrivate(Me), Assert.Single(load.Changed).Key);
        Assert.Equal(MyRecord, Assert.Single(load.Gone));
        Assert.Equal(good, File.ReadAllBytes(Path.Combine(_copy, "kept", DataFolder.Private, Me, DataFolder.Records, "8f3a1c.json")));
    }

    [Fact]
    public void AcceptPrivateFolder_CoversOnlyTheRoot_AndAFolderInItOfAnotherOwnerStaysRefused()
    {
        var mine = _share.CreatePrivate(Me);
        Create(mine, Record, "mine");
        var copy = OpenAt(Me);
        copy.Load();
        _files.Owners.Set(Path.Combine(mine.Root, DataFolder.Records), TestOwners.Other);

        copy.AcceptPrivateFolder(Me);

        Assert.Equal(new SkippedEntry(@"private\demo.user\records", null, SkipReason.ForeignFolder), Assert.Single(copy.Load().Skipped));
        Assert.Equal(MyRecord, Assert.Single(OpenAt(Me).Documents()).Key);
    }

    [Theory]
    [InlineData("Demo.User")]
    [InlineData("..")]
    public void AcceptPrivateFolder_OfAnAccountThatFailsTheNameCheck_IsRefused(string account) =>
        Assert.Throws<ArgumentException>(() => OpenAt(Me).AcceptPrivateFolder(account));

    [Fact]
    public void Load_ThePrivateFolderOfAnotherUser_IsComparedWithTheFolderTheCopyFirstSawOfThatAccount()
    {
        Create(_share.CreatePrivate(Me), Record, "mine");
        var theirs = _share.CreatePrivate(Other);
        Create(theirs, Record, "theirs");
        var first = OpenAt(Me);
        first.CopyOthers([Other]);
        first.Load();
        Make(Change.AnotherOwner, theirs.Root);
        var copy = OpenAt(Me);
        copy.CopyOthers([Other]);

        var load = copy.Load();

        Assert.Equal(new SkippedEntry(@"private\demo.other", null, SkipReason.ForeignFolder), Assert.Single(load.Skipped));
        _clock.Advance(InPlaceEdits.SettleTime);
        Assert.Empty(copy.Load().Outside);
        Assert.True(File.Exists(Path.Combine(_copy, "data", DataFolder.Private, Other, DataFolder.Records, "8f3a1c.json")), "Their document left the copy.");
        Assert.False(File.Exists(Path.Combine(_copy, "data", DataFolder.Private, Other, DataFolder.Records, "b72e09.json")), "A document of the folder at the name was taken.");
    }

    [Fact]
    public void Open_KeepsNoReferenceOfThePrivateFolderOfAnotherUserThatTheCopyHasNothingOf()
    {
        var theirs = _share.CreatePrivate(Other);
        var copy = OpenAt(Me);
        copy.CopyOthers([Other]);
        copy.Load();
        Make(Change.AnotherOwner, theirs.Root);
        Assert.Equal(SkipReason.ForeignFolder, Assert.Single(copy.Load().Skipped).Reason);
        var again = OpenAt(Me);
        again.CopyOthers([Other]);

        // Nothing of it to keep for a restore, so the folder at the name is seen first.
        Assert.Empty(again.Load().Skipped);
        Assert.True(File.Exists(Path.Combine(_copy, "data", DataFolder.Private, Other, DataFolder.Records, "b72e09.json")), "The folder at the name was not taken.");
    }

    private static JsonObject Content(string name) => new() { ["name"] = name };

    private static string? Name(LoadedDocument document) => (string?)document.Read.Document?.Content["name"];

    private static void Create(DataFolder root, DocumentKey key, string name) => Assert.Equal(WriteOutcome.Written, root.CreateDocument(key, Content(name)).Outcome);

    private static void Replace(DataFolder root, DocumentKey key, int version, string name) =>
        Assert.Equal(WriteOutcome.Written, root.ReplaceDocument(key, version, Content(name)).Outcome);

    private static FileId IdOf(string folder)
    {
        using var handle = NativeFiles.OpenFolder(folder);
        return NativeFiles.Id(handle);
    }

    /// <summary>Opens the copy on <see cref="_clock"/>.</summary>
    private DataCopy OpenAt(string? account) => DataCopy.Open(_share, _copy, _clock, account);

    /// <summary>Does <paramref name="change"/> to the private folder at <paramref name="root"/>.</summary>
    private void Make(Change change, string root)
    {
        if (change == Change.TakenOver)
        {
            _files.Owners.Set(root, TestOwners.Other);
            return;
        }

        Directory.Move(root, Path.Combine(_test.Root, "away"));
        if (change == Change.AnotherOwner)
        {
            MakeFolderOfAnother(root);
        }
        else
        {
            // Of the same owner, as the default owner of the tests is the one that made the private folder.
            MakeFolder(root);
        }
    }

    /// <summary>A folder with the folders of a private folder and a document, of another owner, at <paramref name="root"/>.</summary>
    private void MakeFolderOfAnother(string root)
    {
        MakeFolder(root);
        SetOwner(root, TestOwners.Other);
    }

    /// <summary>Gives a folder and every folder in it an owner, as when they were all made by that account.</summary>
    private void SetOwner(string root, string owner)
    {
        foreach (var folder in (string[])[root, .. Directory.GetDirectories(root, "*", SearchOption.AllDirectories)])
        {
            _files.Owners.Set(folder, owner);
        }
    }

    /// <summary>A folder with the folders of a private folder and a document in it, as someone could put at a name.</summary>
    private static void MakeFolder(string root)
    {
        foreach (var folder in (string[])[DataFolder.Records, DataFolder.History, DataFolder.Trash])
        {
            Directory.CreateDirectory(Path.Combine(root, folder));
        }

        TestFolder.WriteAsKiler(Path.Combine(root, DataFolder.Records, "b72e09.json"), DocumentFormat.New(OtherRecord, Content("planted")));
    }

    private string PrivateSlotPath(string account, DocumentKey key, int version) =>
        Path.Combine(_copy, "data", DataFolder.Private, account, DataFolder.History, key.Folder ?? "", key.Id, $"v{version}.json");
}

using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Time.Testing;

namespace Kiler.Storage.Tests;

/// <summary>
/// Writing back the private folder of another user from the local copy of an admin (M2.9c, PLAN M2 step 10, design 5.6): a
/// document gone, damaged, edited in place or out of order comes back as one of the user's own does (M2.9a, M2.9b), through
/// a write of its own, and the Kiler of its owner takes it. The write back makes no folder there, as one would have the SID
/// of the admin, which the Kiler of the owner refuses for good (M2.7d): a folder that is missing is
/// <see cref="WriteOutcome.NoFolder"/>, and a root that has gone is made again by the Kiler of the owner (decided by the
/// user). The two Kilers are two file systems on the same files, the owner's making them with its own SID
/// (<see cref="TestFileSystem.Maker"/>), the admin's with <see cref="TestOwners.Me"/>.
/// </summary>
public sealed class DataCopyWriteBackOthersTests : IDisposable
{
    /// <summary>The admin, whose copy writes back.</summary>
    private const string Me = "demo.user";

    /// <summary>The owner of the private folder.</summary>
    private const string Other = "demo.other";

    private static readonly DocumentKey Record = DocumentKey.InFolder(DataFolder.Records, "8f3a1c");
    private static readonly DocumentKey TheirRecord = Record.InPrivate(Other);
    private static readonly DocumentKey TheirOtherRecord = DocumentKey.InFolder(DataFolder.Records, "b72e09").InPrivate(Other);

    private readonly TestFolder _test = new();
    private readonly TestFileSystem _files = new();
    private readonly TestFileSystem _theirFiles;
    private readonly FakeTimeProvider _clock = new(DateTimeOffset.UtcNow);
    private readonly DataFolder _share;
    private readonly DataFolder _theirShare;
    private readonly string _root;

    public DataCopyWriteBackOthersTests()
    {
        _share = DataFolder.Create(Path.Combine(_test.Root, "data"), _files);
        _theirFiles = new TestFileSystem(_files.Owners) { Maker = TestOwners.Other };
        _theirShare = DataFolder.Open(_share.Root, _theirFiles);
        _root = Path.Combine(_share.Root, DataFolder.Private, Other);
    }

    /// <summary>How the file of a document at version 2 is changed outside Kiler.</summary>
    public enum Change
    {
        Gone,
        GoneFromTheTrash,
        Damaged,

        /// <summary>Edited in place at its version, so a write back over it takes the lock of M2.9b.</summary>
        EditedInPlace,

        /// <summary>Put back at version 1 from its history slot.</summary>
        OutOfOrder,
    }

    /// <summary>A folder of the private folder of another user that a write back of a document needs, and that is missing.</summary>
    public enum Missing
    {
        /// <summary><c>records\</c>, gone with the document.</summary>
        DocumentFolder,

        /// <summary><c>trash\records\</c>, gone with the document in it.</summary>
        TrashFolder,

        /// <summary>
        /// The history folder of a document never changed, at version 1 and edited in place, which the lock of a write back
        /// over it needs (M2.9b).
        /// </summary>
        HistoryFolderOfTheDocument,

        /// <summary>The same, in a private folder none of whose records were ever changed, so <c>history\records\</c> is missing too.</summary>
        HistoryFolderOfTheRecords,
    }

    public void Dispose() => _test.Dispose();

    /// <summary>
    /// A document of the private folder of another user comes back from the copy of an admin as its last good bytes, at their
    /// version, as one of the user's own does (M2.9a, M2.9b), and is the admin's, who wrote it. No folder of the admin is made
    /// there, so the Kiler of the owner takes the document as good, without <see cref="SkipReason.ForeignFolder"/>, and writes
    /// it again. The copy of the admin follows the write, and its next loads report nothing.
    /// </summary>
    [Theory]
    [InlineData(Change.Gone)]
    [InlineData(Change.GoneFromTheTrash)]
    [InlineData(Change.Damaged)]
    [InlineData(Change.EditedInPlace)]
    [InlineData(Change.OutOfOrder)]
    public void WriteBack_ADocumentOfThePrivateFolderOfAnotherUser_ComesBack_AndTheKilerOfItsOwnerTakesIt(Change change)
    {
        var inTrash = change == Change.GoneFromTheTrash;
        var theirs = OpenTheirs();
        theirs.CreatePrivate();
        Assert.Equal(WriteOutcome.Written, theirs.Create(TheirRecord, Content("first")).Outcome);
        Assert.Equal(WriteOutcome.Written, theirs.Replace(TheirRecord, 1, Content("second")).Outcome);
        if (inTrash)
        {
            Assert.Equal(WriteOutcome.Deleted, theirs.Delete(TheirRecord, 2).Outcome);
        }

        theirs.Load();
        var copy = OpenAdmin();
        var path = inTrash ? TrashPath(Record) : DocumentPath(Record);
        var good = File.ReadAllBytes(path);
        MakeChange(path, change);
        var listed = ListTheirs(copy);
        Assert.Equal(new OutsideChange(TheirRecord, inTrash, ReasonOf(change), LastGood: 2), listed);

        var result = copy.WriteBack(listed);

        Assert.Equal(WriteOutcome.Written, result.Outcome);
        Assert.Equal(2, result.Version);
        Assert.Equal(good, File.ReadAllBytes(path));
        Assert.Equal(TestOwners.Me, OwnerOfFile(path));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp", SearchOption.AllDirectories));
        AssertEveryFolderIsTheirs();
        Assert.Empty(copy.ToRestore());
        Assert.Empty(LoadAMinuteLater(copy).Outside);
        Assert.Empty(LoadAMinuteLater(copy).Outside);
        Assert.Empty(copy.ToRestore());

        var load = theirs.Load();
        Assert.Empty(load.Skipped);
        Assert.Empty(load.Outside);
        Assert.Equal("second", Name(Assert.Single(inTrash ? theirs.Trash() : theirs.Documents())));
        var next = inTrash ? theirs.RestoreFromTrash(TheirRecord) : theirs.Replace(TheirRecord, 2, Content("third"));
        Assert.Equal(WriteOutcome.Written, next.Outcome);
        AssertEveryFolderIsTheirs();
    }

    /// <summary>
    /// A write back into the private folder of another user makes no folder there (decided by the user): one of the admin
    /// would be refused by the Kiler of the owner for good (M2.7d), and by the write back itself, as it has another owner than
    /// the root. So a folder that it needs and that is missing is <see cref="WriteOutcome.NoFolder"/>: nothing is made or
    /// written, the file there stays as it is, and the change stays listed. The Kiler of the owner writes the change back from
    /// its own copy, which reports it too (M2.9a, M2.9b), and makes the folder as the owner.
    /// </summary>
    [Theory]
    [InlineData(Missing.DocumentFolder)]
    [InlineData(Missing.TrashFolder)]
    [InlineData(Missing.HistoryFolderOfTheDocument)]
    [InlineData(Missing.HistoryFolderOfTheRecords)]
    public void WriteBack_IntoThePrivateFolderOfAnotherUser_WhenAFolderThatItNeedsIsMissing_IsNoFolder_AndMakesNone(Missing missing)
    {
        var theirs = OpenTheirs();
        theirs.CreatePrivate();
        Assert.Equal(WriteOutcome.Written, theirs.Create(TheirRecord, Content("record")).Outcome);
        if (missing == Missing.TrashFolder)
        {
            Assert.Equal(WriteOutcome.Deleted, theirs.Delete(TheirRecord, 1).Outcome);
        }
        else if (missing == Missing.HistoryFolderOfTheDocument)
        {
            Assert.Equal(WriteOutcome.Written, theirs.Create(TheirOtherRecord, Content("other")).Outcome);
            Assert.Equal(WriteOutcome.Written, theirs.Replace(TheirOtherRecord, 1, Content("other, again")).Outcome);
        }

        var copy = OpenAdmin();
        var path = missing == Missing.TrashFolder ? TrashPath(Record) : DocumentPath(Record);
        var history = Path.GetDirectoryName(HistoryPath(Record, 1))!;
        var folder = missing switch
        {
            Missing.DocumentFolder or Missing.TrashFolder => Path.GetDirectoryName(path)!,
            Missing.HistoryFolderOfTheDocument => history,
            _ => Path.GetDirectoryName(history)!,
        };
        if (missing is Missing.DocumentFolder or Missing.TrashFolder)
        {
            Directory.Delete(folder, recursive: true);
        }
        else
        {
            TestFolder.EditInPlace(path, DocumentFormat.New(Record, Content("edited by hand")));
        }

        Assert.False(Directory.Exists(folder), "The folder that the test leaves out is there.");
        var there = File.Exists(path) ? File.ReadAllBytes(path) : null;
        var listed = ListTheirs(copy);

        Assert.Equal(WriteOutcome.NoFolder, copy.WriteBack(listed).Outcome);

        Assert.False(Directory.Exists(folder), "The write back made a folder in the private folder of another user.");
        Assert.Equal(there, File.Exists(path) ? File.ReadAllBytes(path) : null);
        Assert.Empty(Directory.GetFiles(_root, "*.tmp", SearchOption.AllDirectories));
        AssertEveryFolderIsTheirs();
        Assert.Equal(listed, Assert.Single(copy.ToRestore()));

        theirs.Load();
        _clock.Advance(InPlaceEdits.SettleTime);
        theirs.Load();
        Assert.Equal(WriteOutcome.Written, theirs.WriteBack(Assert.Single(theirs.ToRestore())).Outcome);
        Assert.True(Directory.Exists(folder), "The write back of the owner did not make the folder.");
        AssertEveryFolderIsTheirs();
    }

    /// <summary>
    /// The history of the private folder of another user makes a write back <see cref="WriteOutcome.Behind"/> as that of the
    /// user's own does (M2.9a): a version after the last good one, which the owner wrote and the copy of the admin did not
    /// see, and nothing is written.
    /// </summary>
    [Fact]
    public void WriteBack_IntoThePrivateFolderOfAnotherUser_WhoseHistoryHasAVersionThatTheCopyDidNotSee_IsBehind()
    {
        var theirs = OpenTheirs();
        theirs.CreatePrivate();
        Assert.Equal(WriteOutcome.Written, theirs.Create(TheirRecord, Content("first")).Outcome);
        var copy = OpenAdmin();
        Assert.Equal(WriteOutcome.Written, theirs.Replace(TheirRecord, 1, Content("second")).Outcome);
        Assert.Equal(WriteOutcome.Written, theirs.Replace(TheirRecord, 2, Content("third")).Outcome);
        File.Delete(DocumentPath(Record));
        var listed = ListTheirs(copy);
        Assert.Equal(new OutsideChange(TheirRecord, InTrash: false, OutsideReason.Gone, LastGood: 1), listed);

        Assert.Equal(WriteOutcome.Behind, copy.WriteBack(listed).Outcome);

        Assert.False(File.Exists(DocumentPath(Record)), "A copy that is behind wrote the document back.");
        Assert.Equal(listed, Assert.Single(copy.ToRestore()));
    }

    /// <summary>
    /// A private folder of another user that has gone whole is not made again by a write back: the Kiler of the owner would
    /// take a root of the admin only once accepted, and then refuse every folder that it makes in it (M2.7d). The write back
    /// is a <see cref="DirectoryNotFoundException"/>, as for the other writes. The Kiler of the owner makes the folder again
    /// (<see cref="DataCopy.CreatePrivate"/>), which the write back refuses (<see cref="ForeignFolderException"/>) until the
    /// admin accepts it (<see cref="DataCopy.AcceptPrivateFolder"/>); then the documents come back into it, and the Kiler of
    /// the owner takes them (decided by the user).
    /// </summary>
    [Fact]
    public void WriteBack_IntoAPrivateFolderOfAnotherUserThatHasGone_Throws_AndComesBackOnceTheKilerOfItsOwnerMakesItAgain()
    {
        var theirs = OpenTheirs();
        theirs.CreatePrivate();
        Assert.Equal(WriteOutcome.Written, theirs.Create(TheirRecord, Content("record")).Outcome);
        theirs.Load();
        var copy = OpenAdmin();
        var good = File.ReadAllBytes(DocumentPath(Record));
        Directory.Delete(_root, recursive: true);
        var listed = ListTheirs(copy);
        Assert.Equal(new OutsideChange(TheirRecord, InTrash: false, OutsideReason.Gone, LastGood: 1), listed);

        Assert.Throws<DirectoryNotFoundException>(() => copy.WriteBack(listed));

        Assert.False(Directory.Exists(_root), "The write back made the private folder of another user.");
        theirs.CreatePrivate();
        Assert.Equal(new SkippedEntry(@"private\demo.other", null, SkipReason.ForeignFolder), Assert.Single(LoadAMinuteLater(copy).Skipped));
        Assert.Throws<ForeignFolderException>(() => copy.WriteBack(listed));
        Assert.False(File.Exists(DocumentPath(Record)), "The write back went into a private folder that the copy has not accepted.");
        copy.AcceptPrivateFolder(Other);
        Assert.Empty(copy.Load().Skipped);
        Assert.Equal(listed, Assert.Single(copy.ToRestore()));

        Assert.Equal(WriteOutcome.Written, copy.WriteBack(listed).Outcome);

        Assert.Equal(good, File.ReadAllBytes(DocumentPath(Record)));
        AssertEveryFolderIsTheirs();
        Assert.Empty(theirs.Load().Skipped);
        Assert.Equal("record", Name(Assert.Single(theirs.Documents())));
        Assert.Equal(WriteOutcome.Written, theirs.Replace(TheirRecord, 1, Content("again")).Outcome);
        AssertEveryFolderIsTheirs();
    }

    private static JsonObject Content(string name) => new() { ["name"] = name };

    private static string? Name(LoadedDocument document) => (string?)document.Read.Document?.Content["name"];

    private static OutsideReason ReasonOf(Change change) => change switch
    {
        Change.Gone or Change.GoneFromTheTrash => OutsideReason.Gone,
        Change.Damaged => OutsideReason.Damaged,
        Change.EditedInPlace => OutsideReason.EditedInPlace,
        _ => OutsideReason.OutOfOrder,
    };

    /// <summary>Changes the file of <see cref="Record"/> at version 2 outside Kiler, as <paramref name="change"/> says.</summary>
    private void MakeChange(string path, Change change)
    {
        switch (change)
        {
            case Change.Gone:
            case Change.GoneFromTheTrash:
                File.Delete(path);
                break;
            case Change.Damaged:
                File.WriteAllText(path, "{", Encoding.UTF8);
                break;
            case Change.EditedInPlace:
                TestFolder.EditInPlace(path, DocumentFormat.Next(Record, 1, Content("edited by hand")));
                break;
            default:
                TestFolder.WriteAsKiler(path, File.ReadAllBytes(HistoryPath(Record, 1)));
                break;
        }
    }

    /// <summary>The copy of the owner of the private folder, <see cref="Other"/>, on its own file system, in a folder of its own, as on the owner's machine.</summary>
    private DataCopy OpenTheirs() =>
        DataCopy.Open(_theirShare, Directory.CreateDirectory(Path.Combine(_test.Root, "their copy")).FullName, _clock, CopyLimits.Default, Other);

    /// <summary>The copy of the admin, <see cref="Me"/>, given the private folder of <see cref="Other"/> (M2.7c), and loaded once.</summary>
    private DataCopy OpenAdmin()
    {
        var copy = DataCopy.Open(_share, Directory.CreateDirectory(Path.Combine(_test.Root, "copy")).FullName, _clock, CopyLimits.Default, Me);
        copy.CopyOthers([Other]);
        copy.Load();
        return copy;
    }

    /// <summary>Loads the copy of the admin on <see cref="_clock"/> a minute later, when it takes the private folders of other users again (<see cref="DataCopy.OthersInterval"/>, M2.8c).</summary>
    private LoadResult LoadAMinuteLater(DataCopy copy)
    {
        _clock.Advance(DataCopy.OthersInterval);
        return copy.Load();
    }

    /// <summary>
    /// Loads the copy of the admin twice, a minute apart, so it reports what changed outside Kiler, also a document gone or
    /// edited in place, which it reports once that has settled (M2.7a), and gives the one change that it lists.
    /// </summary>
    private OutsideChange ListTheirs(DataCopy copy)
    {
        LoadAMinuteLater(copy);
        LoadAMinuteLater(copy);
        return Assert.Single(copy.ToRestore());
    }

    /// <summary>Asserts that every folder of the private folder of <see cref="Other"/>, its root too, is the owner's, so the Kiler of the owner refuses none (M2.7d).</summary>
    private void AssertEveryFolderIsTheirs()
    {
        string[] folders = [_root, .. Directory.GetDirectories(_root, "*", SearchOption.AllDirectories)];
        foreach (var folder in folders)
        {
            using var held = PinnedFolder.Open(folder, readOwner: true);
            Assert.True(_files.Owners.Of(held.Handle) == TestOwners.Other, $"A folder of the private folder of another user is not the owner's: {folder}");
        }
    }

    /// <summary>The owner of a file, read through the handle that reads it, as the layer reads it.</summary>
    private string? OwnerOfFile(string path)
    {
        using var stream = _files.OpenRead(path) ?? throw new FileNotFoundException("No file to read the owner of.", path);
        return _files.Owner(stream);
    }

    private string DocumentPath(DocumentKey key) => Path.Combine(_root, key.Folder ?? "", key.Id + ".json");

    private string TrashPath(DocumentKey key) => Path.Combine(_root, DataFolder.Trash, key.Folder ?? "", key.Id + ".json");

    private string HistoryPath(DocumentKey key, int version) => Path.Combine(_root, DataFolder.History, key.Folder ?? "", key.Id, $"v{version}.json");
}

using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Time.Testing;

namespace Kiler.Storage.Tests;

/// <summary>
/// Writing back from the local copy over a file that is there (M2.9b, PLAN M2 step 10): a document that is damaged, edited
/// in place or at a version out of order, in its folder or in the trash, gets its last good bytes back as they are, at their
/// version, through a write of its own (<see cref="DataCopy.WriteBack"/>), and the copy follows it. The file that it
/// replaces is not kept (decided by the user).
/// </summary>
public sealed class DataCopyWriteBackOverTests : IDisposable
{
    private const string Me = "demo.user";

    private static readonly DocumentKey Record = DocumentKey.InFolder(DataFolder.Records, "8f3a1c");

    private readonly TestFolder _test = new();
    private readonly TestFileSystem _files = new();
    private readonly FakeTimeProvider _clock = new(DateTimeOffset.UtcNow);
    private readonly DataFolder _share;
    private readonly string _copy;

    public DataCopyWriteBackOverTests()
    {
        _share = DataFolder.Create(Path.Combine(_test.Root, "data"), _files);
        _copy = Directory.CreateDirectory(Path.Combine(_test.Root, "copy")).FullName;
    }

    /// <summary>How the file of a document at version 2 is changed outside Kiler.</summary>
    public enum Change
    {
        Damaged,
        TooLarge,

        /// <summary>Edited in place at its version.</summary>
        EditedInPlace,

        /// <summary>Put back at version 1 from its history slot.</summary>
        OutOfOrder,
    }

    /// <summary>What happened to the file of a document since the list gave it.</summary>
    public enum Since
    {
        NewVersion,
        EditedAgain,
        Fixed,
        Gone,
        FolderGone,
        FolderInstead,
        LinkInstead,
    }

    /// <summary>A slot of the version of the file there with its bytes, as a write of another Kiler of that version leaves it.</summary>
    public enum Leftover
    {
        UnderWay,
        Young,
        Old,

        /// <summary>Old, and another write makes its slot between the takeover and the lock.</summary>
        OldThenAnotherWrite,
    }

    /// <summary>How the name of the file there stays taken after the write back deleted it.</summary>
    public enum Taken
    {
        Once,
        RefusedOnce,
        Always,
    }

    /// <summary>How the history of the share shows that the file there is not to be written over.</summary>
    public enum Ahead
    {
        /// <summary>Versions after the last good one, which the copy did not see.</summary>
        SlotAfter,

        /// <summary>The same, in a listing that gives no sizes, so each slot looks empty, as a dead lock is.</summary>
        SlotAfterListedWithNoSize,

        /// <summary>A slot of the last good version with other bytes than both, which blocks it for good (M2.4).</summary>
        SlotWithOtherBytes,

        /// <summary>A slot of the last good version with the bytes of the file there, which is at a later version.</summary>
        SlotWithTheBytesOfALaterFile,

        FolderAtTheSlot,
        LinkAtTheSlot,
    }

    public void Dispose() => _test.Dispose();

    /// <summary>
    /// The last good bytes go back as they are, at their version (decided in M2.9a, by the user), over the file that is there,
    /// which goes (decided in M2.9b, by the user), and the copy follows the write: the next load reads the file back, finds it
    /// good, and reports nothing. The history of the share stays as it was.
    /// </summary>
    [Theory]
    [InlineData(Change.Damaged, false)]
    [InlineData(Change.TooLarge, false)]
    [InlineData(Change.EditedInPlace, false)]
    [InlineData(Change.OutOfOrder, false)]
    [InlineData(Change.Damaged, true)]
    [InlineData(Change.EditedInPlace, true)]
    [InlineData(Change.OutOfOrder, true)]
    public void WriteBack_ADocumentChangedOutsideKiler_ComesBackAsItsLastGoodVersion_AndTheNextLoadTakesItAsGood(Change change, bool inTrash)
    {
        Create(Record, "first");
        Replace(Record, 1, "second");
        if (inTrash)
        {
            Assert.Equal(WriteOutcome.Deleted, _share.DeleteDocument(Record, 2).Outcome);
        }

        var copy = OpenAt();
        copy.Load();
        var path = inTrash ? _share.TrashPath(Record) : _share.DocumentPath(Record);
        var good = File.ReadAllBytes(path);
        var history = SlotBytesOnShare(Record);
        MakeChange(path, change);
        var listed = List(copy);
        Assert.Equal(new OutsideChange(Record, inTrash, ReasonOf(change), LastGood: 2), listed);

        var result = copy.WriteBack(listed);

        Assert.Equal(WriteOutcome.Written, result.Outcome);
        Assert.Equal(2, result.Version);
        Assert.Equal(good, File.ReadAllBytes(path));
        Assert.Equal(history, SlotBytesOnShare(Record));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp"));
        Assert.Empty(copy.ToRestore());
        Assert.Empty(FilesUnder(Path.Combine(_copy, "kept")));
        Assert.Equal("second", Name(Assert.Single(inTrash ? copy.Trash() : copy.Documents())));
        Assert.Empty(copy.Load().Outside);
        _clock.Advance(InPlaceEdits.SettleTime);
        Assert.Empty(copy.Load().Outside);
        Assert.Empty(copy.ToRestore());
        Assert.Equal(2, Assert.Single(inTrash ? OpenAt().Trash() : OpenAt().Documents()).Read.Document?.Version);
    }

    [Fact]
    public void WriteBack_ADocumentOfThePrivateFolderOfTheUser_ComesBack()
    {
        var mine = _share.CreatePrivate(Me);
        Assert.Equal(WriteOutcome.Written, mine.CreateDocument(Record, Content("mine")).Outcome);
        var copy = OpenAt(Me);
        copy.Load();
        var path = mine.DocumentPath(Record);
        var good = File.ReadAllBytes(path);
        TestFolder.EditInPlace(path, DocumentFormat.New(Record, Content("edited by hand")));
        var listed = List(copy);
        Assert.Equal(Record.InPrivate(Me), listed.Key);

        Assert.Equal(WriteOutcome.Written, copy.WriteBack(listed).Outcome);

        Assert.Equal(good, File.ReadAllBytes(path));
        var load = copy.Load();
        Assert.Empty(load.Outside);
        Assert.Empty(load.Skipped);
        Assert.Equal("mine", Name(Assert.Single(copy.Documents())));
    }

    /// <summary>
    /// The version of the file there does not count, as it is a change outside Kiler (decided by the user): a document whose
    /// version someone set to the last one, which has no version after it (M2.2), comes back at its last good version and can
    /// be written again.
    /// </summary>
    [Fact]
    public void WriteBack_ADocumentEditedInPlaceToTheLastVersion_ComesBackAtItsLastGoodVersion_AndCanBeWrittenAgain()
    {
        Create(Record, "record");
        var copy = OpenAt();
        copy.Load();
        var path = _share.DocumentPath(Record);
        var good = File.ReadAllBytes(path);
        TestFolder.EditInPlace(path, DocumentFormat.Next(Record, int.MaxValue - 1, Content("edited by hand")));
        var listed = List(copy);
        Assert.Equal(new OutsideChange(Record, InTrash: false, OutsideReason.EditedInPlace, LastGood: 1), listed);
        Assert.Equal(WriteOutcome.NoNextVersion, copy.Replace(Record, int.MaxValue, Content("next")).Outcome);

        Assert.Equal(WriteOutcome.Written, copy.WriteBack(listed).Outcome);

        Assert.Equal(good, File.ReadAllBytes(path));
        Assert.Empty(SlotBytesOnShare(Record));
        Assert.Equal(WriteOutcome.Written, copy.Replace(Record, 1, Content("next")).Outcome);
        Assert.Equal("v1.json", Assert.Single(SlotBytesOnShare(Record)).Key);
    }

    /// <summary>A document whose last good version is the last one comes back at it, and its writes give the defined result of M2.2.</summary>
    [Fact]
    public void WriteBack_ADamagedDocumentWhoseLastGoodVersionIsTheLast_ComesBackAtIt_AndHasNoNextVersion()
    {
        var path = _share.DocumentPath(Record);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var good = DocumentFormat.Next(Record, int.MaxValue - 1, Content("last"));
        TestFolder.WriteAsKiler(path, good);
        var copy = OpenAt();
        copy.Load();
        File.WriteAllText(path, "{", Encoding.UTF8);
        var listed = List(copy);
        Assert.Equal(new OutsideChange(Record, InTrash: false, OutsideReason.Damaged, LastGood: int.MaxValue), listed);

        var result = copy.WriteBack(listed);

        Assert.Equal(WriteOutcome.Written, result.Outcome);
        Assert.Equal(int.MaxValue, result.Version);
        Assert.Equal(good, File.ReadAllBytes(path));
        Assert.Equal(WriteOutcome.NoNextVersion, copy.Replace(Record, int.MaxValue, Content("next")).Outcome);
    }

    /// <summary>
    /// A slot with other bytes than the document at its version blocks every write of that version (M2.4), as after a crash
    /// that left the slot and an edit in place. The write back gives the document the bytes of the slot again, so the slot
    /// is a leftover that the next write takes over once it is old; the slots stay as they are (decided in M2.9a).
    /// </summary>
    [Fact]
    public void WriteBack_OverAnEditInPlaceAfterACrashLeftTheSlotOfItsVersion_MakesTheSlotALeftoverThatTheNextWriteTakesOver()
    {
        Create(Record, "record");
        var copy = OpenAt();
        copy.Load();
        var path = _share.DocumentPath(Record);
        var good = File.ReadAllBytes(path);
        var slot = _share.HistoryPath(Record, 1);
        Directory.CreateDirectory(Path.GetDirectoryName(slot)!);
        TestFolder.WriteAsKiler(slot, good);
        File.SetLastWriteTimeUtc(slot, DateTime.UtcNow - DataFolder.SlotAge - TimeSpan.FromMinutes(1));
        TestFolder.EditInPlace(path, DocumentFormat.New(Record, Content("edited by hand")));
        var listed = List(copy);
        Assert.Equal(WriteOutcome.Conflict, copy.Replace(Record, 1, Content("blocked")).Outcome);

        Assert.Equal(WriteOutcome.Written, copy.WriteBack(listed).Outcome);

        Assert.Equal(good, File.ReadAllBytes(path));
        Assert.Equal(good, File.ReadAllBytes(slot));
        Assert.Equal(WriteOutcome.Written, copy.Replace(Record, 1, Content("next")).Outcome);
    }

    /// <summary>Every copy that has the same last good bytes takes them as good again, so its list clears too (decided in M2.9a, by the user).</summary>
    [Fact]
    public void WriteBack_AnotherCopyWithTheSameLastGoodBytes_TakesTheDocumentAsGoodAgain()
    {
        Create(Record, "record");
        var copy = OpenAt();
        var other = OpenAnother();
        copy.Load();
        other.Load();
        TestFolder.EditInPlace(_share.DocumentPath(Record), DocumentFormat.New(Record, Content("edited by hand")));
        LoadBoth(copy, other);
        var listed = Assert.Single(copy.ToRestore());
        Assert.Equal(listed, Assert.Single(other.ToRestore()));

        Assert.Equal(WriteOutcome.Written, copy.WriteBack(listed).Outcome);

        Assert.Empty(other.Load().Outside);
        Assert.Empty(other.ToRestore());
        Assert.Equal("record", Name(Assert.Single(other.Documents())));
    }

    /// <summary>
    /// The file there is written over only while it is the one that the list gave (M2.9b): a new version that another Kiler
    /// wrote over the edit since, another edit, a damaged file put right, a file gone or a folder at its name is not, and
    /// nothing is written.
    /// </summary>
    [Theory]
    [InlineData(Since.NewVersion)]
    [InlineData(Since.EditedAgain)]
    [InlineData(Since.Fixed)]
    [InlineData(Since.Gone)]
    [InlineData(Since.FolderGone)]
    [InlineData(Since.FolderInstead)]
    [InlineData(Since.LinkInstead)]
    public void WriteBack_WhenTheFileThereIsNoLongerTheOneTheListGave_WritesNothing(Since since)
    {
        Create(Record, "record");
        var copy = OpenAt();
        copy.Load();
        var path = _share.DocumentPath(Record);
        var good = File.ReadAllBytes(path);
        if (since == Since.Fixed)
        {
            File.WriteAllText(path, "{", Encoding.UTF8);
        }
        else
        {
            TestFolder.EditInPlace(path, DocumentFormat.New(Record, Content("edited by hand")));
        }

        var listed = List(copy);
        switch (since)
        {
            case Since.NewVersion:
                Assert.Equal(WriteOutcome.Written, _share.ReplaceDocument(Record, 1, Content("saved by another user")).Outcome);
                break;
            case Since.EditedAgain:
                TestFolder.EditInPlace(path, DocumentFormat.New(Record, Content("edited again")));
                break;
            case Since.Fixed:
                TestFolder.WriteAsKiler(path, DocumentFormat.New(Record, Content("put right by hand")));
                break;
            case Since.Gone:
                File.Delete(path);
                break;
            case Since.FolderGone:
                Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
                break;
            case Since.LinkInstead:
                var target = Path.Combine(_test.Root, "elsewhere.json");
                File.Move(path, target);
                TestFolder.CreateFileLink(path, target);
                break;
            default:
                File.Delete(path);
                Directory.CreateDirectory(path);
                break;
        }

        var there = File.Exists(path) ? File.ReadAllBytes(path) : null;

        Assert.Equal(WriteOutcome.NotListed, copy.WriteBack(listed).Outcome);

        Assert.Equal(there, File.Exists(path) ? File.ReadAllBytes(path) : null);
        Assert.NotEqual(good, there);
        Assert.Equal(since != Since.FolderGone, Directory.Exists(Path.GetDirectoryName(path)));
        Assert.DoesNotContain(FilesUnder(Path.GetDirectoryName(path)!), file => file.EndsWith(".tmp", StringComparison.Ordinal));
        Assert.Equal(listed, Assert.Single(copy.ToRestore()));
    }

    /// <summary>
    /// A copy that did not see the newer versions of the share writes nothing back over the file there either (M2.9a). Nor
    /// over a slot of the last good version with other bytes than both, which blocks the writes of that version for good
    /// (the M2.4 line; only one with the last good bytes is cleared). A folder or a link at the slot of the version of the
    /// file there is no write of it under way, so it counts as for any slot.
    /// </summary>
    [Theory]
    [InlineData(Ahead.SlotAfter)]
    [InlineData(Ahead.SlotAfterListedWithNoSize)]
    [InlineData(Ahead.SlotWithOtherBytes)]
    [InlineData(Ahead.SlotWithTheBytesOfALaterFile)]
    [InlineData(Ahead.FolderAtTheSlot)]
    [InlineData(Ahead.LinkAtTheSlot)]
    public void WriteBack_WhenTheHistoryOfTheShareHasAVersionThatTheCopyDidNotSee_IsRefusedAsBehind(Ahead ahead)
    {
        Create(Record, "first");
        var copy = OpenAt();
        copy.Load();
        var path = _share.DocumentPath(Record);
        var slot = _share.HistoryPath(Record, 1);
        if (ahead is Ahead.SlotAfter or Ahead.SlotAfterListedWithNoSize)
        {
            Replace(Record, 1, "second");
            Replace(Record, 2, "third");
            TestFolder.EditInPlace(path, DocumentFormat.Next(Record, 2, Content("edited by hand")));
        }
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(slot)!);
            TestFolder.EditInPlace(path, ahead == Ahead.SlotWithTheBytesOfALaterFile
                ? DocumentFormat.Next(Record, 1, Content("edited by hand"))
                : DocumentFormat.New(Record, Content("edited by hand")));
        }

        switch (ahead)
        {
            case Ahead.SlotWithOtherBytes:
                TestFolder.WriteAsKiler(slot, DocumentFormat.New(Record, Content("edited elsewhere")));
                break;
            case Ahead.SlotWithTheBytesOfALaterFile:
                TestFolder.WriteAsKiler(slot, File.ReadAllBytes(path));
                break;
            case Ahead.FolderAtTheSlot:
                Directory.CreateDirectory(slot);
                break;
            case Ahead.LinkAtTheSlot:
                var target = Path.Combine(_test.Root, "elsewhere.json");
                File.Copy(path, target);
                TestFolder.CreateFileLink(slot, target);
                break;
        }

        var edited = File.ReadAllBytes(path);
        var listed = List(copy);
        Assert.Equal(new OutsideChange(Record, InTrash: false, OutsideReason.EditedInPlace, LastGood: 1), listed);
        var history = Directory.GetFileSystemEntries(Path.GetDirectoryName(slot)!).Order(StringComparer.Ordinal).ToList();
        var slots = ahead is Ahead.FolderAtTheSlot or Ahead.LinkAtTheSlot ? null : SlotBytesOnShare(Record);
        _files.ListsNoSizes = ahead == Ahead.SlotAfterListedWithNoSize;

        Assert.Equal(WriteOutcome.Behind, copy.WriteBack(listed).Outcome);

        Assert.Equal(edited, File.ReadAllBytes(path));
        Assert.Equal(history, Directory.GetFileSystemEntries(Path.GetDirectoryName(slot)!).Order(StringComparer.Ordinal));
        if (slots is not null)
        {
            Assert.Equal(slots, SlotBytesOnShare(Record));
        }

        Assert.Equal(listed, Assert.Single(copy.ToRestore()));
    }

    /// <summary>
    /// A document in the trash is not locked by the slot of its version, as only a restore moves it, and the hold stops that:
    /// so the slot that the delete of another Kiler made of the edited file in its folder counts as any slot of the version
    /// with other bytes, which a restore from the trash would leave in the way of every write (M2.4, M2.9a).
    /// </summary>
    [Fact]
    public void WriteBack_InTheTrash_AfterAnotherKilerDeletedTheEditedDocument_IsRefusedAsBehind()
    {
        Create(Record, "record");
        var copy = OpenAt();
        copy.Load();
        TestFolder.EditInPlace(_share.DocumentPath(Record), DocumentFormat.New(Record, Content("edited by hand")));
        List(copy);
        Assert.Equal(WriteOutcome.Deleted, _share.DeleteDocument(Record, 1).Outcome);
        var edited = File.ReadAllBytes(_share.TrashPath(Record));
        var listed = List(copy);
        Assert.Equal(new OutsideChange(Record, InTrash: true, OutsideReason.EditedInPlace, LastGood: 1), listed);
        File.SetLastWriteTimeUtc(_share.HistoryPath(Record, 1), DateTime.UtcNow - DataFolder.SlotAge - TimeSpan.FromMinutes(1));

        Assert.Equal(WriteOutcome.Behind, copy.WriteBack(listed).Outcome);

        Assert.Equal(edited, File.ReadAllBytes(_share.TrashPath(Record)));
        Assert.Equal(edited, File.ReadAllBytes(_share.HistoryPath(Record, 1)));
    }

    /// <summary>A read-only file there may not be deleted, so it stays, and nothing of the write back is left.</summary>
    [Fact]
    public void WriteBack_WhenTheFileThereIsReadOnly_IsBusy_AndLeavesIt()
    {
        Create(Record, "record");
        var copy = OpenAt();
        copy.Load();
        var path = _share.DocumentPath(Record);
        TestFolder.EditInPlace(path, DocumentFormat.New(Record, Content("edited by hand")));
        var edited = File.ReadAllBytes(path);
        var listed = List(copy);
        File.SetAttributes(path, FileAttributes.ReadOnly);
        try
        {
            Assert.Equal(WriteOutcome.Busy, copy.WriteBack(listed).Outcome);

            Assert.Equal(edited, File.ReadAllBytes(path));
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp"));
            Assert.Empty(SlotBytesOnShare(Record));
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }
    }

    /// <summary>
    /// The lock is a marker, which goes when it is let go, also when the process ends or the file server ends its session
    /// (<see cref="IFileSystem.CreateMarker"/>), so a write back that stops at any step leaves no lock (security review of
    /// M2.9b). Here the file there is at a version after the last good one, where a lock left at the name of its slot would
    /// block the document for good: before the delete, the write back is tried again; after it, the document is gone and is
    /// written back as a gone one, as decided; after the rename, its next writes go through.
    /// </summary>
    [Theory]
    [InlineData(nameof(IFileSystem.DeleteHeldFile))]
    [InlineData(nameof(IFileSystem.MoveFile))]
    [InlineData(nameof(IFileSystem.SetCreationToWriteTime))]
    public void WriteBack_StoppedAtAnyStep_LeavesNoLock(string stopAt)
    {
        Create(Record, "record");
        var copy = OpenAt();
        copy.Load();
        var path = _share.DocumentPath(Record);
        var good = File.ReadAllBytes(path);
        TestFolder.EditInPlace(path, DocumentFormat.Next(Record, 1, Content("edited by hand")));
        var listed = List(copy);
        Assert.Equal(new OutsideChange(Record, InTrash: false, OutsideReason.EditedInPlace, LastGood: 1), listed);
        var records = Path.GetDirectoryName(path)!;
        var times = 0;
        _files.CrashAt = (step, at) =>
            step == stopAt
            && (step == nameof(IFileSystem.DeleteHeldFile) || string.Equals(Path.GetDirectoryName(at), records, StringComparison.OrdinalIgnoreCase))
            && (step != nameof(IFileSystem.SetCreationToWriteTime) || ++times == 2);

        Assert.ThrowsAny<IOException>(() => copy.WriteBack(listed));

        Assert.True(_files.Crashed, "The write back did not stop.");
        Assert.Empty(SlotBytesOnShare(Record));
        var share = DataFolder.Open(_share.Root, new TestFileSystem());
        var reopened = DataCopy.Open(share, _copy, _clock, CopyLimits.Default);
        if (stopAt == nameof(IFileSystem.DeleteHeldFile))
        {
            Assert.Equal(WriteOutcome.Written, reopened.WriteBack(Assert.Single(reopened.ToRestore())).Outcome);
        }
        else if (stopAt == nameof(IFileSystem.MoveFile))
        {
            Assert.False(File.Exists(path), "The file there was not deleted.");
            reopened.Load();
            _clock.Advance(InPlaceEdits.SettleTime);
            reopened.Load();
            var gone = Assert.Single(reopened.ToRestore());
            Assert.Equal(new OutsideChange(Record, InTrash: false, OutsideReason.Gone, LastGood: 1), gone);
            Assert.Equal(WriteOutcome.Written, reopened.WriteBack(gone).Outcome);
        }

        Assert.Equal(good, File.ReadAllBytes(path));
        reopened.Load();
        Assert.Empty(reopened.ToRestore());
        Assert.Equal(WriteOutcome.Written, reopened.Replace(Record, 1, Content("second")).Outcome);
        Assert.Equal(WriteOutcome.Written, reopened.Replace(Record, 2, Content("third")).Outcome);
    }

    /// <summary>
    /// A crash of the machine that holds the files loses the delete-on-close of a lock, so its marker stays as an empty file
    /// at the name of the slot (second security review of M2.9b). A slot that Kiler writes is never empty, and a live lock
    /// cannot be held, so the write back takes such a file over as a dead lock once it is <see cref="DataFolder.SlotAge"/>
    /// old, at the version written back or after it, and the history does not count it as a version.
    /// </summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void WriteBack_OverAFileWhoseLockACrashOfTheFileServerLeft_TakesItOverOnceItIsOld(bool later, bool old)
    {
        Create(Record, "record");
        var copy = OpenAt();
        copy.Load();
        var path = _share.DocumentPath(Record);
        var good = File.ReadAllBytes(path);
        TestFolder.EditInPlace(path, later ? DocumentFormat.Next(Record, 1, Content("edited by hand")) : DocumentFormat.New(Record, Content("edited by hand")));
        var edited = File.ReadAllBytes(path);
        var listed = List(copy);
        var slot = LeaveDeadLock(Record, later ? 2 : 1, old);

        var result = copy.WriteBack(listed);

        if (old)
        {
            Assert.Equal(WriteOutcome.Written, result.Outcome);
            Assert.Equal(good, File.ReadAllBytes(path));
            Assert.False(File.Exists(slot), "The dead lock stayed.");
        }
        else
        {
            Assert.Equal(WriteOutcome.Busy, result.Outcome);
            Assert.Equal(edited, File.ReadAllBytes(path));
            Assert.Empty(File.ReadAllBytes(slot));
        }

        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp"));
    }

    /// <summary>
    /// A dead lock left after the delete of the file there leaves the document gone (decided by the user): the write back of
    /// the gone document does not count it as a version, and the writes of its version take it over once it is old.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WriteBack_OfADocumentGoneWithADeadLockOfItsWriteBack_ComesBack_AndItsWritesTakeTheLockOver(bool later)
    {
        Create(Record, "record");
        var copy = OpenAt();
        copy.Load();
        var path = _share.DocumentPath(Record);
        var good = File.ReadAllBytes(path);
        File.Delete(path);
        LeaveDeadLock(Record, later ? 2 : 1, old: true);
        var gone = List(copy);
        Assert.Equal(new OutsideChange(Record, InTrash: false, OutsideReason.Gone, LastGood: 1), gone);

        Assert.Equal(WriteOutcome.Written, copy.WriteBack(gone).Outcome);

        Assert.Equal(good, File.ReadAllBytes(path));
        Assert.Equal(WriteOutcome.Written, copy.Replace(Record, 1, Content("second")).Outcome);
        Assert.Equal(WriteOutcome.Written, copy.Replace(Record, 2, Content("third")).Outcome);
        Assert.Equal(["v1.json", "v2.json"], SlotBytesOnShare(Record).Keys.ToArray());
        Assert.NotEmpty(SlotBytesOnShare(Record)["v2.json"]);
    }

    /// <summary>
    /// Every write of the version of a dead lock takes it over once it is old, as a leftover of a crash (M2.4), without the
    /// checks of the bytes, as no write makes an empty slot; while it is young, the write is refused as for a write under way.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AWriteOfTheVersionOfADeadLock_TakesItOver_OnceItIsOld(bool old)
    {
        Create(Record, "record");
        var slot = LeaveDeadLock(Record, 1, old);

        var replaced = _share.ReplaceDocument(Record, 1, Content("next"));

        Assert.Equal(old ? WriteOutcome.Written : WriteOutcome.Conflict, replaced.Outcome);
        Assert.Equal(old, File.ReadAllBytes(slot).Length > 0);
    }

    /// <summary>
    /// A write that read the document before it changed, as before a write back over it, does not take a dead lock over: the
    /// document is checked as for any slot, so the write meets a conflict and does not replace what came since (code review
    /// of M2.9b).
    /// </summary>
    [Fact]
    public void AWriteOfTheVersionOfADeadLock_WhoseDocumentChangedSinceItsRead_DoesNotTakeItOver()
    {
        Create(Record, "record");
        var path = _share.DocumentPath(Record);
        var records = Path.GetDirectoryName(path)!;
        var slot = LeaveDeadLock(Record, 1, old: true);
        var since = DocumentFormat.New(Record, Content("changed since"));
        var changed = false;
        _files.Before = (step, at) =>
        {
            if (step == nameof(IFileSystem.CreateFile) && !changed && string.Equals(Path.GetDirectoryName(at), records, StringComparison.OrdinalIgnoreCase))
            {
                changed = true;
                TestFolder.WriteAsKiler(path, since);
            }
        };

        Assert.Equal(WriteOutcome.Conflict, _share.ReplaceDocument(Record, 1, Content("next")).Outcome);

        Assert.True(changed, "The document did not change.");
        Assert.Equal(since, File.ReadAllBytes(path));
        Assert.Empty(File.ReadAllBytes(slot));
    }

    /// <summary>
    /// Over a file there at a version after the last good one, a slot of that version counts as one that the copy did not
    /// see, also with the bytes of the file there and old: someone can have put it back over the document, and a takeover
    /// would delete it from the history (security review of M2.9b).
    /// </summary>
    [Fact]
    public void WriteBack_OverAFileAtALaterVersion_WhoseSlotHasItsBytes_IsRefusedAsBehind_AndKeepsTheSlot()
    {
        Create(Record, "record");
        var copy = OpenAt();
        copy.Load();
        var path = _share.DocumentPath(Record);
        TestFolder.EditInPlace(path, DocumentFormat.Next(Record, 1, Content("edited by hand")));
        var edited = File.ReadAllBytes(path);
        var listed = List(copy);
        var slot = _share.HistoryPath(Record, 2);
        Directory.CreateDirectory(Path.GetDirectoryName(slot)!);
        TestFolder.WriteAsKiler(slot, edited);
        File.SetLastWriteTimeUtc(slot, DateTime.UtcNow - DataFolder.SlotAge - TimeSpan.FromMinutes(1));

        Assert.Equal(WriteOutcome.Behind, copy.WriteBack(listed).Outcome);

        Assert.Equal(edited, File.ReadAllBytes(path));
        Assert.Equal(edited, File.ReadAllBytes(slot));
    }

    /// <summary>The bytes written back are those of a good document of the key, which the copy checks by their hash first; the layer refuses others too.</summary>
    [Fact]
    public void WriteBack_OfBytesThatAreNotAGoodDocument_IsRefusedBeforeAnythingIsLookedAt()
    {
        Create(Record, "record");
        var path = _share.DocumentPath(Record);
        var bytes = File.ReadAllBytes(path);
        var damaged = Encoding.UTF8.GetBytes("{");
        var reads = _files.Reads;

        Assert.Throws<ArgumentException>(() => _share.WriteBackOver(Record, inTrash: false, damaged, 100, _ => true, makeFolders: true));
        Assert.Throws<ArgumentException>(() => _share.WriteBackOver(Record, inTrash: false, DocumentFormat.New(DocumentKey.InFolder(DataFolder.Records, "b72e09"), Content("other")), 100, _ => true, makeFolders: true));
        Assert.Throws<ArgumentException>(() => _share.WriteBack(Record, inTrash: false, damaged, 100, makeFolders: true));
        Assert.Throws<ArgumentNullException>(() => _share.WriteBackOver(Record, inTrash: false, bytes, 100, null!, makeFolders: true));

        Assert.Equal(reads, _files.Reads);
        Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    /// <summary>A file there that another program holds open without sharing the delete cannot be held, so the write back can be tried again.</summary>
    [Fact]
    public void WriteBack_WhenTheFileThereIsHeldOpen_IsBusy_AndCanBeTriedAgain()
    {
        Create(Record, "record");
        var copy = OpenAt();
        copy.Load();
        var path = _share.DocumentPath(Record);
        var good = File.ReadAllBytes(path);
        TestFolder.EditInPlace(path, DocumentFormat.New(Record, Content("edited by hand")));
        var edited = File.ReadAllBytes(path);
        var listed = List(copy);

        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.Equal(WriteOutcome.Busy, copy.WriteBack(listed).Outcome);
        }

        Assert.Equal(edited, File.ReadAllBytes(path));
        Assert.Empty(SlotBytesOnShare(Record));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp"));
        Assert.Equal(WriteOutcome.Written, copy.WriteBack(listed).Outcome);
        Assert.Equal(good, File.ReadAllBytes(path));
    }

    /// <summary>
    /// A write of another Kiler of the version of the file there, while the write back runs, is refused and leaves nothing
    /// (decided by the user): at a version that is not before the one written back, the write back holds a marker at the name
    /// of the slot of that version, which every write of it takes first (M2.4), so the other write meets it; at an older one,
    /// the hold of the file stops its rename or its move to the trash, and it deletes its own slot. The fake waits nothing, so
    /// the other write gives up while the hold lasts; a real one tries for about 1.6 s, and one done after the write back acts
    /// on the restored file (the note of the task).
    /// </summary>
    [Theory]
    [InlineData(Change.EditedInPlace, false)]
    [InlineData(Change.EditedInPlace, true)]
    [InlineData(Change.OutOfOrder, false)]
    [InlineData(Change.OutOfOrder, true)]
    public void WriteBack_AWriteOfAnotherKilerWhileItRuns_IsRefused_AndLeavesNoSlot(Change change, bool delete)
    {
        Create(Record, "first");
        Replace(Record, 1, "second");
        var copy = OpenAt();
        copy.Load();
        var path = _share.DocumentPath(Record);
        var good = File.ReadAllBytes(path);
        MakeChange(path, change);
        var version = change == Change.EditedInPlace ? 2 : 1;
        if (change == Change.OutOfOrder)
        {
            // The slot of version 1 would stop a write of it anyway, as its bytes are not taken over while they are young.
            File.Delete(_share.HistoryPath(Record, 1));
        }

        var listed = List(copy);
        var history = SlotBytesOnShare(Record);
        WriteResult? other = null;
        _files.Before = (step, at) =>
        {
            if (step == nameof(IFileSystem.DeleteHeldFile) && other is null)
            {
                // The write back holds the file there, and its lock when it takes one.
                other = delete ? _share.DeleteDocument(Record, version) : _share.ReplaceDocument(Record, version, Content("saved by another user"));
            }
        };

        Assert.Equal(WriteOutcome.Written, copy.WriteBack(listed).Outcome);

        Assert.Equal(change == Change.EditedInPlace ? WriteOutcome.Conflict : WriteOutcome.Busy, other?.Outcome);
        Assert.Equal(good, File.ReadAllBytes(path));
        Assert.Equal(history, SlotBytesOnShare(Record));
        Assert.False(File.Exists(_share.TrashPath(Record)), "The document went to the trash.");
    }

    /// <summary>
    /// A slot of the version of the file there with the bytes of that file is a write of another Kiler of that version under
    /// way, or what it left, not a version that the copy did not see: the write back waits for one under way or young, and
    /// takes over an old one, as a write does (M2.4), and none stays.
    /// </summary>
    [Theory]
    [InlineData(Leftover.UnderWay)]
    [InlineData(Leftover.Young)]
    [InlineData(Leftover.Old)]
    [InlineData(Leftover.OldThenAnotherWrite)]
    public void WriteBack_WhenAWriteOfTheVersionOfTheFileThereLeftItsSlot_WaitsForIt_OrTakesItOverOnceItIsOld(Leftover leftover)
    {
        Create(Record, "record");
        var copy = OpenAt();
        copy.Load();
        var path = _share.DocumentPath(Record);
        var good = File.ReadAllBytes(path);
        TestFolder.EditInPlace(path, DocumentFormat.New(Record, Content("edited by hand")));
        var edited = File.ReadAllBytes(path);
        var listed = List(copy);
        var slot = _share.HistoryPath(Record, 1);
        Directory.CreateDirectory(Path.GetDirectoryName(slot)!);
        TestFolder.WriteAsKiler(slot, edited);
        if (leftover is Leftover.Old or Leftover.OldThenAnotherWrite)
        {
            File.SetLastWriteTimeUtc(slot, DateTime.UtcNow - DataFolder.SlotAge - TimeSpan.FromMinutes(1));
        }

        var locks = 0;
        _files.Before = (step, at) =>
        {
            if (leftover == Leftover.OldThenAnotherWrite && step == nameof(IFileSystem.CreateMarker) && string.Equals(at, slot, StringComparison.OrdinalIgnoreCase) && ++locks == 2)
            {
                TestFolder.WriteAsKiler(slot, edited);
            }
        };

        WriteResult result;
        using (leftover == Leftover.UnderWay ? new FileStream(slot, FileMode.Open, FileAccess.ReadWrite, FileShare.Read | FileShare.Delete) : null)
        {
            result = copy.WriteBack(listed);
        }

        if (leftover == Leftover.Old)
        {
            Assert.Equal(WriteOutcome.Written, result.Outcome);
            Assert.Equal(good, File.ReadAllBytes(path));
            Assert.Empty(SlotBytesOnShare(Record));
        }
        else
        {
            Assert.Equal(WriteOutcome.Busy, result.Outcome);
            Assert.Equal(edited, File.ReadAllBytes(path));
            Assert.Equal(edited, File.ReadAllBytes(slot));
            Assert.Single(SlotBytesOnShare(Record));
        }

        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(slot)!, "*.tmp"));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp"));
    }

    /// <summary>
    /// A file system that does not delete through the handle at once keeps the name until the last reader lets go, so the
    /// rename into it is tried again, also when it is refused; when the name stays taken, the write back is no longer as the
    /// list gave it, and the file at the name stays.
    /// </summary>
    [Theory]
    [InlineData(Taken.Once)]
    [InlineData(Taken.RefusedOnce)]
    [InlineData(Taken.Always)]
    public void WriteBack_WhenTheNameStaysTakenAfterTheDelete_TriesTheRenameAgain(Taken taken)
    {
        var staysTaken = taken == Taken.Always;
        Create(Record, "record");
        var copy = OpenAt();
        copy.Load();
        var path = _share.DocumentPath(Record);
        var good = File.ReadAllBytes(path);
        TestFolder.EditInPlace(path, DocumentFormat.New(Record, Content("edited by hand")));
        var listed = List(copy);
        var records = Path.GetDirectoryName(path)!;
        var atTheName = DocumentFormat.New(Record, Content("at the name"));
        var put = false;
        _files.Before = (step, at) =>
        {
            if (step == nameof(IFileSystem.MoveFile) && !put && string.Equals(Path.GetDirectoryName(at), records, StringComparison.OrdinalIgnoreCase))
            {
                put = true;
                if (taken == Taken.RefusedOnce)
                {
                    throw new UnauthorizedAccessException("The name is in the middle of a delete.");
                }

                TestFolder.WriteAsKiler(path, atTheName);
            }
            else if (step == nameof(IFileSystem.Wait) && !staysTaken && File.Exists(path))
            {
                File.Delete(path);
            }
        };

        var result = copy.WriteBack(listed);

        Assert.True(put, "The name was not taken.");
        Assert.Empty(Directory.GetFiles(records, "*.tmp"));
        if (staysTaken)
        {
            Assert.Equal(WriteOutcome.NotListed, result.Outcome);
            Assert.Equal(atTheName, File.ReadAllBytes(path));
            Assert.Equal(DataFolder.RenameWaits, _files.Waits);
        }
        else
        {
            Assert.Equal(WriteOutcome.Written, result.Outcome);
            Assert.Equal(good, File.ReadAllBytes(path));
            Assert.Equal([DataFolder.RenameWaits[0]], _files.Waits);
        }

        Assert.Empty(SlotBytesOnShare(Record));
    }

    /// <summary>
    /// A failure after the file there was deleted leaves the document gone (decided by the user): a load reports it so, and a
    /// write back of a gone document brings it back (M2.9a). Nothing of the write back stays on the share.
    /// </summary>
    [Fact]
    public void WriteBack_ThatFailsAfterTheDelete_LeavesTheDocumentGone_AndItIsWrittenBackAsGone()
    {
        Create(Record, "record");
        var copy = OpenAt();
        copy.Load();
        var path = _share.DocumentPath(Record);
        var good = File.ReadAllBytes(path);
        TestFolder.EditInPlace(path, DocumentFormat.New(Record, Content("edited by hand")));
        var listed = List(copy);
        var records = Path.GetDirectoryName(path)!;
        _files.Before = (step, at) =>
        {
            if (step == nameof(IFileSystem.MoveFile) && string.Equals(Path.GetDirectoryName(at), records, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("The network went away.");
            }
        };

        Assert.Throws<IOException>(() => copy.WriteBack(listed));

        _files.Before = null;
        Assert.False(File.Exists(path), "The file there was not deleted.");
        Assert.Empty(Directory.GetFiles(records, "*.tmp"));
        Assert.Empty(SlotBytesOnShare(Record));
        Assert.Equal(listed, Assert.Single(copy.ToRestore()));
        var gone = List(copy);
        Assert.Equal(new OutsideChange(Record, InTrash: false, OutsideReason.Gone, LastGood: 1), gone);
        Assert.Equal(WriteOutcome.Written, copy.WriteBack(gone).Outcome);
        Assert.Equal(good, File.ReadAllBytes(path));
    }

    /// <summary>
    /// A copy whose bytes of the file there have gone from its data\ does not know that file any more, so it writes nothing
    /// over it until the next load has read it again.
    /// </summary>
    [Fact]
    public void WriteBack_WhenTheCopyHasLostTheBytesOfTheFileThere_IsBusy_UntilTheNextLoad()
    {
        Create(Record, "record");
        var copy = OpenAt();
        copy.Load();
        var path = _share.DocumentPath(Record);
        var good = File.ReadAllBytes(path);
        TestFolder.EditInPlace(path, DocumentFormat.New(Record, Content("edited by hand")));
        var edited = File.ReadAllBytes(path);
        var listed = List(copy);
        File.Delete(CopyPath(Record));
        var reopened = OpenAt();

        Assert.Equal(WriteOutcome.Busy, reopened.WriteBack(listed).Outcome);

        Assert.Equal(edited, File.ReadAllBytes(path));
        reopened.Load();
        Assert.Equal(WriteOutcome.Written, reopened.WriteBack(listed).Outcome);
        Assert.Equal(good, File.ReadAllBytes(path));
    }

    /// <summary>
    /// The copy follows the write back only when it can write the bytes into data\; when not, it stays as it was, and the
    /// next load takes the file of the share, as the last good bytes again are good.
    /// </summary>
    [Fact]
    public void WriteBack_WhoseBytesCannotBeWrittenIntoTheCopy_LeavesTheCopy_AndTheNextLoadTakesTheFile()
    {
        Create(Record, "record");
        var copy = OpenAt();
        copy.Load();
        var path = _share.DocumentPath(Record);
        var good = File.ReadAllBytes(path);
        TestFolder.EditInPlace(path, DocumentFormat.New(Record, Content("edited by hand")));
        var listed = List(copy);

        using (new FileStream(CopyPath(Record), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Equal(WriteOutcome.Written, copy.WriteBack(listed).Outcome);
        }

        Assert.Equal(good, File.ReadAllBytes(path));
        Assert.Equal(listed, Assert.Single(copy.ToRestore()));
        var load = copy.Load();
        Assert.Empty(load.Outside);
        Assert.Empty(copy.ToRestore());
        Assert.Equal("record", Name(Assert.Single(copy.Documents())));
    }

    private static JsonObject Content(string name) => new() { ["name"] = name };

    private static string? Name(LoadedDocument document) => (string?)document.Read.Document?.Content["name"];

    private static OutsideReason ReasonOf(Change change) => change switch
    {
        Change.Damaged or Change.TooLarge => OutsideReason.Damaged,
        Change.EditedInPlace => OutsideReason.EditedInPlace,
        _ => OutsideReason.OutOfOrder,
    };

    /// <summary>The files in a folder of the copy and in its folders, none when it is not there.</summary>
    private static string[] FilesUnder(string folder) => Directory.Exists(folder) ? Directory.GetFiles(folder, "*", SearchOption.AllDirectories) : [];

    /// <summary>A document on the share, as a write of another Kiler makes it.</summary>
    private void Create(DocumentKey key, string name) => Assert.Equal(WriteOutcome.Written, _share.CreateDocument(key, Content(name)).Outcome);

    /// <summary>A new version of a document on the share, as a write of another Kiler makes it, with the old one in its history slot.</summary>
    private void Replace(DocumentKey key, int version, string name) => Assert.Equal(WriteOutcome.Written, _share.ReplaceDocument(key, version, Content(name)).Outcome);

    /// <summary>Changes the file of <see cref="Record"/> at version 2 outside Kiler, as <paramref name="change"/> says.</summary>
    private void MakeChange(string path, Change change)
    {
        switch (change)
        {
            case Change.Damaged:
                File.WriteAllText(path, "{", Encoding.UTF8);
                break;
            case Change.TooLarge:
                File.WriteAllBytes(path, new byte[DocumentFormat.MaxSize + 1]);
                break;
            case Change.EditedInPlace:
                TestFolder.EditInPlace(path, DocumentFormat.Next(Record, 1, Content("edited by hand")));
                break;
            default:
                TestFolder.WriteAsKiler(path, File.ReadAllBytes(_share.HistoryPath(Record, 1)));
                break;
        }
    }

    /// <summary>Opens the copy on <see cref="_clock"/>, with an account or without.</summary>
    private DataCopy OpenAt(string? account = null) => DataCopy.Open(_share, _copy, _clock, CopyLimits.Default, account);

    /// <summary>Opens a second copy of the share in a folder of its own, as on the machine of another user.</summary>
    private DataCopy OpenAnother() => DataCopy.Open(_share, Directory.CreateDirectory(Path.Combine(_test.Root, "another copy")).FullName, _clock, CopyLimits.Default);

    /// <summary>
    /// Loads a copy twice, <see cref="InPlaceEdits.SettleTime"/> apart, so it reports what changed outside Kiler (M2.7a), and
    /// gives the one change that it lists.
    /// </summary>
    private OutsideChange List(DataCopy copy)
    {
        copy.Load();
        _clock.Advance(InPlaceEdits.SettleTime);
        copy.Load();
        return Assert.Single(copy.ToRestore());
    }

    /// <summary>Loads two copies, and again <see cref="InPlaceEdits.SettleTime"/> later, so both report what changed outside Kiler.</summary>
    private void LoadBoth(DataCopy copy, DataCopy other)
    {
        copy.Load();
        other.Load();
        _clock.Advance(InPlaceEdits.SettleTime);
        copy.Load();
        other.Load();
    }

    /// <summary>
    /// Puts an empty file at the name of a history slot of a document, as the marker of a write back stays when the machine
    /// that holds the files crashes while the write back holds it, made old or young, and gives its path.
    /// </summary>
    private string LeaveDeadLock(DocumentKey key, int version, bool old)
    {
        var slot = _share.HistoryPath(key, version);
        Directory.CreateDirectory(Path.GetDirectoryName(slot)!);
        TestFolder.WriteAsKiler(slot, []);
        if (old)
        {
            File.SetLastWriteTimeUtc(slot, DateTime.UtcNow - DataFolder.SlotAge - TimeSpan.FromMinutes(1));
        }

        return slot;
    }

    /// <summary>The files in the history folder of a document on the share, by name in order, with their bytes.</summary>
    private SortedDictionary<string, byte[]> SlotBytesOnShare(DocumentKey key)
    {
        var folder = Path.GetDirectoryName(_share.HistoryPath(key, 1))!;
        var slots = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        if (Directory.Exists(folder))
        {
            foreach (var path in Directory.GetFiles(folder))
            {
                slots.Add(Path.GetFileName(path), File.ReadAllBytes(path));
            }
        }

        return slots;
    }

    /// <summary>The file of a document in the copy: a mirror of the share in <c>data\</c> (decided in M2.7).</summary>
    private string CopyPath(DocumentKey key) => Path.Combine(_copy, "data", key.Folder ?? "", key.Id + ".json");
}

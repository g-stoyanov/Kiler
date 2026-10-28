using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Time.Testing;

namespace Kiler.Storage.Tests;

/// <summary>
/// How the local copy follows the changes of Kiler (M2.7a): the moves to the trash and back, the slots of the history, the
/// writes of this Kiler, and the last good bytes of a document changed outside Kiler, kept apart and reported.
/// </summary>
public sealed class DataCopyFollowTests : IDisposable
{
    private static readonly DocumentKey Record = DocumentKey.InFolder(DataFolder.Records, "8f3a1c");
    private static readonly DocumentKey OtherRecord = DocumentKey.InFolder(DataFolder.Records, "b72e09");
    private static readonly DocumentKey ThirdRecord = DocumentKey.InFolder(DataFolder.Records, "c3d4e5");
    private static readonly DocumentKey Settings = DocumentKey.AtRoot(DataFolder.Settings);

    private readonly TestFolder _test = new();
    private readonly TestFileSystem _files = new();
    private readonly FakeTimeProvider _clock = new(DateTimeOffset.UtcNow);
    private readonly DataFolder _share;
    private readonly string _copy;

    public DataCopyFollowTests()
    {
        _share = DataFolder.Create(Path.Combine(_test.Root, "data"), _files);
        _copy = Directory.CreateDirectory(Path.Combine(_test.Root, "copy")).FullName;
    }

    public void Dispose() => _test.Dispose();

    /// <summary>
    /// The report waits until the document has been gone for <see cref="InPlaceEdits.SettleTime"/>, as the listing of a move,
    /// or of the file back, can come late through the SMB cache (code review of M2.7a).
    /// </summary>
    [Fact]
    public void Load_ADocumentDeletedOutsideTheTrash_KeepsItsLastGoodBytesApart_AndReportsItOnceItHasStayedGone()
    {
        Create(Record, "record");
        Create(OtherRecord, "stays");
        var copy = OpenAt();
        copy.Load();
        var good = File.ReadAllBytes(_share.DocumentPath(Record));
        File.Delete(_share.DocumentPath(Record));

        var load = copy.Load();

        Assert.Equal(Record, Assert.Single(load.Gone));
        Assert.Empty(load.Outside);
        Assert.False(File.Exists(CopyPath(Record)), "The gone document is still in data.");
        Assert.Equal(good, File.ReadAllBytes(KeptPath(Record)));
        _clock.Advance(InPlaceEdits.SettleTime);

        Assert.Equal(new OutsideChange(Record, InTrash: false, OutsideReason.Gone, LastGood: 1), Assert.Single(copy.Load().Outside));
        Assert.Empty(copy.Load().Outside);
        Assert.Equal(OtherRecord, Assert.Single(OpenAt().Documents()).Key);
        Assert.Equal(good, File.ReadAllBytes(KeptPath(Record)));
    }

    [Fact]
    public void Load_ADamagedDocument_IsGivenAsDamaged_AndKeepsItsLastGoodBytesApart()
    {
        Create(Record, "good");
        var copy = Open();
        copy.Load();
        var good = File.ReadAllBytes(_share.DocumentPath(Record));
        File.WriteAllText(_share.DocumentPath(Record), "{", Encoding.UTF8);

        var load = copy.Load();

        Assert.Equal(DocumentDamage.NotJson, Assert.Single(load.Changed).Read.Damage);
        Assert.Equal(new OutsideChange(Record, InTrash: false, OutsideReason.Damaged, LastGood: 1), Assert.Single(load.Outside));
        Assert.False(File.Exists(CopyPath(Record)), "The bytes of a damaged file are in the copy.");
        Assert.Equal(good, File.ReadAllBytes(KeptPath(Record)));
        Assert.Equal(DocumentDamage.NotJson, Assert.Single(Open().Documents()).Read.Damage);
    }

    [Fact]
    public void Load_ADocumentEditedInPlace_IsGivenAsItIs_AndKeepsItsLastGoodBytesApart_AndIsReportedOnceTheEditHasSettled()
    {
        Create(Record, "good");
        var copy = OpenAt();
        copy.Load();
        var good = File.ReadAllBytes(_share.DocumentPath(Record));
        var edited = DocumentFormat.New(Record, Content("edited by hand"));
        TestFolder.EditInPlace(_share.DocumentPath(Record), edited);

        var load = copy.Load();

        Assert.Equal("edited by hand", Name(Assert.Single(load.Changed)));
        Assert.Empty(load.Outside);
        Assert.Equal(edited, File.ReadAllBytes(CopyPath(Record)));
        Assert.Equal(good, File.ReadAllBytes(KeptPath(Record)));
        _clock.Advance(InPlaceEdits.SettleTime);

        var settled = copy.Load();

        Assert.Empty(settled.Changed);
        Assert.Equal(new OutsideChange(Record, InTrash: false, OutsideReason.EditedInPlace, LastGood: 1), Assert.Single(settled.Outside));
        _clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Empty(copy.Load().Outside);
        Assert.Equal("edited by hand", Name(Assert.Single(OpenAt().Documents())));
        Assert.Equal(good, File.ReadAllBytes(KeptPath(Record)));
    }

    /// <summary>
    /// A rename over a name that another file left in the last 15 s gives the new file the creation time of the old one
    /// (tunneling, M2.3), so a write of Kiler looks edited in place until its times are set again; it is never reported.
    /// </summary>
    [Fact]
    public void Load_AWriteOfKilerThatLooksEditedForAMoment_IsNotReported_AndItsBytesBecomeTheLastGood()
    {
        Create(Record, "first");
        var copy = OpenAt();
        copy.Load();
        Replace(Record, 1, "second");
        var path = _share.DocumentPath(Record);
        var written = File.GetLastWriteTimeUtc(path);
        File.SetCreationTimeUtc(path, written.AddMinutes(-1));

        Assert.Equal(2, Assert.Single(copy.Load().Changed).Read.Document?.Version);
        File.SetCreationTimeUtc(path, written);
        _clock.Advance(InPlaceEdits.SettleTime);

        var load = copy.Load();

        Assert.Empty(load.Outside);
        _clock.Advance(InPlaceEdits.SettleTime);
        Assert.Empty(copy.Load().Outside);
        Assert.False(File.Exists(KeptPath(Record)), "The last good bytes of a version that Kiler made are kept apart.");
        Assert.Equal(File.ReadAllBytes(path), File.ReadAllBytes(CopyPath(Record)));
    }

    /// <summary>Kiler never writes a lower version, nor other bytes at the same version (decided in M2.7a, by the user).</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Load_ADocumentWhoseVersionGoesDownOrStaysWithOtherBytes_IsAChangeOutsideKiler(bool down)
    {
        Create(Record, "first");
        Replace(Record, 1, "second");
        var copy = Open();
        copy.Load();
        var good = File.ReadAllBytes(_share.DocumentPath(Record));
        var other = down ? File.ReadAllBytes(_share.HistoryPath(Record, 1)) : DocumentFormat.Next(Record, 1, Content("other"));
        TestFolder.WriteAsKiler(_share.DocumentPath(Record), other);

        var load = copy.Load();

        Assert.Equal(down ? 1 : 2, Assert.Single(load.Changed).Read.Document?.Version);
        Assert.Equal(new OutsideChange(Record, InTrash: false, OutsideReason.OutOfOrder, LastGood: 2), Assert.Single(load.Outside));
        Assert.Equal(other, File.ReadAllBytes(CopyPath(Record)));
        Assert.Equal(good, File.ReadAllBytes(KeptPath(Record)));
    }

    [Fact]
    public void Load_ADocumentThatComesBackAsItWas_IsGoodAgain_AndItsKeptBytesGo()
    {
        Create(Record, "record");
        var copy = Open();
        copy.Load();
        var good = File.ReadAllBytes(_share.DocumentPath(Record));
        File.Delete(_share.DocumentPath(Record));
        copy.Load();
        TestFolder.WriteAsKiler(_share.DocumentPath(Record), good);

        var load = copy.Load();

        Assert.Equal(Record, Assert.Single(load.Changed).Key);
        Assert.Empty(load.Outside);
        Assert.False(File.Exists(KeptPath(Record)), "The kept bytes of a document that is good again stay.");
        Assert.Equal(good, File.ReadAllBytes(CopyPath(Record)));
    }

    [Fact]
    public void Load_AMoveToTheTrashAndBack_IsFollowed_AndNothingIsKeptApart()
    {
        Create(Record, "record");
        Create(OtherRecord, "stays");
        var copy = Open();
        copy.Load();
        var bytes = File.ReadAllBytes(_share.DocumentPath(Record));
        Assert.Equal(WriteOutcome.Deleted, _share.DeleteDocument(Record, 1).Outcome);

        var deleted = copy.Load();

        Assert.Equal(Record, Assert.Single(deleted.Gone));
        Assert.Empty(deleted.Changed);
        Assert.Empty(deleted.Outside);
        Assert.False(File.Exists(CopyPath(Record)), "The deleted document is still in data.");
        Assert.False(File.Exists(KeptPath(Record)), "The deleted document is kept apart.");
        Assert.Equal(bytes, File.ReadAllBytes(TrashCopyPath(Record)));

        // The copy has the deleted version in its trash, so it does not take the slot of the delete from the share.
        Assert.Empty(SlotCopies(Record));
        Assert.Equal(Record, Assert.Single(Open().Trash()).Key);
        Assert.Equal(WriteOutcome.Written, _share.RestoreFromTrash(Record).Outcome);

        var restored = copy.Load();

        Assert.Equal(Record, Assert.Single(restored.Changed).Key);
        Assert.Empty(restored.Gone);
        Assert.Empty(restored.Outside);
        Assert.Empty(copy.Trash());
        Assert.False(File.Exists(TrashCopyPath(Record)), "The restored document is still in the trash of the copy.");
        Assert.Equal(bytes, File.ReadAllBytes(CopyPath(Record)));
        Assert.Equal(Keys([OtherRecord, Record]), Keys(Open().Documents()));
    }

    /// <summary>
    /// A load says when the trash that the copy gives changed (decided in M2.8, by the user), so the upper layers read it again
    /// only then: a delete, a restore, a document gone from the trash, a document changed or damaged there.
    /// </summary>
    [Fact]
    public void Load_SaysWhenTheTrashChanged_AndOnlyThen()
    {
        Create(Record, "record");
        Create(OtherRecord, "other");
        var copy = OpenAt();
        Assert.False(copy.Load().TrashChanged, "The first load of a share without a trash says that the trash changed.");
        Assert.Equal(WriteOutcome.Deleted, _share.DeleteDocument(Record, 1).Outcome);

        Assert.True(copy.Load().TrashChanged, "A delete does not change the trash.");
        Assert.False(copy.Load().TrashChanged, "A load that saw nothing says that the trash changed.");
        Replace(OtherRecord, 1, "changed");
        Create(ThirdRecord, "new");
        Assert.False(copy.Load().TrashChanged, "A change of the documents in their folders changes the trash.");
        Assert.Equal(WriteOutcome.Written, _share.RestoreFromTrash(Record).Outcome);
        Assert.True(copy.Load().TrashChanged, "A restore does not change the trash.");
        Assert.Equal(WriteOutcome.Deleted, _share.DeleteDocument(OtherRecord, 2).Outcome);
        copy.Load();
        TestFolder.WriteAsKiler(_share.TrashPath(OtherRecord), "{"u8.ToArray());
        Assert.True(copy.Load().TrashChanged, "A document damaged in the trash does not change the trash.");
        File.Delete(_share.TrashPath(OtherRecord));
        Assert.True(copy.Load().TrashChanged, "A document gone from the trash does not change the trash.");
    }

    [Fact]
    public void Load_ADocumentThatGoesFromTheTrashWithoutComingBack_KeepsItsLastGoodBytesApart()
    {
        Create(Record, "record");
        var copy = OpenAt();
        copy.Load();
        var bytes = File.ReadAllBytes(_share.DocumentPath(Record));
        _share.DeleteDocument(Record, 1);
        copy.Load();
        File.Delete(_share.TrashPath(Record));
        Assert.Empty(copy.Load().Outside);
        _clock.Advance(InPlaceEdits.SettleTime);

        var load = copy.Load();

        Assert.Empty(load.Gone);
        Assert.Equal(new OutsideChange(Record, InTrash: true, OutsideReason.Gone, LastGood: 1), Assert.Single(load.Outside));
        Assert.Equal(bytes, File.ReadAllBytes(KeptTrashPath(Record)));
        Assert.Empty(Open().Trash());
    }

    [Fact]
    public void Load_ADocumentMovedToTheTrashAtAnOlderVersion_IsAChangeOutsideKiler_AndKeepsTheLastGoodBytes()
    {
        Create(Record, "first");
        Replace(Record, 1, "second");
        var copy = Open();
        copy.Load();
        var good = File.ReadAllBytes(_share.DocumentPath(Record));
        Directory.CreateDirectory(Path.GetDirectoryName(_share.TrashPath(Record))!);
        TestFolder.WriteAsKiler(_share.TrashPath(Record), File.ReadAllBytes(_share.HistoryPath(Record, 1)));
        File.Delete(_share.DocumentPath(Record));

        var load = copy.Load();

        Assert.Equal(Record, Assert.Single(load.Gone));
        Assert.Equal(new OutsideChange(Record, InTrash: true, OutsideReason.OutOfOrder, LastGood: 2), Assert.Single(load.Outside));
        Assert.Equal(good, File.ReadAllBytes(KeptTrashPath(Record)));
        Assert.Equal(1, Assert.Single(Open().Trash()).Read.Document?.Version);
    }

    [Fact]
    public void Load_TheTrashFolders_SkipTheirMarkersAndTemporaryFiles_AndDeleteTheOldOnes()
    {
        Create(Record, "record");
        var copy = OpenAt();
        copy.Load();
        _share.DeleteDocument(Record, 1);
        var trash = Path.GetDirectoryName(_share.TrashPath(Record))!;
        var old = Path.Combine(trash, "b72e09.0123abcd.tmp");
        var young = Path.Combine(trash, "c3d4e5.89abcdef.tmp");
        File.WriteAllText(old, "made up");
        File.WriteAllText(young, "made up");
        File.SetLastWriteTimeUtc(old, _clock.GetUtcNow().UtcDateTime.AddMinutes(-11));

        var load = copy.Load();

        Assert.Empty(load.Skipped);
        Assert.Equal(Record, Assert.Single(copy.Trash()).Key);
        Assert.False(File.Exists(old), "The old temporary file of the trash is still there.");
        Assert.True(File.Exists(young), "The young temporary file of the trash is gone.");
    }

    [Fact]
    public void Load_ARootDocumentMovedToTheTrash_IsFollowed()
    {
        Create(Settings, "settings");
        var copy = Open();
        copy.Load();
        Assert.Equal(WriteOutcome.Deleted, _share.DeleteDocument(Settings, 1).Outcome);

        var load = copy.Load();

        Assert.Equal(Settings, Assert.Single(load.Gone));
        Assert.Empty(load.Outside);
        Assert.Equal(Settings, Assert.Single(Open().Trash()).Key);
    }

    [Fact]
    public void Load_ANewVersion_TakesTheSlotsOfItsHistoryFromTheVersionThatTheCopyHad()
    {
        Create(Record, "first");
        var copy = Open();
        copy.Load();
        Replace(Record, 1, "second");
        Replace(Record, 2, "third");

        var load = copy.Load();

        Assert.Equal(3, Assert.Single(load.Changed).Read.Document?.Version);
        Assert.Equal(["v1.json", "v2.json"], SlotCopies(Record));
        foreach (var version in (int[])[1, 2])
        {
            Assert.Equal(File.ReadAllBytes(_share.HistoryPath(Record, version)), File.ReadAllBytes(SlotCopyPath(Record, version)));
        }
    }

    [Fact]
    public void Load_TheFirstLoad_TakesNoHistory_AndListsNoHistoryFolder()
    {
        Create(Record, "first");
        Replace(Record, 1, "second");
        var lists = CountHistoryListings();

        Open().Load();

        Assert.Equal(0, lists());
        Assert.Empty(SlotCopies(Record));
    }

    [Fact]
    public void Load_ListsTheHistoryFolderOfADocument_OnlyWhenTheDocumentHasChanged()
    {
        Create(Record, "first");
        Create(OtherRecord, "other");
        var copy = Open();
        copy.Load();
        Replace(Record, 1, "second");
        var lists = CountHistoryListings();

        copy.Load();

        Assert.Equal(1, lists());
        copy.Load();
        Assert.Equal(1, lists());
    }

    [Fact]
    public void Load_AHistoryFolderThatCannotBeListed_IsReported_AndListedAgainByTheNextLoad()
    {
        Create(Record, "first");
        var copy = Open();
        copy.Load();
        Replace(Record, 1, "second");
        Replace(Record, 2, "third");
        var history = Path.GetDirectoryName(_share.HistoryPath(Record, 1))!;
        _files.Before = (step, path) =>
        {
            if (step == nameof(IFileSystem.List) && path == history)
            {
                throw new IOException("A made-up failure of the listing.");
            }
        };

        var load = copy.Load();

        Assert.Equal(new SkippedEntry(@"history\records\8f3a1c", null, SkipReason.Unreadable), Assert.Single(load.Skipped));
        Assert.Equal(["v1.json"], SlotCopies(Record));
        _files.Before = null;
        Assert.Empty(copy.Load().Skipped);
        Assert.Equal(["v1.json", "v2.json"], SlotCopies(Record));
    }

    [Fact]
    public void Load_TheHistoryOfTheCopy_HasABudgetOfItsOwn_AndItsOldestSlotsMakeRoom()
    {
        Create(Record, "v1");
        var copy = OpenAt(DataCopyTests.Limits(historySlots: 2));
        copy.Load();
        for (var version = 1; version <= 3; version++)
        {
            Replace(Record, version, "v" + (version + 1));
            _clock.Advance(TimeSpan.FromMinutes(1));
            Assert.Empty(copy.Load().Skipped);
        }

        Assert.Equal(["v2.json", "v3.json"], SlotCopies(Record));
        Assert.Equal(4, Assert.Single(OpenAt(DataCopyTests.Limits(historySlots: 2)).Documents()).Read.Document?.Version);
    }

    [Fact]
    public void Load_ASlotOverTheBudgetOfTheHistory_IsReported_AndTheDocumentsAreNotHeldBack()
    {
        Create(Record, "first");
        var copy = Open(DataCopyTests.Limits(historyBytes: 10));
        copy.Load();
        Replace(Record, 1, "second");

        var load = copy.Load();

        Assert.Equal(2, Assert.Single(load.Changed).Read.Document?.Version);
        Assert.Equal(new SkippedEntry(@"history\records\8f3a1c", "v1.json", SkipReason.OverBudget), Assert.Single(load.Skipped));
        Assert.Empty(SlotCopies(Record));
    }

    [Fact]
    public void Replace_ThroughTheCopy_KeepsTheBytesItWrote_AndTheSlot_AndTheNextLoadReadsTheShareBack()
    {
        var copy = Open();
        Assert.Equal(WriteOutcome.Written, copy.Create(Record, Content("first")).Outcome);
        Assert.Equal(WriteOutcome.Written, copy.Replace(Record, 1, Content("second")).Outcome);

        Assert.Equal(File.ReadAllBytes(_share.DocumentPath(Record)), File.ReadAllBytes(CopyPath(Record)));
        Assert.Equal(File.ReadAllBytes(_share.HistoryPath(Record, 1)), File.ReadAllBytes(SlotCopyPath(Record, 1)));
        var started = Assert.Single(Open().Documents()).Read;
        Assert.Equal(2, started.Document?.Version);
        Assert.Null(started.Owner);
        _files.Reads = 0;

        var load = copy.Load();

        Assert.Equal(1, _files.Reads);
        Assert.Empty(load.Outside);
        Assert.Equal(TestOwners.Me, Assert.Single(Open().Documents()).Read.Owner);
    }

    /// <summary>
    /// Someone can rename the temporary file of a write away and put a file of their own under its name before the rename
    /// (M2.3 note); the copy keeps the bytes that this Kiler wrote, and the load finds the other file.
    /// </summary>
    [Fact]
    public void Replace_ThroughTheCopy_ThenAnotherFileUnderItsName_IsAChangeOutsideKiler_AndKeepsTheBytesItWrote()
    {
        var copy = Open();
        copy.Create(Record, Content("first"));
        copy.Replace(Record, 1, Content("second"));
        var mine = File.ReadAllBytes(_share.DocumentPath(Record));
        TestFolder.WriteAsKiler(_share.DocumentPath(Record), DocumentFormat.Next(Record, 1, Content("swapped")));

        var load = copy.Load();

        Assert.Equal(new OutsideChange(Record, InTrash: false, OutsideReason.OutOfOrder, LastGood: 2), Assert.Single(load.Outside));
        Assert.Equal(mine, File.ReadAllBytes(KeptPath(Record)));
    }

    [Fact]
    public void Delete_AndRestoreFromTrash_ThroughTheCopy_AreFollowedWithoutALoad()
    {
        var copy = Open();
        copy.Create(Record, Content("record"));
        var bytes = File.ReadAllBytes(_share.DocumentPath(Record));

        Assert.Equal(WriteOutcome.Deleted, copy.Delete(Record, 1).Outcome);

        Assert.Empty(Open().Documents());
        Assert.Equal(Record, Assert.Single(Open().Trash()).Key);
        Assert.Equal(bytes, File.ReadAllBytes(TrashCopyPath(Record)));
        Assert.Equal(bytes, File.ReadAllBytes(SlotCopyPath(Record, 1)));

        Assert.Equal(WriteOutcome.Written, copy.RestoreFromTrash(Record).Outcome);

        Assert.Equal(Record, Assert.Single(Open().Documents()).Key);
        Assert.Empty(Open().Trash());
        var load = copy.Load();
        Assert.Empty(load.Gone);
        Assert.Empty(load.Outside);
    }

    [Fact]
    public void Writes_ThroughTheCopy_ThatTheShareRefuses_LeaveTheCopyAsItWas()
    {
        var copy = Open();
        copy.Create(Record, Content("record"));

        Assert.Equal(WriteOutcome.Exists, copy.Create(Record, Content("again")).Outcome);
        Assert.Equal(WriteOutcome.Conflict, copy.Replace(Record, 2, Content("stale")).Outcome);
        Assert.Equal(WriteOutcome.Conflict, copy.Delete(Record, 2).Outcome);
        Assert.Equal(WriteOutcome.Missing, copy.RestoreFromTrash(Record).Outcome);

        Assert.Equal(File.ReadAllBytes(_share.DocumentPath(Record)), File.ReadAllBytes(CopyPath(Record)));
        Assert.Empty(Open().Trash());
    }

    /// <summary>Bytes that this Kiler wrote never make room as brief leftovers (fourth security review of M2.7).</summary>
    [Fact]
    public void Load_TheLastGoodBytesThatThisKilerWrote_NeverMakeRoom()
    {
        var copy = OpenAt(DataCopyTests.Limits(documents: 2));
        copy.Create(Record, Content("mine"));
        copy.Load();
        _clock.Advance(TimeSpan.FromMinutes(1));
        File.Delete(_share.DocumentPath(Record));
        copy.Load();
        Create(OtherRecord, "other");
        Assert.Equal(OtherRecord, Assert.Single(copy.Load().Changed).Key);
        Create(ThirdRecord, "third");
        _clock.Advance(TimeSpan.FromMinutes(1));

        var load = copy.Load();

        Assert.Empty(load.Changed);
        Assert.Equal(new SkippedEntry(DataFolder.Records, "c3d4e5.json", SkipReason.OverBudget), Assert.Single(load.Skipped));
        Assert.True(File.Exists(KeptPath(Record)), "The last good bytes that this Kiler wrote made room.");
    }

    /// <summary>A version that is not after the last good one keeps both; it is read, and left when the budget has no room for both.</summary>
    [Fact]
    public void Load_ADocumentThatTurnsOutToNeedRoomForBoth_IsGiven_AndLeftWhenTheBudgetHasNone()
    {
        Create(Record, "first");
        Replace(Record, 1, "second");
        var size = new FileInfo(_share.DocumentPath(Record)).Length;
        var copy = Open(DataCopyTests.Limits(bytes: size + (size / 2)));
        copy.Load();
        var good = File.ReadAllBytes(_share.DocumentPath(Record));
        TestFolder.WriteAsKiler(_share.DocumentPath(Record), DocumentFormat.Next(Record, 1, Content("other")));

        var load = copy.Load();

        Assert.Equal("other", Name(Assert.Single(load.Changed)));
        Assert.Equal(new SkippedEntry(DataFolder.Records, "8f3a1c.json", SkipReason.OverBudget), Assert.Single(load.Skipped));
        Assert.Empty(load.Outside);
        Assert.Equal(good, File.ReadAllBytes(CopyPath(Record)));
        Assert.False(File.Exists(KeptPath(Record)), "The copy went over its budget.");
    }

    [Fact]
    public void Open_DeletesTheFilesOfTheTrashTheHistoryAndTheKeptCopiesThatNoEntryNames()
    {
        Create(Record, "record");
        Create(OtherRecord, "other");
        var copy = OpenAt();
        copy.Load();
        Replace(Record, 1, "second");
        File.Delete(_share.DocumentPath(OtherRecord));
        copy.Load();
        _share.DeleteDocument(Record, 2);
        copy.Load();
        string[] strays =
        [
            Path.Combine(Path.GetDirectoryName(TrashCopyPath(Record))!, "d4e5f6.json"),
            Path.Combine(Path.GetDirectoryName(SlotCopyPath(Record, 1))!, "v9.json"),
            Path.Combine(Path.GetDirectoryName(KeptPath(Record))!, "d4e5f6.json"),
            Path.Combine(_copy, "kept", "trash", "levels.json"),
        ];
        foreach (var stray in strays)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(stray)!);
            File.WriteAllText(stray, "made up");
        }

        _clock.Advance(TimeSpan.FromMinutes(11));

        OpenAt();

        Assert.All(strays, stray => Assert.False(File.Exists(stray), $"{stray} is still there."));
        Assert.True(File.Exists(TrashCopyPath(Record)), "The copy of the trash is gone.");
        Assert.True(File.Exists(SlotCopyPath(Record, 1)), "A slot is gone.");
        Assert.True(File.Exists(KeptPath(OtherRecord)), "The last good bytes of a gone document are gone.");
    }

    [Fact]
    public void Open_ASlotWhoseFileIsMissing_IsForgotten()
    {
        Create(Record, "record");
        var copy = OpenAt();
        copy.Load();
        Replace(Record, 1, "second");
        copy.Load();
        Assert.Equal([1], IndexSlots());
        File.Delete(SlotCopyPath(Record, 1));

        OpenAt().Load();

        Assert.Empty(IndexSlots());
    }

    /// <summary>A load keeps a gone document only for its last good bytes, so without them it would hold a place for good.</summary>
    [Fact]
    public void Open_AGoneDocumentWhoseKeptBytesAreMissing_IsForgotten_AndNoLongerCountsInTheBudget()
    {
        Create(Record, "record");
        Create(OtherRecord, "other");
        var copy = OpenAt(DataCopyTests.Limits(documents: 2));
        copy.Load();
        _clock.Advance(TimeSpan.FromDays(2));
        File.Delete(_share.DocumentPath(OtherRecord));
        copy.Load();
        File.Delete(KeptPath(OtherRecord));
        Create(ThirdRecord, "third");

        var load = OpenAt(DataCopyTests.Limits(documents: 2)).Load();

        Assert.Equal(ThirdRecord, Assert.Single(load.Changed).Key);
        Assert.Empty(load.Skipped);
    }

    /// <summary>
    /// A load that stops before its index is written, here as the index cannot be written, leaves the index of the load
    /// before: the files that it names are still there, so the last good bytes are not lost (code review of M2.7a).
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Load_ThatCannotWriteItsIndex_LeavesTheFilesThatTheOldIndexNames(bool damaged)
    {
        Create(Record, "good");
        var copy = OpenAt();
        copy.Load();
        var good = File.ReadAllBytes(_share.DocumentPath(Record));
        if (damaged)
        {
            File.WriteAllText(_share.DocumentPath(Record), "{", Encoding.UTF8);
        }
        else
        {
            TestFolder.EditInPlace(_share.DocumentPath(Record), DocumentFormat.New(Record, Content("edited by hand")));
        }

        using (new FileStream(Path.Combine(_copy, "index.json"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            copy.Load();
        }

        _clock.Advance(TimeSpan.FromMinutes(11));
        var restarted = OpenAt();

        Assert.Equal(1, Assert.Single(restarted.Documents()).Read.Document?.Version);
        restarted.Load();
        Assert.Equal(good, File.ReadAllBytes(KeptPath(Record)));
    }

    /// <summary>A write through a copy that a load filled would push the index over the budget, and the next start would take it for no index (security review of M2.7a).</summary>
    [Fact]
    public void Create_ThroughAFullCopy_IsLeftToTheNextLoad_AndTheNextStartKeepsTheCopy()
    {
        Create(OtherRecord, "other");
        var copy = OpenAt(DataCopyTests.Limits(documents: 1));
        copy.Load();

        Assert.Equal(WriteOutcome.Written, copy.Create(Record, Content("mine")).Outcome);

        _clock.Advance(TimeSpan.FromMinutes(11));
        var restarted = OpenAt(DataCopyTests.Limits(documents: 1));
        Assert.Equal(OtherRecord, Assert.Single(restarted.Documents()).Key);
        Assert.Equal(new SkippedEntry(DataFolder.Records, "8f3a1c.json", SkipReason.OverBudget), Assert.Single(restarted.Load().Skipped));
    }

    /// <summary>The version that a new one replaces is taken from the bytes of the copy, not left to the history of the share (security review of M2.7a).</summary>
    [Fact]
    public void Load_ANewVersionWhoseSlotIsMissingOnTheShare_KeepsTheVersionThatTheCopyHadInItsHistory()
    {
        Create(Record, "first");
        var copy = Open();
        copy.Load();
        var first = File.ReadAllBytes(_share.DocumentPath(Record));
        Replace(Record, 1, "second");
        File.Delete(_share.HistoryPath(Record, 1));

        copy.Load();

        Assert.Equal(first, File.ReadAllBytes(SlotCopyPath(Record, 1)));
    }

    [Fact]
    public void Replace_ThroughTheCopy_OverAnEditInPlace_KeepsTheVersionThatKilerMadeAsItsSlot()
    {
        var copy = OpenAt();
        copy.Create(Record, Content("good"));
        copy.Load();
        var good = File.ReadAllBytes(_share.DocumentPath(Record));
        TestFolder.EditInPlace(_share.DocumentPath(Record), DocumentFormat.New(Record, Content("edited by hand")));
        copy.Load();

        Assert.Equal(WriteOutcome.Written, copy.Replace(Record, 1, Content("saved")).Outcome);

        Assert.Equal(good, File.ReadAllBytes(SlotCopyPath(Record, 1)));
        Assert.NotEqual(good, File.ReadAllBytes(_share.HistoryPath(Record, 1)));
        Assert.False(File.Exists(KeptPath(Record)), "The kept bytes of the edit stay after a write of Kiler.");
    }

    /// <summary>Only a slot that was read good makes room, so junk that turns out damaged pushes nothing out (security review of M2.7a).</summary>
    [Fact]
    public void Load_SlotsOfTheShareThatTurnOutDamaged_MakeNoRoom()
    {
        Create(Record, "v1");
        var copy = OpenAt(DataCopyTests.Limits(historySlots: 2));
        copy.Load();
        Replace(Record, 1, "v2");
        _clock.Advance(TimeSpan.FromMinutes(1));
        copy.Load();
        Replace(Record, 2, "v3");
        Replace(Record, 3, "v4");
        File.WriteAllText(_share.HistoryPath(Record, 3), "{" + new string(' ', 4000), Encoding.UTF8);
        _clock.Advance(TimeSpan.FromMinutes(1));

        copy.Load();

        Assert.Equal(["v1.json", "v2.json"], SlotCopies(Record));
    }

    /// <summary>The slots of this Kiler make room only for each other, so junk on the share cannot push them out (decided in M2.7a, by the user).</summary>
    [Fact]
    public void Load_SlotsOfTheShare_NeverPushOutTheSlotsOfThisKiler()
    {
        var copy = OpenAt(DataCopyTests.Limits(historySlots: 1));
        copy.Create(Record, Content("v1"));
        copy.Replace(Record, 1, Content("v2"));
        Create(OtherRecord, "v1");
        copy.Load();
        Assert.Equal(["v1.json"], SlotCopies(Record));
        Replace(OtherRecord, 1, "v2");
        Replace(OtherRecord, 2, "v3");
        _clock.Advance(TimeSpan.FromMinutes(1));

        var load = copy.Load();

        Assert.Equal(["v1.json"], SlotCopies(Record));
        Assert.Empty(SlotCopies(OtherRecord));
        Assert.Equal(
            [new SkippedEntry(@"history\records\b72e09", "v2.json", SkipReason.OverBudget), new SkippedEntry(@"history\records\b72e09", "v1.json", SkipReason.OverBudget)],
            load.Skipped);
    }

    [Fact]
    public void Load_ASlotOfAnotherVersionInside_IsSkipped_AndNotTriedAgain()
    {
        Create(Record, "v1");
        var copy = Open();
        copy.Load();
        Replace(Record, 1, "v2");
        Replace(Record, 2, "v3");
        TestFolder.WriteAsKiler(_share.HistoryPath(Record, 2), File.ReadAllBytes(_share.DocumentPath(Record)));

        Assert.Empty(copy.Load().Skipped);

        Assert.Equal(["v1.json"], SlotCopies(Record));
        var lists = CountHistoryListings();
        copy.Load();
        Assert.Equal(0, lists());
    }

    [Fact]
    public void Load_ASlotThatCannotBeRead_IsReported_AndReadAgainByTheNextLoad()
    {
        Create(Record, "v1");
        var copy = Open();
        copy.Load();
        Replace(Record, 1, "v2");
        Replace(Record, 2, "v3");
        TestFolder.DenyRead(_share.HistoryPath(Record, 2));
        LoadResult load;
        try
        {
            load = copy.Load();
        }
        finally
        {
            TestFolder.AllowRead(_share.HistoryPath(Record, 2));
        }

        Assert.Equal(new SkippedEntry(@"history\records\8f3a1c", "v2.json", SkipReason.Unreadable), Assert.Single(load.Skipped));
        Assert.Empty(copy.Load().Skipped);
        Assert.Equal(["v1.json", "v2.json"], SlotCopies(Record));
    }

    [Fact]
    public void Open_RemovesAnEmptyHistoryFolderOfTheCopy()
    {
        Create(Record, "record");
        Create(OtherRecord, "other");
        var copy = OpenAt(DataCopyTests.Limits(historySlots: 1));
        copy.Load();
        Replace(Record, 1, "second");
        _clock.Advance(TimeSpan.FromMinutes(1));
        copy.Load();
        Replace(OtherRecord, 1, "second");
        _clock.Advance(TimeSpan.FromMinutes(1));
        copy.Load();
        var folder = Path.GetDirectoryName(SlotCopyPath(Record, 1))!;
        Assert.Empty(Directory.GetFiles(folder));
        _clock.Advance(TimeSpan.FromMinutes(11));

        OpenAt(DataCopyTests.Limits(historySlots: 1));

        Assert.False(Directory.Exists(folder), "The empty history folder of the copy is still there.");
        Assert.Equal(["v1.json"], SlotCopies(OtherRecord));
    }

    /// <summary>Anyone can make every document change, so a load reports at most as many as it reports skipped entries (security review of M2.7a).</summary>
    [Fact]
    public void Load_ReportsAtMostTheLimitOfChangesOutsideKiler_AndTheRestLater()
    {
        Create(Record, "record");
        Create(OtherRecord, "other");
        Create(ThirdRecord, "third");
        var copy = OpenAt(DataCopyTests.Limits(reports: 2));
        copy.Load();
        foreach (var key in (DocumentKey[])[Record, OtherRecord, ThirdRecord])
        {
            File.Delete(_share.DocumentPath(key));
        }

        copy.Load();
        _clock.Advance(InPlaceEdits.SettleTime);

        Assert.Equal(2, copy.Load().Outside.Count);
        Assert.Single(copy.Load().Outside);
        Assert.Empty(copy.Load().Outside);
    }

    /// <summary>
    /// The SMB client caches the listing of each folder for about 10 s, so a load can list a document that a delete has
    /// moved both in its folder and in the trash; the next load follows the move (code review of M2.7a).
    /// </summary>
    [Fact]
    public void Load_ADeleteSeenInBothPlaces_IsFollowedWhenTheFolderStopsListingIt()
    {
        Create(Record, "record");
        var copy = OpenAt();
        copy.Load();
        var bytes = File.ReadAllBytes(_share.DocumentPath(Record));
        Directory.CreateDirectory(Path.GetDirectoryName(_share.TrashPath(Record))!);
        TestFolder.WriteAsKiler(_share.TrashPath(Record), bytes);
        copy.Load();
        File.Delete(_share.DocumentPath(Record));

        Assert.Equal(Record, Assert.Single(copy.Load().Gone));
        _clock.Advance(InPlaceEdits.SettleTime);

        Assert.Empty(copy.Load().Outside);
        Assert.False(File.Exists(KeptPath(Record)), "The deleted document is kept apart.");
        Assert.Equal(Record, Assert.Single(copy.Trash()).Key);
    }

    /// <summary>The other way: the listing of the trash shows the move a load later than the listing of the folder (code review of M2.7a).</summary>
    [Fact]
    public void Load_ADocumentGoneBeforeTheTrashListsIt_IsFollowedWhenTheTrashListsIt()
    {
        Create(Record, "record");
        var copy = OpenAt();
        copy.Load();
        var bytes = File.ReadAllBytes(_share.DocumentPath(Record));
        Assert.Equal(WriteOutcome.Deleted, _share.DeleteDocument(Record, 1).Outcome);
        var hidden = Path.Combine(_test.Root, "hidden.json");
        File.Move(_share.TrashPath(Record), hidden);
        copy.Load();
        File.Move(hidden, _share.TrashPath(Record));

        var load = copy.Load();

        Assert.Empty(load.Gone);
        Assert.Empty(load.Outside);
        _clock.Advance(InPlaceEdits.SettleTime);
        Assert.Empty(copy.Load().Outside);
        Assert.False(File.Exists(KeptPath(Record)), "The moved document is kept apart.");
        Assert.Equal(bytes, File.ReadAllBytes(TrashCopyPath(Record)));
    }

    /// <summary>
    /// A file in the other place long after its document went is no move: anyone could put a newer version there to make the
    /// copy drop the last good bytes it keeps of the document (second security review of M2.7a).
    /// </summary>
    [Fact]
    public void Load_AFileInTheOtherPlaceLongAfterItsDocumentWent_IsNoMove_AndTheLastGoodBytesStay()
    {
        Create(Record, "record");
        var copy = OpenAt();
        copy.Load();
        var good = File.ReadAllBytes(_share.DocumentPath(Record));
        File.Delete(_share.DocumentPath(Record));
        copy.Load();
        _clock.Advance(InPlaceEdits.SettleTime);
        Assert.Single(copy.Load().Outside);
        Directory.CreateDirectory(Path.GetDirectoryName(_share.TrashPath(Record))!);
        TestFolder.WriteAsKiler(_share.TrashPath(Record), DocumentFormat.Next(Record, 1, Content("junk")));

        copy.Load();

        Assert.Equal(good, File.ReadAllBytes(KeptPath(Record)));
    }

    /// <summary>
    /// A move does not take the place of a document that keeps other last good bytes in the place it moves to, here those
    /// that this Kiler wrote; the file there is read as a change of that document (second security review of M2.7a).
    /// </summary>
    [Fact]
    public void Load_AMoveOntoADocumentWithOtherLastGoodBytes_IsNoMove_AndThoseBytesStay()
    {
        var copy = OpenAt();
        copy.Create(Record, Content("mine"));
        copy.Load();
        var mine = File.ReadAllBytes(_share.DocumentPath(Record));
        File.Delete(_share.DocumentPath(Record));
        copy.Load();

        // Long enough gone that a file in the trash is no late listing of a move.
        _clock.Advance(InPlaceEdits.SettleTime);
        Assert.Single(copy.Load().Outside);
        var junk = DocumentFormat.New(Record, Content("junk"));
        Directory.CreateDirectory(Path.GetDirectoryName(_share.TrashPath(Record))!);
        TestFolder.WriteAsKiler(_share.TrashPath(Record), junk);
        copy.Load();
        File.Move(_share.TrashPath(Record), _share.DocumentPath(Record));

        copy.Load();

        Assert.Equal(mine, File.ReadAllBytes(KeptPath(Record)));
    }

    /// <summary>
    /// A slot that anyone puts on the share for a version that the copy has the bytes of is not taken; the slot of that version
    /// comes from those bytes once a new version replaces it (second security review of M2.7a).
    /// </summary>
    [Fact]
    public void Load_ASlotOfTheShareForAVersionThatTheCopyHas_IsNotTaken()
    {
        Create(Record, "v1");
        var copy = Open();
        copy.Load();
        Directory.CreateDirectory(Path.GetDirectoryName(_share.HistoryPath(Record, 2))!);
        TestFolder.WriteAsKiler(_share.HistoryPath(Record, 2), DocumentFormat.Next(Record, 1, Content("forged")));
        Replace(Record, 1, "v2");
        copy.Load();
        Assert.Equal(["v1.json"], SlotCopies(Record));
        var real = File.ReadAllBytes(_share.DocumentPath(Record));
        File.Delete(_share.HistoryPath(Record, 2));
        Replace(Record, 2, "v3");

        copy.Load();

        Assert.Equal(real, File.ReadAllBytes(SlotCopyPath(Record, 2)));
    }

    /// <summary>
    /// A delete whose trash folder the load cannot list is gone at first, and a load that lists the trash within
    /// <see cref="InPlaceEdits.SettleTime"/> follows the move before anything is reported (second code review and third security
    /// review of M2.7a).
    /// </summary>
    [Fact]
    public void Load_ADeleteWhileTheTrashCannotBeListed_IsFollowedByTheNextLoad()
    {
        Create(Record, "record");
        var copy = OpenAt();
        copy.Load();
        _share.DeleteDocument(Record, 1);
        var trash = Path.GetDirectoryName(_share.TrashPath(Record))!;
        _files.Before = (step, path) =>
        {
            if (step == nameof(IFileSystem.List) && path == trash)
            {
                throw new IOException("A made-up failure of the listing.");
            }
        };

        var load = copy.Load();

        Assert.Equal(Record, Assert.Single(load.Gone));
        Assert.Empty(load.Outside);
        _files.Before = null;
        Assert.Empty(copy.Load().Gone);
        _clock.Advance(InPlaceEdits.SettleTime);
        Assert.Empty(copy.Load().Outside);
        Assert.False(File.Exists(KeptPath(Record)), "The deleted document is kept apart.");
        Assert.Equal(Record, Assert.Single(copy.Trash()).Key);
    }

    /// <summary>A damaged file of the size of the good one fits where the good one was, as it has no bytes in the copy (code review of M2.7a).</summary>
    [Fact]
    public void Load_ADocumentDamagedInAFullCopy_IsTakenAsDamaged()
    {
        Create(Record, "good");
        var size = new FileInfo(_share.DocumentPath(Record)).Length;
        var copy = Open(DataCopyTests.Limits(bytes: size + (size / 2)));
        copy.Load();
        TestFolder.WriteAsKiler(_share.DocumentPath(Record), Encoding.UTF8.GetBytes("{" + new string(' ', (int)size - 1)));

        var load = copy.Load();

        Assert.Equal(new OutsideChange(Record, InTrash: false, OutsideReason.Damaged, LastGood: 1), Assert.Single(load.Outside));
        Assert.Empty(load.Skipped);
        Assert.True(File.Exists(KeptPath(Record)), "The last good bytes of the damaged document are not kept apart.");
    }

    [Fact]
    public void Load_AMoveToTheTrashInAFullCopy_IsFollowed()
    {
        Create(Record, "record");
        Create(OtherRecord, "other");
        var copy = Open(DataCopyTests.Limits(documents: 2));
        copy.Load();
        _share.DeleteDocument(Record, 1);

        var load = copy.Load();

        Assert.Empty(load.Skipped);
        Assert.Equal(Record, Assert.Single(copy.Trash()).Key);
    }

    [Fact]
    public void Create_ThroughTheCopy_OverADocumentGoneOutsideTheTrash_DropsItsKeptBytes()
    {
        Create(Record, "record");
        var copy = OpenAt();
        copy.Load();
        File.Delete(_share.DocumentPath(Record));
        copy.Load();
        Assert.True(File.Exists(KeptPath(Record)), "The gone document is not kept apart.");

        Assert.Equal(WriteOutcome.Written, copy.Create(Record, Content("again")).Outcome);

        Assert.False(File.Exists(KeptPath(Record)), "The kept bytes stay after a create of Kiler.");
        _clock.Advance(InPlaceEdits.SettleTime);
        Assert.Empty(copy.Load().Outside);
    }

    /// <summary>
    /// The listing can still show the old stamp through the SMB cache, here with the times of the old file put back on a new
    /// one of the same size; the copy reads the share's file all the same.
    /// </summary>
    [Fact]
    public void Replace_ThroughTheCopy_WhoseBytesCannotBeWrittenIntoIt_LeavesTheCopy_AndTheNextLoadReadsTheShare()
    {
        var copy = Open();
        copy.Create(Record, Content("first"));
        copy.Load();
        var path = _share.DocumentPath(Record);
        var created = File.GetCreationTimeUtc(path);
        var written = File.GetLastWriteTimeUtc(path);
        using (new FileStream(CopyPath(Record), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Equal(WriteOutcome.Written, copy.Replace(Record, 1, Content("fixed")).Outcome);
        }

        File.SetCreationTimeUtc(path, created);
        File.SetLastWriteTimeUtc(path, written);

        Assert.Equal(1, Assert.Single(Open().Documents()).Read.Document?.Version);
        Assert.Equal(2, Assert.Single(copy.Load().Changed).Read.Document?.Version);
        Assert.Equal(File.ReadAllBytes(_share.DocumentPath(Record)), File.ReadAllBytes(CopyPath(Record)));
    }

    /// <summary>An entry of the index that is not whole is left out, and its document is read again.</summary>
    [Theory]
    [InlineData("a good one with kept bytes")]
    [InlineData("kept bytes of no length")]
    [InlineData("a history from version 0")]
    [InlineData("gone without the reason")]
    [InlineData("a reason of gone without the time")]
    [InlineData("damaged without the reason")]
    [InlineData("a change outside Kiler without any bytes")]
    public void Open_AnEntryOfTheSecondFormatThatIsNotWhole_IsLeftOut(string change)
    {
        Create(Record, "record");
        Open().Load();
        ChangeIndex(index =>
        {
            var entry = index["files"]![0]!.AsObject();
            // Other bytes than the document's, so an entry that is taken shows as a change outside Kiler at the next load.
            JsonObject Kept(int length) => new() { ["version"] = 1, ["hash"] = new string('0', 64), ["length"] = length, ["own"] = false };
            switch (change)
            {
                case "a good one with kept bytes":
                    entry["kept"] = Kept((int)entry["copied"]!);
                    break;
                case "kept bytes of no length":
                    entry["outside"] = nameof(OutsideReason.OutOfOrder);
                    entry["kept"] = Kept(0);
                    break;
                case "a history from version 0":
                    entry["historyFrom"] = 0;
                    break;
                case "gone without the reason":
                    entry["goneAt"] = "2026-01-01T00:00:00+00:00";
                    entry["copied"] = 0;
                    entry["kept"] = Kept(10);
                    break;
                case "a reason of gone without the time":
                    entry["outside"] = nameof(OutsideReason.Gone);
                    entry["copied"] = 0;
                    entry["kept"] = Kept(10);
                    break;
                case "damaged without the reason":
                    entry["damage"] = "NotJson";
                    entry["copied"] = 0;
                    break;
                case "a change outside Kiler without any bytes":
                    entry["outside"] = nameof(OutsideReason.EditedInPlace);
                    entry["copied"] = 0;
                    break;
            }
        });

        // The file that the kept bytes of a gone entry name is there, so the entry is not left out for a missing file.
        if (change.Contains("gone", StringComparison.Ordinal))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(KeptPath(Record))!);
            File.WriteAllText(KeptPath(Record), "made up");
        }

        var copy = Open();

        Assert.Empty(copy.Documents());
        var load = copy.Load();
        Assert.Equal(Record, Assert.Single(load.Changed).Key);
        Assert.Empty(load.Outside);
    }

    [Fact]
    public void Open_ASlotOfTheIndexWithANameThatFailsTheCheck_IsLeftOut_AndItsFileGoes()
    {
        Create(Record, "first");
        var copy = OpenAt();
        copy.Load();
        Replace(Record, 1, "second");
        copy.Load();
        ChangeIndex(index => index["history"]![0]!["id"] = "..");
        _clock.Advance(TimeSpan.FromMinutes(11));

        OpenAt();

        Assert.Empty(SlotCopies(Record));
    }

    /// <summary>A slot that the listing of the share gives pushes out the slot of the share that the copy took first (second code review of M2.7a).</summary>
    [Fact]
    public void Load_ASlotFromTheShare_PushesOutTheOldestSlotOfTheShare()
    {
        Create(Record, "v1");
        Create(OtherRecord, "v1");
        var copy = OpenAt(DataCopyTests.Limits(historySlots: 2));
        copy.Load();
        Replace(Record, 1, "v2");
        _clock.Advance(TimeSpan.FromMinutes(1));
        copy.Load();
        Replace(OtherRecord, 1, "v2");
        Replace(OtherRecord, 2, "v3");
        _clock.Advance(TimeSpan.FromMinutes(1));

        Assert.Empty(copy.Load().Skipped);

        Assert.Empty(SlotCopies(Record));
        Assert.Equal(["v1.json", "v2.json"], SlotCopies(OtherRecord));
    }

    /// <summary>A slot of this Kiler pushes out the slots of the share first, so they cannot block it (second code review of M2.7a).</summary>
    [Fact]
    public void Replace_ThroughTheCopy_PushesOutTheOldestSlotOfTheShare()
    {
        Create(OtherRecord, "v1");
        var copy = OpenAt(DataCopyTests.Limits(historySlots: 1));
        copy.Load();
        Replace(OtherRecord, 1, "v2");
        copy.Load();
        Assert.Equal(["v1.json"], SlotCopies(OtherRecord));
        copy.Create(Record, Content("v1"));

        copy.Replace(Record, 1, Content("v2"));

        Assert.Empty(SlotCopies(OtherRecord));
        Assert.Equal(["v1.json"], SlotCopies(Record));
    }

    [Fact]
    public void Open_KeepsAYoungEmptyHistoryFolder_AsAWriteOfAnotherSessionMayFillIt()
    {
        Create(Record, "record");
        var copy = OpenAt();
        copy.Load();
        Replace(Record, 1, "second");
        copy.Load();
        var folder = Path.GetDirectoryName(SlotCopyPath(Record, 1))!;
        var empty = Path.Combine(Path.GetDirectoryName(folder)!, "b72e09");
        Directory.CreateDirectory(empty);
        Directory.SetLastWriteTimeUtc(empty, _clock.GetUtcNow().UtcDateTime);
        _clock.Advance(TimeSpan.FromMinutes(11));
        Directory.SetLastWriteTimeUtc(empty, _clock.GetUtcNow().UtcDateTime.AddMinutes(-1));

        OpenAt();

        Assert.True(Directory.Exists(empty), "A young empty history folder is gone.");
        Assert.True(Directory.Exists(folder), "A history folder with a slot is gone.");
    }

    /// <summary>A load holds the listings of all its folders at once, so it keeps no more links and bad names than it reports (security review of M2.7a).</summary>
    [Fact]
    public void ListFolder_KeepsAtMostTheLinksAndBadNamesThatALoadReports_AndCountsTheRest()
    {
        for (var i = 0; i < 5; i++)
        {
            File.WriteAllText(Path.Combine(_share.Root, DataFolder.Records, $"Bad{i}.json"), "made up");
        }

        using var listed = _share.ListFolder(DataFolder.Records, inTrash: false, maxEntries: 100, maxReported: 2);

        Assert.Equal(2, listed.Listing.BadNames.Count);
        Assert.Equal(3, listed.Untold);
    }

    /// <summary>
    /// The overwrite that the guarded index allowed fails: the entry stays as it was guarded, with the last good bytes in
    /// data\ and apart, and the next load reads the share's file again.
    /// </summary>
    [Fact]
    public void Load_WhoseOverwriteFailsAfterTheGuardedIndex_LeavesTheLastGoodBytesNamed()
    {
        Create(Record, "good");
        var copy = OpenAt();
        copy.Load();
        var good = File.ReadAllBytes(_share.DocumentPath(Record));
        var edited = DocumentFormat.New(Record, Content("edited by hand"));
        TestFolder.EditInPlace(_share.DocumentPath(Record), edited);
        _files.Owners.Set(_share.DocumentPath(Record), TestOwners.Other);
        LoadResult load;
        using (new FileStream(CopyPath(Record), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            load = copy.Load();
        }

        Assert.Equal(new SkippedEntry(DataFolder.Records, "8f3a1c.json", SkipReason.NotCopied), Assert.Single(load.Skipped));
        var restarted = OpenAt();
        var given = Assert.Single(restarted.Documents()).Read;
        Assert.Equal("good", (string?)given.Document?.Content["name"]);

        // The owner of the edit is never given with the bytes of the good version (second security review of M2.7a).
        Assert.Equal(TestOwners.Me, given.Owner);
        Assert.Equal(good, File.ReadAllBytes(KeptPath(Record)));
        Assert.Equal("edited by hand", Name(Assert.Single(restarted.Load().Changed)));
        Assert.Equal(edited, File.ReadAllBytes(CopyPath(Record)));
        Assert.Equal(good, File.ReadAllBytes(KeptPath(Record)));
    }

    /// <summary>Anyone can make every document change, so one load lists at most so many history folders, and the next load the rest (second security review of M2.7a).</summary>
    [Fact]
    public void Load_ListsAtMostTheHistoryFoldersOfALoad_AndTheRestNextTime()
    {
        Create(Record, "v1");
        Create(OtherRecord, "v1");
        var copy = Open(DataCopyTests.Limits(historyListings: 1));
        copy.Load();
        Replace(Record, 1, "v2");
        Replace(Record, 2, "v3");
        Replace(OtherRecord, 1, "v2");
        Replace(OtherRecord, 2, "v3");
        var lists = CountHistoryListings();

        copy.Load();

        Assert.Equal(1, lists());
        copy.Load();
        Assert.Equal(2, lists());
        copy.Load();
        Assert.Equal(2, lists());
        Assert.Equal(["v1.json", "v2.json"], SlotCopies(Record));
        Assert.Equal(["v1.json", "v2.json"], SlotCopies(OtherRecord));
    }

    /// <summary>One load reads at most so many bytes of slots; what it leaves waits for the next load (second security review of M2.7a).</summary>
    [Fact]
    public void Load_ReadsAtMostTheSlotsOfALoad_AndTheRestNextTime()
    {
        Create(Record, "v1");
        var copy = Open();
        copy.Load();
        for (var version = 1; version <= 3; version++)
        {
            Replace(Record, version, "v" + (version + 1));
        }

        var slot = new FileInfo(_share.HistoryPath(Record, 3)).Length;
        copy = Open(DataCopyTests.Limits(historyReads: slot + (slot / 2)));
        _files.Reads = 0;

        copy.Load();

        Assert.Equal(["v1.json", "v3.json"], SlotCopies(Record));
        copy.Load();
        Assert.Equal(["v1.json", "v2.json", "v3.json"], SlotCopies(Record));
    }

    /// <summary>
    /// The index on the disk is behind when a slot of the copy is not in it yet, here as the index could not be written after a
    /// replace through the copy; the bytes of the version in data\ are not overwritten until an index names its slot (second
    /// code review of M2.7a).
    /// </summary>
    [Fact]
    public void Load_WhileTheIndexIsBehind_DoesNotOverwriteTheLastGoodBytesBeforeAnIndexNamesTheirSlot()
    {
        var copy = OpenAt();
        copy.Create(Record, Content("v1"));
        copy.Load();
        var index = Path.Combine(_copy, "index.json");
        using (new FileStream(index, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Equal(WriteOutcome.Written, copy.Replace(Record, 1, Content("v2")).Outcome);
        }

        Replace(Record, 2, "v3");
        using (new FileStream(index, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            copy.Load();
        }

        Assert.Equal(1, Assert.Single(OpenAt().Documents()).Read.Document?.Version);
    }

    /// <summary>
    /// A kept file that an index stopped naming, whose delete waits for an index that can be written, is named again by a
    /// change of the same load: the guarded index of another change must not delete it (second security review of M2.7a).
    /// </summary>
    [Fact]
    public void Load_AKeptFileThatAnEarlierIndexStoppedNaming_StaysWhenItIsNamedAgain()
    {
        Create(Record, "record");
        Create(OtherRecord, "other");
        var copy = OpenAt();
        copy.Load();
        TestFolder.EditInPlace(_share.DocumentPath(OtherRecord), DocumentFormat.New(OtherRecord, Content("edited by hand")));
        copy.Load();
        var newer = DocumentFormat.Next(OtherRecord, 1, Content("newer"));
        TestFolder.WriteAsKiler(_share.DocumentPath(OtherRecord), newer);
        using (new FileStream(Path.Combine(_copy, "index.json"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            copy.Load();
        }

        File.WriteAllText(_share.DocumentPath(OtherRecord), "{", Encoding.UTF8);
        TestFolder.EditInPlace(_share.DocumentPath(Record), DocumentFormat.New(Record, Content("edited by hand")));

        copy.Load();

        Assert.Equal(newer, File.ReadAllBytes(KeptPath(OtherRecord)));
    }

    /// <summary>A history folder that failed is listed after the others, so it does not hold them back (second security review of M2.7a).</summary>
    [Fact]
    public void Load_AHistoryFolderThatFailed_IsListedAfterTheOthers()
    {
        Create(Record, "v1");
        Create(OtherRecord, "v1");
        var copy = Open(DataCopyTests.Limits(historyListings: 1));
        copy.Load();
        foreach (var key in (DocumentKey[])[Record, OtherRecord])
        {
            Replace(key, 1, "v2");
            Replace(key, 2, "v3");
        }

        var history = Path.GetDirectoryName(_share.HistoryPath(Record, 1))!;
        _files.Before = (step, path) =>
        {
            if (step == nameof(IFileSystem.List) && path == history)
            {
                throw new IOException("A made-up failure of the listing.");
            }
        };

        copy.Load();
        copy.Load();

        Assert.Equal(["v1.json", "v2.json"], SlotCopies(OtherRecord));
    }

    /// <summary>
    /// A document in the trash with the id of a real one, made to have the real one's bytes and then taken away, is no move
    /// onto the real one, which keeps its last good bytes (third security review of M2.7a).
    /// </summary>
    [Fact]
    public void Load_ADocumentOfTheTrashWithTheBytesOfARealOne_TakenAway_LeavesTheRealOneAsItWas()
    {
        Create(Record, "real");
        var real = File.ReadAllBytes(_share.DocumentPath(Record));
        var copy = OpenAt();
        copy.Load();
        _clock.Advance(TimeSpan.FromMinutes(1));
        Directory.CreateDirectory(Path.GetDirectoryName(_share.TrashPath(Record))!);
        TestFolder.WriteAsKiler(_share.TrashPath(Record), DocumentFormat.Next(Record, 9, Content("junk")));
        copy.Load();
        TestFolder.WriteAsKiler(_share.TrashPath(Record), real);
        copy.Load();
        File.Delete(_share.TrashPath(Record));
        copy.Load();
        File.Delete(_share.DocumentPath(Record));

        copy.Load();

        Assert.Equal(real, File.ReadAllBytes(KeptPath(Record)));
    }

    /// <summary>
    /// A copy of a real document put in the trash and taken away does not make the real one look new or of another Kiler, so
    /// its last good bytes keep their protection (third security review of M2.7a).
    /// </summary>
    [Fact]
    public void Load_ACopyOfARealDocumentPutInTheTrashAndTakenAway_KeepsWhatTheCopyKnowsOfTheRealOne()
    {
        var copy = OpenAt(DataCopyTests.Limits(documents: 2));
        copy.Create(Record, Content("mine"));
        copy.Load();
        _clock.Advance(TimeSpan.FromMinutes(1));
        Directory.CreateDirectory(Path.GetDirectoryName(_share.TrashPath(Record))!);
        TestFolder.WriteAsKiler(_share.TrashPath(Record), File.ReadAllBytes(_share.DocumentPath(Record)));
        copy.Load();
        File.Delete(_share.TrashPath(Record));
        copy.Load();
        _clock.Advance(TimeSpan.FromMinutes(1));
        File.Delete(_share.DocumentPath(Record));
        copy.Load();
        Create(OtherRecord, "other");
        copy.Load();
        Create(ThirdRecord, "third");
        _clock.Advance(TimeSpan.FromMinutes(1));

        var load = copy.Load();

        Assert.Equal(new SkippedEntry(DataFolder.Records, "c3d4e5.json", SkipReason.OverBudget), Assert.Single(load.Skipped));
        Assert.True(File.Exists(KeptPath(Record)), "The last good bytes that this Kiler wrote made room.");
    }

    /// <summary>
    /// A document in the trash with the id of a real one shares its history folder: no slot is taken there, as anyone could
    /// put slots of versions that the real one reaches later (third security review of M2.7a).
    /// </summary>
    [Fact]
    public void Load_TakesNoSlotsOfADocumentThatIsBothInItsFolderAndInTheTrash()
    {
        Create(Record, "v1");
        for (var version = 1; version <= 5; version++)
        {
            Replace(Record, version, "v" + (version + 1));
        }

        var copy = Open();
        copy.Load();
        Directory.CreateDirectory(Path.GetDirectoryName(_share.TrashPath(Record))!);
        TestFolder.WriteAsKiler(_share.TrashPath(Record), DocumentFormat.Next(Record, 4, Content("trash")));
        copy.Load();
        TestFolder.WriteAsKiler(_share.TrashPath(Record), DocumentFormat.Next(Record, 11, Content("trash")));
        for (var version = 7; version <= 11; version++)
        {
            TestFolder.WriteAsKiler(_share.HistoryPath(Record, version), DocumentFormat.Next(Record, version - 1, Content("forged")));
        }

        copy.Load();

        Assert.Empty(SlotCopies(Record));
    }

    /// <summary>The version of a file that is not a version that Kiler made says nothing of the history to take (third security review of M2.7a).</summary>
    [Fact]
    public void Load_TakesNoSlotsAboveTheLastGoodVersion_WhateverTheShareFileSays()
    {
        Create(Record, "v1");
        var copy = Open();
        copy.Load();
        Replace(Record, 1, "v2");
        var history = Path.GetDirectoryName(_share.HistoryPath(Record, 1))!;
        _files.Before = (step, path) =>
        {
            if (step == nameof(IFileSystem.List) && path == history)
            {
                throw new IOException("A made-up failure of the listing.");
            }
        };
        copy.Load();
        _files.Before = null;
        TestFolder.EditInPlace(_share.DocumentPath(Record), DocumentFormat.Next(Record, 99, Content("edited by hand")));
        TestFolder.WriteAsKiler(_share.HistoryPath(Record, 50), DocumentFormat.Next(Record, 49, Content("forged")));

        copy.Load();

        Assert.Equal(["v1.json"], SlotCopies(Record));
    }

    /// <summary>A slot that the copy took from the share gives way to the bytes that the copy has of its version (third security review of M2.7a).</summary>
    [Fact]
    public void Load_ASlotFromTheShare_GivesWayToTheBytesThatTheCopyHasOfItsVersion()
    {
        Create(Record, "v1");
        Open().Load();
        Replace(Record, 1, "v2");
        Replace(Record, 2, "v3");
        Open().Load();
        Assert.Equal(["v1.json", "v2.json"], SlotCopies(Record));
        ChangeIndex(index => index["files"]!.AsArray().Clear());
        var other = DocumentFormat.Next(Record, 1, Content("other"));
        TestFolder.WriteAsKiler(_share.DocumentPath(Record), other);
        var copy = Open();
        copy.Load();
        File.Delete(_share.HistoryPath(Record, 2));
        Replace(Record, 2, "v3 again");

        copy.Load();

        Assert.Equal(other, File.ReadAllBytes(SlotCopyPath(Record, 2)));
    }

    /// <summary>
    /// A document that goes from its folder while a late listing of the trash has its last good bytes loses nothing, so it is
    /// not kept apart or reported, and the entry in the trash keeps its stay and that this Kiler wrote the bytes (third and
    /// fourth security reviews of M2.7a).
    /// </summary>
    [Fact]
    public void Load_ADocumentThatGoesWhileALateListingOfTheOtherPlaceHasItsBytes_IsNotReported_AndThatEntryKeepsWhatItWas()
    {
        var copy = OpenAt();
        copy.Create(Record, Content("mine"));
        copy.Load();
        _clock.Advance(TimeSpan.FromMinutes(1));
        Directory.CreateDirectory(Path.GetDirectoryName(_share.TrashPath(Record))!);
        TestFolder.WriteAsKiler(_share.TrashPath(Record), File.ReadAllBytes(_share.DocumentPath(Record)));
        copy.Load();
        File.Delete(_share.DocumentPath(Record));
        copy.Load();
        _clock.Advance(InPlaceEdits.SettleTime);

        Assert.Empty(copy.Load().Outside);
        Assert.False(File.Exists(KeptPath(Record)), "The document, whose bytes the trash has, is kept apart.");
        var entry = JsonNode.Parse(File.ReadAllBytes(Path.Combine(_copy, "index.json")))!["files"]!.AsArray().Single()!;
        Assert.True((bool)entry["inTrash"]!, "The entry left is not the one in the trash.");
        Assert.True((bool)entry["own"]!, "The entry in the trash lost that this Kiler wrote the bytes.");
    }

    /// <summary>A copy that anyone put in the other place long ago is no late listing: a document that goes is kept apart and reported (fourth security review of M2.7a).</summary>
    [Fact]
    public void Load_ADocumentThatGoesWhileAnOlderEntryOfTheOtherPlaceHasItsBytes_IsKeptApartAndReported()
    {
        Create(Record, "record");
        var copy = OpenAt();
        copy.Load();
        Directory.CreateDirectory(Path.GetDirectoryName(_share.TrashPath(Record))!);
        TestFolder.WriteAsKiler(_share.TrashPath(Record), File.ReadAllBytes(_share.DocumentPath(Record)));
        copy.Load();
        _clock.Advance(InPlaceEdits.SettleTime);
        copy.Load();
        File.Delete(_share.DocumentPath(Record));
        copy.Load();
        _clock.Advance(InPlaceEdits.SettleTime);

        Assert.Equal(new OutsideChange(Record, InTrash: false, OutsideReason.Gone, LastGood: 1), Assert.Single(copy.Load().Outside));
        Assert.True(File.Exists(KeptPath(Record)), "The document is not kept apart.");
    }

    /// <summary>A move with the same bytes does not take the place of a young entry of this Kiler either, which would lose that this Kiler wrote them (fourth security review of M2.7a).</summary>
    [Fact]
    public void Load_AMoveWithTheSameBytesOntoAYoungEntryOfThisKiler_IsNoMove_AndTheEntryStaysItsOwn()
    {
        var copy = OpenAt();
        copy.Create(Record, Content("mine"));
        copy.Load();
        var mine = File.ReadAllBytes(_share.DocumentPath(Record));
        Directory.CreateDirectory(Path.GetDirectoryName(_share.TrashPath(Record))!);
        TestFolder.WriteAsKiler(_share.TrashPath(Record), mine);
        copy.Load();
        File.Delete(_share.TrashPath(Record));
        File.Delete(_share.DocumentPath(Record));
        TestFolder.WriteAsKiler(_share.DocumentPath(Record), mine);

        copy.Load();

        var live = JsonNode.Parse(File.ReadAllBytes(Path.Combine(_copy, "index.json")))!["files"]!.AsArray().Single(entry => !(bool)entry!["inTrash"]!)!;
        Assert.True((bool)live["own"]!, "The document lost that this Kiler wrote its bytes.");
    }

    /// <summary>A move does not take the place of a young entry with other last good bytes, which a restore would then lose (fourth security review of M2.7a).</summary>
    [Fact]
    public void Load_AMoveOntoAYoungEntryWithOtherLastGoodBytes_IsNoMove_AndThoseBytesAreKept()
    {
        Create(Record, "real");
        var real = File.ReadAllBytes(_share.DocumentPath(Record));
        var copy = OpenAt();
        copy.Load();
        var junk = DocumentFormat.New(Record, Content("junk"));
        Directory.CreateDirectory(Path.GetDirectoryName(_share.TrashPath(Record))!);
        TestFolder.WriteAsKiler(_share.TrashPath(Record), junk);
        copy.Load();
        File.Delete(_share.TrashPath(Record));
        TestFolder.WriteAsKiler(_share.DocumentPath(Record), junk);

        copy.Load();

        Assert.Equal(real, File.ReadAllBytes(KeptPath(Record)));
    }

    /// <summary>A gone document that its own place lists again is no move to the other place (fourth security review of M2.7a).</summary>
    [Fact]
    public void Load_AGoneDocumentListedAgainInItsPlace_IsNoMoveToTheOtherPlace()
    {
        Create(Record, "record");
        var copy = OpenAt();
        copy.Load();
        var bytes = File.ReadAllBytes(_share.DocumentPath(Record));
        File.Delete(_share.DocumentPath(Record));
        copy.Load();
        TestFolder.WriteAsKiler(_share.DocumentPath(Record), bytes);
        Directory.CreateDirectory(Path.GetDirectoryName(_share.TrashPath(Record))!);
        TestFolder.WriteAsKiler(_share.TrashPath(Record), DocumentFormat.New(Record, Content("trash")));

        var load = copy.Load();

        Assert.Empty(load.Gone);
        Assert.Equal(Record, Assert.Single(load.Changed).Key);
        Assert.Equal(Record, Assert.Single(OpenAt().Documents()).Key);
    }

    /// <summary>A move does not take the place of a young entry of this Kiler, here one that it has just created (fourth security review of M2.7a).</summary>
    [Fact]
    public void Load_AMoveOntoADocumentThatThisKilerHasJustWritten_IsNoMove()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_share.TrashPath(Record))!);
        TestFolder.WriteAsKiler(_share.TrashPath(Record), DocumentFormat.New(Record, Content("junk")));
        var copy = OpenAt();
        copy.Load();
        Assert.Equal(WriteOutcome.Written, copy.Create(Record, Content("mine")).Outcome);
        var mine = File.ReadAllBytes(_share.DocumentPath(Record));
        File.Delete(_share.TrashPath(Record));
        TestFolder.WriteAsKiler(_share.DocumentPath(Record), DocumentFormat.Next(Record, 1, Content("other")));

        copy.Load();

        Assert.Equal(mine, File.ReadAllBytes(SlotCopyPath(Record, 1)));
    }

    [Fact]
    public void Replace_ThroughTheCopy_TakesThePlaceOfASlotFromTheShareWithOtherBytes()
    {
        Create(Record, "v1");
        Open().Load();
        Replace(Record, 1, "v2");
        Replace(Record, 2, "v3");
        Open().Load();
        ChangeIndex(index => index["files"]!.AsArray().Clear());
        var other = DocumentFormat.Next(Record, 1, Content("other"));
        TestFolder.WriteAsKiler(_share.DocumentPath(Record), other);
        var copy = Open();
        copy.Load();
        File.Delete(_share.HistoryPath(Record, 2));

        Assert.Equal(WriteOutcome.Written, copy.Replace(Record, 2, Content("v3 again")).Outcome);

        Assert.Equal(other, File.ReadAllBytes(SlotCopyPath(Record, 2)));
    }

    /// <summary>
    /// A move does not take the place of a young entry whose last good bytes, kept apart for an edit in place, are of this
    /// Kiler: that it wrote them would be lost, and they could be evicted (fifth security review of M2.7a).
    /// </summary>
    [Fact]
    public void Load_AMoveOntoAYoungEntryWhoseKeptBytesAreOfThisKiler_IsNoMove()
    {
        var copy = OpenAt();
        copy.Create(Record, Content("mine"));
        var mine = File.ReadAllBytes(_share.DocumentPath(Record));
        TestFolder.EditInPlace(_share.DocumentPath(Record), DocumentFormat.New(Record, Content("edited by hand")));
        Directory.CreateDirectory(Path.GetDirectoryName(_share.TrashPath(Record))!);
        TestFolder.WriteAsKiler(_share.TrashPath(Record), mine);
        copy.Load();
        File.Delete(_share.TrashPath(Record));
        TestFolder.EditInPlace(_share.DocumentPath(Record), DocumentFormat.New(Record, Content("edited again")));

        copy.Load();

        var live = JsonNode.Parse(File.ReadAllBytes(Path.Combine(_copy, "index.json")))!["files"]!.AsArray().Single(entry => !(bool)entry!["inTrash"]!)!;
        Assert.True((bool)live["kept"]!["own"]!, "The kept bytes of the document lost that this Kiler wrote them.");
        Assert.Equal(mine, File.ReadAllBytes(KeptPath(Record)));
    }

    /// <summary>
    /// A document in its folder with the id of one in the trash does not keep the copy from taking the version that the one in
    /// the trash had as its slot, in the history of the trash (fifth security review of M2.7a).
    /// </summary>
    [Fact]
    public void Load_ANewVersionInTheTrashBesideADocumentWithItsId_KeepsTheVersionThatTheCopyHadInTheHistoryOfTheTrash()
    {
        Create(Record, "v1");
        var copy = OpenAt();
        copy.Load();
        var v1 = File.ReadAllBytes(_share.DocumentPath(Record));
        Assert.Equal(WriteOutcome.Deleted, _share.DeleteDocument(Record, 1).Outcome);
        copy.Load();
        TestFolder.WriteAsKiler(_share.DocumentPath(Record), DocumentFormat.New(Record, Content("another")));
        TestFolder.WriteAsKiler(_share.TrashPath(Record), DocumentFormat.Next(Record, 1, Content("v2")));

        copy.Load();

        var trashHistory = Path.Combine(_copy, "data", "trash", "history", "records", "8f3a1c");
        Assert.Equal(v1, File.ReadAllBytes(Path.Combine(trashHistory, "v1.json")));
        Assert.Empty(SlotCopies(Record));

        // A file of the history of the trash that no entry names goes at the open, as in the other folders of the copy.
        var stray = Path.Combine(trashHistory, "v9.json");
        File.WriteAllText(stray, "made up");
        _clock.Advance(TimeSpan.FromMinutes(11));
        OpenAt();
        Assert.False(File.Exists(stray), "A file of the history of the trash that no entry names is still there.");
        Assert.True(File.Exists(Path.Combine(trashHistory, "v1.json")), "A slot of the history of the trash is gone.");
    }

    /// <summary>The slots of the share of a document that is only in the trash go to the history of the trash (fifth security review of M2.7a).</summary>
    [Fact]
    public void Load_TheSlotsOfTheShareOfADocumentOnlyInTheTrash_GoToTheHistoryOfTheTrash()
    {
        Create(Record, "v1");
        var copy = Open();
        copy.Load();
        Assert.Equal(WriteOutcome.Deleted, _share.DeleteDocument(Record, 1).Outcome);
        copy.Load();
        var v2 = DocumentFormat.Next(Record, 1, Content("v2"));
        TestFolder.WriteAsKiler(_share.HistoryPath(Record, 2), v2);
        TestFolder.WriteAsKiler(_share.TrashPath(Record), DocumentFormat.Next(Record, 2, Content("v3")));

        copy.Load();

        Assert.Equal(v2, File.ReadAllBytes(Path.Combine(_copy, "data", "trash", "history", "records", "8f3a1c", "v2.json")));
        Assert.Empty(SlotCopies(Record));
    }

    /// <summary>
    /// A slot that the copy took from the bytes of a document in the trash does not take the place of the slot of that version
    /// of the document with its id in its folder (fifth security review of M2.7a).
    /// </summary>
    [Fact]
    public void Load_ASlotFromTheTrash_DoesNotBlockTheSlotOfTheDocumentInItsFolder()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_share.TrashPath(Record))!);
        TestFolder.WriteAsKiler(_share.TrashPath(Record), DocumentFormat.New(Record, Content("z1")));
        var copy = OpenAt();
        copy.Load();
        _clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(WriteOutcome.Written, copy.Create(Record, Content("h1")).Outcome);
        var h1 = File.ReadAllBytes(_share.DocumentPath(Record));
        File.Delete(_share.DocumentPath(Record));
        copy.Load();
        TestFolder.WriteAsKiler(_share.TrashPath(Record), DocumentFormat.Next(Record, 1, Content("z2")));
        copy.Load();
        TestFolder.WriteAsKiler(_share.DocumentPath(Record), h1);
        copy.Load();
        Replace(Record, 1, "v2");

        copy.Load();

        Assert.Equal(h1, File.ReadAllBytes(SlotCopyPath(Record, 1)));
    }

    /// <summary>
    /// The evictions that the plan made for a folder that then cannot be read are not done, and a later folder used the room;
    /// the load takes the copy back within its budget (fifth security review of M2.7a).
    /// </summary>
    [Fact]
    public void Load_ThatEndsOverTheBudget_AsAFolderThatMadeRoomCouldNotBeRead_TakesTheCopyBackWithinIt()
    {
        var large = DocumentKey.InFolder(DataFolder.Records, "e5f6a7");
        var user = DocumentKey.InFolder(DataFolder.Users, "demo.user");
        Assert.Equal(WriteOutcome.Written, _share.CreateDocument(large, Content(new string('x', 3000))).Outcome);
        Assert.Equal(WriteOutcome.Written, _share.CreateDocument(user, Content("u")).Outcome);
        var largeSize = new FileInfo(_share.DocumentPath(large)).Length;
        var userSize = new FileInfo(_share.DocumentPath(user)).Length;
        File.Delete(_share.DocumentPath(user));
        var copy = OpenAt(DataCopyTests.Limits(bytes: largeSize + userSize - 1));
        copy.Load();
        _clock.Advance(TimeSpan.FromMinutes(1));
        File.Delete(_share.DocumentPath(large));
        copy.Load();
        Assert.True(File.Exists(KeptPath(large)), "The leftover is not kept apart.");
        Assert.Equal(WriteOutcome.Written, _share.CreateDocument(user, Content("u")).Outcome);
        Create(Record, new string('y', 2000));
        var users = Path.Combine(_share.Root, DataFolder.Users);
        var elsewhere = Directory.CreateDirectory(Path.Combine(_test.Root, "elsewhere")).FullName;
        File.Copy(_share.DocumentPath(user), Path.Combine(elsewhere, "demo.user.json"));
        TestFolder.SkipWithoutJunctions(users);
        _files.Before = (step, path) =>
        {
            if (step == nameof(IFileSystem.OpenRead) && path == _share.DocumentPath(user) && !File.GetAttributes(users).HasFlag(FileAttributes.ReparsePoint))
            {
                File.Delete(path);
                TestFolder.MakeMountPoint(users, elsewhere);
            }
        };
        _clock.Advance(TimeSpan.FromMinutes(1));

        var load = copy.Load();

        Assert.Contains(new SkippedEntry(DataFolder.Users, null, SkipReason.Unreadable), load.Skipped);
        Assert.Equal(Record, Assert.Single(load.Changed).Key);
        Assert.False(File.Exists(KeptPath(large)), "The copy stayed over its budget.");
    }

    private static JsonObject Content(string name) => new() { ["name"] = name };

    private static string? Name(LoadedDocument document) => (string?)document.Read.Document?.Content["name"];

    private static string[] Keys(IEnumerable<DocumentKey> keys) => [.. keys.Select(key => key.ToString()).Order(StringComparer.Ordinal)];

    private static string[] Keys(IEnumerable<LoadedDocument> documents) => Keys(documents.Select(document => document.Key));

    private DataCopy Open() => DataCopy.Open(_share, _copy, TimeProvider.System);

    private DataCopy Open(CopyLimits limits) => DataCopy.Open(_share, _copy, TimeProvider.System, limits);

    /// <summary>Opens the copy on <see cref="_clock"/>, with the default budget or another one.</summary>
    private DataCopy OpenAt(CopyLimits? limits = null) => DataCopy.Open(_share, _copy, _clock, limits ?? CopyLimits.Default);

    /// <summary>A document on the share, as a write of another Kiler makes it.</summary>
    private void Create(DocumentKey key, string name) => Assert.Equal(WriteOutcome.Written, _share.CreateDocument(key, Content(name)).Outcome);

    /// <summary>A new version of a document on the share, as a write of another Kiler makes it, with the old one in its history slot.</summary>
    private void Replace(DocumentKey key, int version, string name) => Assert.Equal(WriteOutcome.Written, _share.ReplaceDocument(key, version, Content(name)).Outcome);

    /// <summary>Changes the index of the copy as JSON, as a file that is not what a load wrote.</summary>
    private void ChangeIndex(Action<JsonNode> change)
    {
        var path = Path.Combine(_copy, "index.json");
        var index = JsonNode.Parse(File.ReadAllBytes(path))!;
        change(index);
        File.WriteAllText(path, index.ToJsonString());
    }

    /// <summary>Counts from now the listings of the history folders of the share.</summary>
    private Func<int> CountHistoryListings()
    {
        var history = Path.Combine(_share.Root, DataFolder.History) + Path.DirectorySeparatorChar;
        var lists = 0;
        _files.Before = (step, path) =>
        {
            if (step == nameof(IFileSystem.List) && path.StartsWith(history, StringComparison.OrdinalIgnoreCase))
            {
                Interlocked.Increment(ref lists);
            }
        };
        return () => lists;
    }

    /// <summary>
    /// The versions of the slots that the index of the copy names, as the last write of the index has them. The open writes
    /// the index only with the next load, so an index that it corrected still names what it had; the budget counts what is
    /// in memory, which the next load writes.
    /// </summary>
    private int[] IndexSlots()
    {
        var index = JsonNode.Parse(File.ReadAllBytes(Path.Combine(_copy, "index.json")))!;
        return [.. (index["history"]?.AsArray() ?? []).Select(slot => (int)slot!["version"]!).Order()];
    }

    /// <summary>The names of the slots of a document in the history of the copy, in order.</summary>
    private string[] SlotCopies(DocumentKey key)
    {
        var folder = Path.GetDirectoryName(SlotCopyPath(key, 1))!;
        return Directory.Exists(folder) ? [.. Directory.GetFiles(folder).Select(path => Path.GetFileName(path)).Order(StringComparer.Ordinal)] : [];
    }

    /// <summary>The file of a document in the copy: a mirror of the share in <c>data\</c> (decided in M2.7).</summary>
    private string CopyPath(DocumentKey key) => Path.Combine(_copy, "data", key.Folder ?? "", key.Id + ".json");

    /// <summary>The copy of a document in the trash: <c>data\trash\records\8f3a1c.json</c> (M2.7a).</summary>
    private string TrashCopyPath(DocumentKey key) => Path.Combine(_copy, "data", "trash", key.Folder ?? "", key.Id + ".json");

    /// <summary>A history slot in the copy: <c>data\history\records\8f3a1c\v1.json</c> (M2.7a).</summary>
    private string SlotCopyPath(DocumentKey key, int version) => Path.Combine(_copy, "data", "history", key.Folder ?? "", key.Id, $"v{version}.json");

    /// <summary>The last good bytes of a document kept apart: <c>kept\records\8f3a1c.json</c> (M2.7a).</summary>
    private string KeptPath(DocumentKey key) => Path.Combine(_copy, "kept", key.Folder ?? "", key.Id + ".json");

    /// <summary>The last good bytes of a document of the trash kept apart: <c>kept\trash\records\8f3a1c.json</c> (M2.7a).</summary>
    private string KeptTrashPath(DocumentKey key) => Path.Combine(_copy, "kept", "trash", key.Folder ?? "", key.Id + ".json");
}

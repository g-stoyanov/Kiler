using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Time.Testing;

namespace Kiler.Storage.Tests;

/// <summary>
/// The restore from the local copy (M2.9, PLAN M2 step 10): the documents whose last good bytes the copy keeps for a restore
/// (<see cref="DataCopy.ToRestore"/>), forgetting those that should not come back (<see cref="DataCopy.Forget"/>), such as
/// the private folder of someone who has left (<see cref="DataCopy.ForgetPrivate"/>, design 7.9), and writing back a
/// document gone outside the trash (<see cref="DataCopy.WriteBack"/>, M2.9a); over a file that is there, M2.9b, in
/// <see cref="DataCopyWriteBackOverTests"/>.
/// </summary>
public sealed class DataCopyRestoreTests : IDisposable
{
    private const string Me = "demo.user";
    private const string Other = "demo.other";

    private static readonly DocumentKey Record = DocumentKey.InFolder(DataFolder.Records, "8f3a1c");
    private static readonly DocumentKey OtherRecord = DocumentKey.InFolder(DataFolder.Records, "b72e09");
    private static readonly DocumentKey ThirdRecord = DocumentKey.InFolder(DataFolder.Records, "c3d4e5");
    private static readonly DocumentKey TheirRecord = Record.InPrivate(Other);

    private readonly TestFolder _test = new();
    private readonly TestFileSystem _files = new();
    private readonly FakeTimeProvider _clock = new(DateTimeOffset.UtcNow);
    private readonly DataFolder _share;
    private readonly string _copy;

    public DataCopyRestoreTests()
    {
        _share = DataFolder.Create(Path.Combine(_test.Root, "data"), _files);
        _copy = Directory.CreateDirectory(Path.Combine(_test.Root, "copy")).FullName;
    }

    /// <summary>How the history of the share shows versions that a copy did not see (M2.9a).</summary>
    public enum Ahead
    {
        SlotAfter,
        SlotWithOtherBytes,
        FolderAtItsSlot,
        TooManyEntries,
    }

    public void Dispose() => _test.Dispose();

    /// <summary>The list comes from the index, as a load reports each change outside Kiler once (M2.7a note).</summary>
    [Fact]
    public void ToRestore_ADocumentDeletedOutsideKiler_IsListedWithItsLastGoodVersion_OnceALoadHasReportedIt()
    {
        Create(Record, "first");
        Replace(Record, 1, "second");
        Create(OtherRecord, "stays");
        var copy = OpenAt();
        copy.Load();
        File.Delete(_share.DocumentPath(Record));
        copy.Load();

        // Gone for less than the settle time, it can be a move that the SMB cache shows late (M2.7a).
        Assert.Empty(copy.ToRestore());
        _clock.Advance(InPlaceEdits.SettleTime);
        Assert.Single(copy.Load().Outside);

        var gone = new OutsideChange(Record, InTrash: false, OutsideReason.Gone, LastGood: 2);
        Assert.Equal(gone, Assert.Single(copy.ToRestore()));
        Assert.Empty(copy.Load().Outside);
        Assert.Equal(gone, Assert.Single(copy.ToRestore()));
        Assert.Equal(gone, Assert.Single(OpenAt().ToRestore()));
    }

    [Fact]
    public void ToRestore_ADamagedDocument_OneEditedInPlace_AndOneOutOfOrder_AreListedWithTheirLastGoodVersions()
    {
        Create(Record, "damaged");
        Create(OtherRecord, "edited");
        Create(ThirdRecord, "first");
        Replace(ThirdRecord, 1, "second");
        var copy = OpenAt();
        copy.Load();
        File.WriteAllText(_share.DocumentPath(Record), "{", Encoding.UTF8);
        TestFolder.EditInPlace(_share.DocumentPath(OtherRecord), DocumentFormat.New(OtherRecord, Content("edited by hand")));
        TestFolder.WriteAsKiler(_share.DocumentPath(ThirdRecord), File.ReadAllBytes(_share.HistoryPath(ThirdRecord, 1)));
        copy.Load();
        _clock.Advance(InPlaceEdits.SettleTime);
        copy.Load();

        Assert.Equal(
            [
                new OutsideChange(Record, InTrash: false, OutsideReason.Damaged, LastGood: 1),
                new OutsideChange(OtherRecord, InTrash: false, OutsideReason.EditedInPlace, LastGood: 1),
                new OutsideChange(ThirdRecord, InTrash: false, OutsideReason.OutOfOrder, LastGood: 2),
            ],
            copy.ToRestore());
    }

    [Fact]
    public void ToRestore_ADocumentMovedToTheTrash_OneThatComesBackAsItWas_AndADamagedOneWithoutLastGoodBytes_AreNotListed()
    {
        Create(Record, "deleted");
        Create(OtherRecord, "comes back");
        var copy = OpenAt();
        copy.Load();
        Assert.Equal(WriteOutcome.Deleted, _share.DeleteDocument(Record, 1).Outcome);
        var good = File.ReadAllBytes(_share.DocumentPath(OtherRecord));
        File.Delete(_share.DocumentPath(OtherRecord));
        File.WriteAllText(_share.DocumentPath(ThirdRecord), "{", Encoding.UTF8);
        copy.Load();
        _clock.Advance(InPlaceEdits.SettleTime);
        copy.Load();
        Assert.Equal(new OutsideChange(OtherRecord, InTrash: false, OutsideReason.Gone, LastGood: 1), Assert.Single(copy.ToRestore()));

        TestFolder.WriteAsKiler(_share.DocumentPath(OtherRecord), good);
        copy.Load();

        Assert.Empty(copy.ToRestore());
        Assert.Equal(Record, Assert.Single(copy.Trash()).Key);
    }

    [Fact]
    public void ToRestore_ADocumentGoneFromTheTrashOutsideKiler_IsListedInTheTrash()
    {
        Create(Record, "record");
        Assert.Equal(WriteOutcome.Deleted, _share.DeleteDocument(Record, 1).Outcome);
        var copy = OpenAt();
        copy.Load();
        File.Delete(_share.TrashPath(Record));
        copy.Load();
        _clock.Advance(InPlaceEdits.SettleTime);
        copy.Load();

        Assert.Equal(new OutsideChange(Record, InTrash: true, OutsideReason.Gone, LastGood: 1), Assert.Single(copy.ToRestore()));
    }

    /// <summary>
    /// The private folders of other users are in an admin's copy only for a restore (M2.7c), so their documents are listed,
    /// also once the copy is no longer given the account, as it keeps what it has of them (decided in M2.7c, by the user).
    /// </summary>
    [Fact]
    public void ToRestore_ADocumentOfAnotherPrivateFolder_IsListedInTheCopyOfAnAdmin_AlsoOnceTheFolderIsNoLongerGiven()
    {
        var other = _share.CreatePrivate(Other);
        Create(other, Record, "theirs");
        var copy = OpenAt(Me);
        copy.CopyOthers([Other]);
        copy.Load();
        File.Delete(other.DocumentPath(Record));
        LoadAMinuteLater(copy);
        _clock.Advance(InPlaceEdits.SettleTime);
        copy.Load();

        var gone = new OutsideChange(TheirRecord, InTrash: false, OutsideReason.Gone, LastGood: 1);
        Assert.Equal(gone, Assert.Single(copy.ToRestore()));
        copy.CopyOthers([]);
        Assert.Equal(gone, Assert.Single(copy.ToRestore()));
        Assert.Equal(gone, Assert.Single(OpenAt(Me).ToRestore()));
    }

    /// <summary>The order is not that of the index: the copy takes the trash after the folders, and the private folder of the user before those of others.</summary>
    [Fact]
    public void ToRestore_GivesTheChangesInTheOrderOfTheAccountsTheFoldersAndTheIds_EachInItsFolderBeforeTheTrash()
    {
        var settings = DocumentKey.AtRoot(DataFolder.Settings);
        var template = DocumentKey.InFolder(DataFolder.Templates, "c3d4e5");
        Create(settings, "settings");
        Create(template, "template");
        Create(OtherRecord, "record");
        Create(Record, "deleted");
        Assert.Equal(WriteOutcome.Deleted, _share.DeleteDocument(Record, 1).Outcome);
        var mine = _share.CreatePrivate(Me);
        Create(mine, Record, "mine");
        var other = _share.CreatePrivate(Other);
        Create(other, Record, "theirs");
        var copy = OpenAt(Me);
        copy.CopyOthers([Other]);
        copy.Load();
        foreach (var path in (string[])[_share.DocumentPath(settings), _share.DocumentPath(template), _share.DocumentPath(OtherRecord), _share.TrashPath(Record), mine.DocumentPath(Record), other.DocumentPath(Record)])
        {
            File.Delete(path);
        }

        LoadAMinuteLater(copy);
        _clock.Advance(InPlaceEdits.SettleTime);
        copy.Load();

        Assert.Equal(
            [
                new OutsideChange(settings, InTrash: false, OutsideReason.Gone, LastGood: 1),
                new OutsideChange(Record, InTrash: true, OutsideReason.Gone, LastGood: 1),
                new OutsideChange(OtherRecord, InTrash: false, OutsideReason.Gone, LastGood: 1),
                new OutsideChange(template, InTrash: false, OutsideReason.Gone, LastGood: 1),
                new OutsideChange(TheirRecord, InTrash: false, OutsideReason.Gone, LastGood: 1),
                new OutsideChange(Record.InPrivate(Me), InTrash: false, OutsideReason.Gone, LastGood: 1),
            ],
            copy.ToRestore());
    }

    /// <summary>The wizard writes a new Kiler only into a copy that has had no document (M4.3).</summary>
    [Fact]
    public void IsEmpty_UntilTheCopyHasHadADocument_AlsoOnceTheShareHasLostIt_UntilItIsForgotten()
    {
        var copy = OpenAt();
        Assert.True(copy.IsEmpty, "A new copy is not empty.");
        copy.Load();
        Assert.True(copy.IsEmpty, "The copy of a data folder without documents is not empty.");

        Create(Record, "first");
        copy.Load();
        Assert.False(copy.IsEmpty, "A copy with a document is empty.");

        // Gone outside Kiler: before a load has reported it, after, and in the copy opened again.
        File.Delete(_share.DocumentPath(Record));
        copy.Load();
        Assert.Empty(copy.Documents());
        Assert.Empty(copy.ToRestore());
        Assert.False(copy.IsEmpty, "A copy that keeps the last good bytes of a document gone a moment ago is empty.");
        _clock.Advance(InPlaceEdits.SettleTime);
        copy.Load();
        Assert.False(OpenAt().IsEmpty, "A copy that keeps the last good bytes of a gone document is empty once opened again.");

        Assert.Equal(1, copy.Forget(copy.ToRestore()));
        Assert.True(copy.IsEmpty, "A copy whose last document was forgotten is not empty.");
    }

    [Fact]
    public void Forget_AGoneDocument_TakesItOutOfTheList_WithItsKeptBytesAndItsSlots_ForGood()
    {
        Create(Record, "first");
        Create(OtherRecord, "stays");
        var copy = OpenAt();
        copy.Load();
        Replace(Record, 1, "second");
        copy.Load();
        Assert.Equal(["v1.json"], SlotCopies(Record));
        File.Delete(_share.DocumentPath(Record));
        copy.Load();
        _clock.Advance(InPlaceEdits.SettleTime);
        copy.Load();
        var gone = Assert.Single(copy.ToRestore());

        Assert.Equal(1, copy.Forget([gone]));

        Assert.Empty(copy.ToRestore());
        Assert.False(File.Exists(KeptPath(Record)), "The kept bytes of a forgotten document stay.");
        Assert.Empty(SlotCopies(Record));
        Assert.Empty(OpenAt().ToRestore());
        Assert.Empty(copy.Load().Outside);
        Assert.Equal(OtherRecord, Assert.Single(copy.Documents()).Key);
    }

    /// <summary>
    /// The copy keeps the slots of a document that this Kiler replaced and deleted in its folder (M2.7a), so forgetting it once
    /// it went from the trash takes those too (reviews of M2.9).
    /// </summary>
    [Fact]
    public void Forget_ADocumentGoneFromTheTrashAfterThisKilerDeletedIt_TakesTheSlotsOfBothPlaces()
    {
        Create(Record, "first");
        var copy = OpenAt();
        copy.Load();
        Assert.Equal(WriteOutcome.Written, copy.Replace(Record, 1, Content("second")).Outcome);
        Assert.Equal(WriteOutcome.Deleted, copy.Delete(Record, 2).Outcome);
        Assert.Equal(["v1.json", "v2.json"], SlotCopies(Record));
        File.Delete(_share.TrashPath(Record));
        copy.Load();
        _clock.Advance(InPlaceEdits.SettleTime);
        copy.Load();
        var gone = Assert.Single(copy.ToRestore());
        Assert.True(gone.InTrash, "The document gone from the trash is listed in its folder.");

        Assert.Equal(1, copy.Forget([gone]));

        Assert.Empty(SlotCopies(Record));
        Assert.Empty(FilesUnder(Path.Combine(_copy, "data", DataFolder.Trash, DataFolder.History)));
    }

    /// <summary>A document of the same id in the other place keeps the slots of that place.</summary>
    [Fact]
    public void Forget_ADocumentGoneFromTheTrash_KeepsTheSlotsOfADocumentOfItsIdInItsFolder()
    {
        Create(Record, "first");
        var copy = OpenAt();
        copy.Load();
        Assert.Equal(WriteOutcome.Deleted, copy.Delete(Record, 1).Outcome);
        TestFolder.WriteAsKiler(_share.DocumentPath(Record), DocumentFormat.New(Record, Content("put by hand")));
        copy.Load();
        _clock.Advance(InPlaceEdits.SettleTime);
        copy.Load();
        File.Delete(_share.TrashPath(Record));
        copy.Load();
        _clock.Advance(InPlaceEdits.SettleTime);
        copy.Load();
        var gone = Assert.Single(copy.ToRestore());
        Assert.True(gone.InTrash, "The document gone from the trash is listed in its folder.");

        Assert.Equal(1, copy.Forget([gone]));

        Assert.Equal(["v1.json"], SlotCopies(Record));
        Assert.Equal("put by hand", Name(Assert.Single(copy.Documents())));
    }

    /// <summary>
    /// Junk that someone kept on the share for a day and took away keeps its last good bytes, as a real document would, and
    /// blocks the new documents until the admin forgets it, in bulk (decided after the third security review of M2.7).
    /// </summary>
    [Fact]
    public void Forget_InBulk_TheLeftoversOfJunkKeptOnTheShareForADay_MakesRoomForTheNewDocuments()
    {
        Create(Record, "junk");
        Create(OtherRecord, "junk");
        var copy = OpenAt(limits: CopyLimits.Default with { Documents = 2 });
        copy.Load();
        _clock.Advance(DataCopy.BriefStay);
        File.Delete(_share.DocumentPath(Record));
        File.Delete(_share.DocumentPath(OtherRecord));
        copy.Load();
        _clock.Advance(InPlaceEdits.SettleTime);
        copy.Load();
        Create(ThirdRecord, "new");
        var full = copy.Load();
        Assert.Equal(SkipReason.OverBudget, Assert.Single(full.Skipped).Reason);
        Assert.Empty(full.Changed);

        Assert.Equal(2, copy.Forget(copy.ToRestore()));

        Assert.Equal(ThirdRecord, Assert.Single(copy.Load().Changed).Key);
        Assert.Equal(ThirdRecord, Assert.Single(copy.Documents()).Key);
    }

    [Fact]
    public void Forget_ADamagedDocument_DropsItsLastGoodBytes_AndItStaysDamaged_WithoutAnotherReport()
    {
        Create(Record, "good");
        var copy = OpenAt();
        copy.Load();
        File.WriteAllText(_share.DocumentPath(Record), "{", Encoding.UTF8);
        copy.Load();

        Assert.Equal(1, copy.Forget(copy.ToRestore()));

        Assert.Empty(copy.ToRestore());
        Assert.False(File.Exists(KeptPath(Record)), "The last good bytes of a forgotten damaged document stay.");
        var load = copy.Load();
        Assert.Empty(load.Outside);
        Assert.Empty(load.Changed);
        Assert.Equal(DocumentDamage.NotJson, Assert.Single(OpenAt().Documents()).Read.Damage);

        // Put right at any version, it is good: the copy has no last good bytes for it to come after.
        TestFolder.WriteAsKiler(_share.DocumentPath(Record), DocumentFormat.New(Record, Content("put right")));
        load = copy.Load();
        Assert.Empty(load.Outside);
        Assert.Equal("put right", Name(Assert.Single(load.Changed)));
    }

    /// <summary>Forgetting a document that the share still has takes its file as it is (decided in M2.9, by the user).</summary>
    [Theory]
    [InlineData(OutsideReason.EditedInPlace)]
    [InlineData(OutsideReason.OutOfOrder)]
    public void Forget_AChangedDocument_TakesItsFileOnTheShareAsTheLastGood(OutsideReason reason)
    {
        Create(Record, "first");
        Replace(Record, 1, "second");
        var copy = OpenAt();
        copy.Load();
        var path = _share.DocumentPath(Record);
        byte[] changed;
        if (reason == OutsideReason.EditedInPlace)
        {
            changed = DocumentFormat.Next(Record, 2, Content("edited by hand"));
            TestFolder.EditInPlace(path, changed);
        }
        else
        {
            changed = File.ReadAllBytes(_share.HistoryPath(Record, 1));
            TestFolder.WriteAsKiler(path, changed);
        }

        copy.Load();
        _clock.Advance(InPlaceEdits.SettleTime);
        copy.Load();
        var listed = Assert.Single(copy.ToRestore());
        Assert.Equal(reason, listed.Reason);

        Assert.Equal(1, copy.Forget([listed]));

        Assert.Empty(copy.ToRestore());
        Assert.False(File.Exists(KeptPath(Record)), "The last good bytes of a forgotten change stay.");
        Assert.Equal(changed, File.ReadAllBytes(CopyPath(Record)));
        var reopened = OpenAt();
        Assert.Empty(reopened.ToRestore());
        Assert.Equal(reason == OutsideReason.EditedInPlace ? 3 : 1, Assert.Single(reopened.Documents()).Read.Document?.Version);
        _clock.Advance(InPlaceEdits.SettleTime);
        Assert.Empty(copy.Load().Outside);

        // What a restore would bring back now is the file as the share had it.
        File.Delete(path);
        copy.Load();
        _clock.Advance(InPlaceEdits.SettleTime);
        copy.Load();
        Assert.Equal(new OutsideChange(Record, InTrash: false, OutsideReason.Gone, reason == OutsideReason.EditedInPlace ? 3 : 1), Assert.Single(copy.ToRestore()));
        Assert.Equal(changed, File.ReadAllBytes(KeptPath(Record)));
    }

    /// <summary>
    /// An entry whose bytes in data\ went from the copy, as the open forgets them (M2.7a), leaves the copy when it is forgotten,
    /// and the next load reads the file anew; the share still has the document, so the slots of its history stay.
    /// </summary>
    [Fact]
    public void Forget_AChangedDocumentWhoseBytesWentFromTheCopy_LeavesIt_AndKeepsItsSlots()
    {
        Create(Record, "first");
        var copy = OpenAt();
        copy.Load();
        Replace(Record, 1, "second");
        copy.Load();
        var older = File.ReadAllBytes(_share.HistoryPath(Record, 1));
        TestFolder.WriteAsKiler(_share.DocumentPath(Record), older);
        copy.Load();
        File.Delete(CopyPath(Record));
        copy = OpenAt();
        var listed = Assert.Single(copy.ToRestore());
        Assert.Equal(OutsideReason.OutOfOrder, listed.Reason);

        Assert.Equal(1, copy.Forget([listed]));

        Assert.Empty(copy.ToRestore());
        Assert.Equal(["v1.json"], SlotCopies(Record));
        Assert.False(File.Exists(KeptPath(Record)), "The last good bytes of a forgotten change stay.");
        var load = copy.Load();
        Assert.Equal(1, Assert.Single(load.Changed).Read.Document?.Version);
        Assert.Empty(load.Outside);
        Assert.Equal(older, File.ReadAllBytes(CopyPath(Record)));
    }

    /// <summary>A change is forgotten only as the list gave it, so the screen never forgets a change that it did not show.</summary>
    [Fact]
    public void Forget_AChangeThatIsNoLongerAsTheListGaveIt_IsLeftAsItIs()
    {
        Create(Record, "good");
        Create(OtherRecord, "good");
        var copy = OpenAt();
        copy.Load();
        TestFolder.EditInPlace(_share.DocumentPath(Record), DocumentFormat.Next(Record, 1, Content("edited by hand")));
        var good = File.ReadAllBytes(_share.DocumentPath(OtherRecord));
        File.Delete(_share.DocumentPath(OtherRecord));
        copy.Load();
        _clock.Advance(InPlaceEdits.SettleTime);
        copy.Load();
        var listed = copy.ToRestore();
        Assert.Equal(2, listed.Count);
        File.WriteAllText(_share.DocumentPath(Record), "{", Encoding.UTF8);
        TestFolder.WriteAsKiler(_share.DocumentPath(OtherRecord), good);
        copy.Load();

        Assert.Equal(0, copy.Forget(listed));
        Assert.Equal(0, copy.Forget([new OutsideChange(Record, InTrash: false, OutsideReason.Damaged, LastGood: 2)]));
        Assert.Equal(0, copy.Forget([new OutsideChange(Record, InTrash: true, OutsideReason.Damaged, LastGood: 1)]));

        Assert.Equal(new OutsideChange(Record, InTrash: false, OutsideReason.Damaged, LastGood: 1), Assert.Single(copy.ToRestore()));
        Assert.True(File.Exists(KeptPath(Record)), "The last good bytes of a change that was not forgotten went.");
    }

    /// <summary>A document gone again after it came back is not in the list until a load reports it, as it can be a late listing of a move (M2.7a).</summary>
    [Fact]
    public void Forget_AChangeThatNoLoadHasReportedAgain_IsLeftAsItIs()
    {
        Create(Record, "record");
        var copy = OpenAt();
        copy.Load();
        var good = File.ReadAllBytes(_share.DocumentPath(Record));
        File.Delete(_share.DocumentPath(Record));
        copy.Load();
        _clock.Advance(InPlaceEdits.SettleTime);
        copy.Load();
        var listed = copy.ToRestore();
        TestFolder.WriteAsKiler(_share.DocumentPath(Record), good);
        copy.Load();
        File.Delete(_share.DocumentPath(Record));
        copy.Load();

        Assert.Equal(0, copy.Forget(listed));

        Assert.Empty(copy.ToRestore());
        _clock.Advance(InPlaceEdits.SettleTime);
        copy.Load();
        Assert.Equal(listed, copy.ToRestore());
    }

    [Fact]
    public void ForgetPrivate_OfAUserWhoLeft_ForgetsAllThatTheCopyHasOfTheirFolder_AndStopsCopyingIt_UntilItIsGivenAgain()
    {
        var other = _share.CreatePrivate(Other);
        Create(other, Record, "theirs");
        Create(other, OtherRecord, "deleted");
        File.WriteAllText(Path.Combine(other.Root, DataFolder.Records, "Skipped.json"), "{}");
        Create(_share.CreatePrivate(Me), Record, "mine");
        var copy = OpenAt(Me);
        copy.CopyOthers([Other]);
        copy.Load();
        File.Delete(other.DocumentPath(OtherRecord));
        LoadAMinuteLater(copy);
        _clock.Advance(InPlaceEdits.SettleTime);
        copy.Load();
        Assert.Equal(OtherRecord.InPrivate(Other), Assert.Single(copy.ToRestore()).Key);
        Assert.Equal(1, copy.OthersSkipsKept);

        copy.ForgetPrivate(Other);

        Assert.Empty(copy.ToRestore());
        Assert.Equal(0, copy.OthersSkipsKept);
        Assert.Empty(FilesUnder(Path.Combine(_copy, "data", DataFolder.Private, Other)));
        Assert.Empty(FilesUnder(Path.Combine(_copy, "kept", DataFolder.Private, Other)));
        Assert.Equal(Record.InPrivate(Me), Assert.Single(copy.Documents()).Key);
        var listed = ListedUnder(other.Root);
        LoadAMinuteLater(copy);
        Assert.Empty(listed);
        Assert.Empty(FilesUnder(Path.Combine(_copy, "data", DataFolder.Private, Other)));

        // Given again, as by a sign-in that lists the account, a folder at the name is taken as first seen (M2.7d).
        Directory.Delete(other.Root, recursive: true);
        Create(_share.CreatePrivate(Other), Record, "again");
        copy.CopyOthers([Other]);
        var load = copy.Load();

        Assert.Empty(load.Skipped);
        Assert.True(File.Exists(Path.Combine(_copy, "data", DataFolder.Private, Other, DataFolder.Records, Record.Id + ".json")), "The folder given again was not copied.");
    }

    /// <summary>A copy opened with another account keeps the private folder of the last one as another user's, with its history (M2.7c).</summary>
    [Fact]
    public void ForgetPrivate_ForgetsTheSlotsLeftFromWhenTheFolderWasTheUsersOwn()
    {
        var other = _share.CreatePrivate(Other);
        Create(other, Record, "first");
        var theirs = OpenAt(Other);
        theirs.Load();
        Assert.Equal(WriteOutcome.Written, other.ReplaceDocument(Record, 1, Content("second")).Outcome);
        theirs.Load();
        var slots = Path.Combine(_copy, "data", DataFolder.Private, Other, DataFolder.History, DataFolder.Records, Record.Id);
        Assert.Single(FilesUnder(slots));
        var copy = OpenAt(Me);

        copy.ForgetPrivate(Other);

        Assert.Empty(FilesUnder(slots));
        Assert.Empty(FilesUnder(Path.Combine(_copy, "data", DataFolder.Private, Other)));
    }

    [Theory]
    [InlineData(Me)]
    [InlineData("Demo.Other")]
    [InlineData("..")]
    public void ForgetPrivate_OfTheCurrentUser_OrOfANameThatFailsTheCheck_IsRefused_AndForgetsNothing(string account)
    {
        var mine = _share.CreatePrivate(Me);
        Create(mine, Record, "mine");
        var copy = OpenAt(Me);
        copy.Load();
        File.Delete(mine.DocumentPath(Record));
        copy.Load();
        _clock.Advance(InPlaceEdits.SettleTime);
        copy.Load();

        Assert.Throws<ArgumentException>(() => copy.ForgetPrivate(account));

        Assert.Equal(new OutsideChange(Record.InPrivate(Me), InTrash: false, OutsideReason.Gone, LastGood: 1), Assert.Single(copy.ToRestore()));
    }

    /// <summary>
    /// The last good bytes go back as they are, at their version (decided in M2.9a, by the user), and the copy follows the
    /// write: the next load reads the file back, finds it good, and reports nothing.
    /// </summary>
    [Fact]
    public void WriteBack_ADocumentDeletedOutsideKiler_ComesBackWithItsBytesAtItsVersion_AndTheNextLoadTakesItAsGood()
    {
        Create(Record, "first");
        Replace(Record, 1, "second");
        var copy = OpenAt();
        copy.Load();
        var path = _share.DocumentPath(Record);
        var good = File.ReadAllBytes(path);
        File.Delete(path);
        var gone = ListGone(copy);
        Assert.Equal(new OutsideChange(Record, InTrash: false, OutsideReason.Gone, LastGood: 2), gone);

        var result = copy.WriteBack(gone);

        Assert.Equal(WriteOutcome.Written, result.Outcome);
        Assert.Equal(2, result.Version);
        Assert.Equal(good, File.ReadAllBytes(path));
        Assert.Equal(["v1.json"], SlotsOnShare(Record));
        Assert.Empty(copy.ToRestore());
        Assert.Equal("second", Name(Assert.Single(copy.Documents())));
        Assert.False(File.Exists(KeptPath(Record)), "The kept bytes of a document written back stay.");
        Assert.Equal(good, File.ReadAllBytes(CopyPath(Record)));
        Assert.Empty(copy.Load().Outside);
        _clock.Advance(InPlaceEdits.SettleTime);
        Assert.Empty(copy.Load().Outside);
        Assert.Empty(copy.ToRestore());
        Assert.Equal(2, Assert.Single(OpenAt().Documents()).Read.Document?.Version);
    }

    /// <summary>
    /// One gone from the trash goes back there, the folder of the trash made again when it went too, as after a delete of
    /// Kiler: the slot of its delete has its bytes, and a restore from the trash deletes it (M2.5).
    /// </summary>
    [Fact]
    public void WriteBack_ADocumentGoneFromTheTrash_GoesBackIntoTheTrash_AndIsRestoredFromThere()
    {
        Create(Record, "record");
        Assert.Equal(WriteOutcome.Deleted, _share.DeleteDocument(Record, 1).Outcome);
        var copy = OpenAt();
        copy.Load();
        var good = File.ReadAllBytes(_share.TrashPath(Record));
        Directory.Delete(Path.GetDirectoryName(_share.TrashPath(Record))!, recursive: true);
        var gone = ListGone(copy);
        Assert.True(gone.InTrash, "The document gone from the trash is listed in its folder.");

        Assert.Equal(WriteOutcome.Written, copy.WriteBack(gone).Outcome);

        Assert.Equal(good, File.ReadAllBytes(_share.TrashPath(Record)));
        Assert.False(File.Exists(_share.DocumentPath(Record)), "The document went back into its folder.");
        Assert.Equal(Record, Assert.Single(copy.Trash()).Key);
        Assert.Empty(copy.Documents());
        Assert.Empty(copy.Load().Outside);
        Assert.Equal(WriteOutcome.Written, copy.RestoreFromTrash(Record).Outcome);
        Assert.Empty(SlotsOnShare(Record));
        Assert.Equal("record", Name(Assert.Single(copy.Documents())));
    }

    [Fact]
    public void WriteBack_ADocumentOfThePrivateFolderOfTheUser_ComesBack()
    {
        var mine = _share.CreatePrivate(Me);
        Create(mine, Record, "mine");
        var copy = OpenAt(Me);
        copy.Load();
        var path = mine.DocumentPath(Record);
        var good = File.ReadAllBytes(path);
        File.Delete(path);
        var gone = ListGone(copy);
        Assert.Equal(Record.InPrivate(Me), gone.Key);

        Assert.Equal(WriteOutcome.Written, copy.WriteBack(gone).Outcome);

        Assert.Equal(good, File.ReadAllBytes(path));
        Assert.Empty(copy.Load().Outside);
        Assert.Equal(Record.InPrivate(Me), Assert.Single(copy.Documents()).Key);
    }

    /// <summary>A document folder that went with its documents is made again by the first of them written back, as a folder of the trash is.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData(Me)]
    public void WriteBack_ADocumentWhoseFolderWentWithIt_MakesTheFolderAgain(string? account)
    {
        var root = account is null ? _share : _share.CreatePrivate(account);
        Create(root, Record, "record");
        var copy = OpenAt(account);
        copy.Load();
        var path = root.DocumentPath(Record);
        var good = File.ReadAllBytes(path);
        Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        var gone = ListGone(copy);

        Assert.Equal(WriteOutcome.Written, copy.WriteBack(gone).Outcome);

        Assert.Equal(good, File.ReadAllBytes(path));
        var load = copy.Load();
        Assert.Empty(load.Outside);
        Assert.Empty(load.Skipped);
    }

    /// <summary>
    /// The bytes written back keep whether this Kiler wrote them (M2.7a), and the stay of the document counts only the time
    /// that the share listed it (fourth security review of M2.7): the bytes of another Kiler that go again keep their place
    /// once the copy has had them for a day, and before that make room for the documents of the share, however long they
    /// were gone before the write back (security review of M2.9a).
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void WriteBack_ADocumentOfAnotherKilerGoneAgain_KeepsItsLastGoodBytes_OnlyAfterADayListedByTheShare(bool aDayListed)
    {
        Create(Record, "record");
        var copy = OpenAt(limits: CopyLimits.Default with { Documents = 2 });
        copy.Load();
        _clock.Advance(aDayListed ? DataCopy.BriefStay : DataCopy.BriefStay / 2);
        File.Delete(_share.DocumentPath(Record));
        var gone = ListGone(copy);
        _clock.Advance(aDayListed ? TimeSpan.Zero : DataCopy.BriefStay);
        Assert.Equal(WriteOutcome.Written, copy.WriteBack(gone).Outcome);
        copy.Load();
        File.Delete(_share.DocumentPath(Record));
        ListGone(copy);
        Create(OtherRecord, "new");
        Create(ThirdRecord, "new");

        var load = copy.Load();

        if (aDayListed)
        {
            Assert.Equal(SkipReason.OverBudget, Assert.Single(load.Skipped).Reason);
            Assert.Equal(Record, Assert.Single(copy.ToRestore()).Key);
        }
        else
        {
            Assert.Empty(load.Skipped);
            Assert.Equal(2, copy.Documents().Count);
            Assert.Empty(copy.ToRestore());
        }
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
        File.Delete(_share.DocumentPath(Record));
        var gone = ListGone(copy);
        var inTheWay = Directory.CreateDirectory(CopyPath(Record)).FullName;

        Assert.Equal(WriteOutcome.Written, copy.WriteBack(gone).Outcome);

        Assert.Equal(gone, Assert.Single(copy.ToRestore()));
        Assert.Empty(copy.Documents());
        Directory.Delete(inTheWay);
        var load = copy.Load();
        Assert.Empty(load.Outside);
        Assert.Equal("record", Name(Assert.Single(load.Changed)));
        Assert.Empty(copy.ToRestore());
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
        File.Delete(_share.DocumentPath(Record));
        LoadBoth(copy, other);
        var gone = Assert.Single(copy.ToRestore());
        Assert.Equal(gone, Assert.Single(other.ToRestore()));

        Assert.Equal(WriteOutcome.Written, copy.WriteBack(gone).Outcome);

        Assert.Empty(other.Load().Outside);
        Assert.Empty(other.ToRestore());
        Assert.Equal("record", Name(Assert.Single(other.Documents())));
    }

    /// <summary>
    /// The limit (decided in M2.9a, by the user): a copy that missed only the last version of the share writes back the one
    /// before it, as the slot of that version has its bytes; the copy that saw the last one reports the document out of
    /// order, with its last good bytes, so nothing is lost.
    /// </summary>
    [Fact]
    public void WriteBack_ByACopyThatMissedTheLastVersion_IsReportedOutOfOrderByTheCopyThatSawIt()
    {
        Create(Record, "first");
        var copy = OpenAt();
        copy.Load();
        Replace(Record, 1, "second");
        var other = OpenAnother();
        other.Load();
        File.Delete(_share.DocumentPath(Record));
        LoadBoth(copy, other);
        var gone = Assert.Single(copy.ToRestore());
        Assert.Equal(1, gone.LastGood);

        Assert.Equal(WriteOutcome.Written, copy.WriteBack(gone).Outcome);

        LoadBoth(copy, other);
        Assert.Empty(copy.ToRestore());
        Assert.Equal(new OutsideChange(Record, InTrash: false, OutsideReason.OutOfOrder, LastGood: 2), Assert.Single(other.ToRestore()));
    }

    /// <summary>
    /// A copy that did not see the newer versions of the share writes nothing back (decided in M2.9a, by the user): the slot
    /// of the version written back, or of one after it, would have other bytes than the document and block its writes for
    /// good (M2.4). The slots stay as they are. Anything at the name of such a slot counts, and a history that a listing does
    /// not take whole cannot be checked.
    /// </summary>
    [Theory]
    [InlineData(Ahead.SlotAfter)]
    [InlineData(Ahead.SlotWithOtherBytes)]
    [InlineData(Ahead.FolderAtItsSlot)]
    [InlineData(Ahead.TooManyEntries)]
    public void WriteBack_WhenTheHistoryOfTheShareHasAVersionThatTheCopyDidNotSee_IsRefusedAsBehind(Ahead ahead)
    {
        Create(Record, "first");
        var copy = OpenAt(limits: ahead == Ahead.TooManyEntries ? CopyLimits.Default with { Listed = 3 } : null);
        copy.Load();
        var history = HistoryOnShare(Record);
        if (ahead == Ahead.SlotAfter)
        {
            Replace(Record, 1, "second");
            Replace(Record, 2, "third");
        }
        else
        {
            Directory.CreateDirectory(history);
        }

        if (ahead == Ahead.SlotWithOtherBytes)
        {
            // Written after an edit in place that the copy did not see.
            TestFolder.WriteAsKiler(_share.HistoryPath(Record, 1), DocumentFormat.New(Record, Content("edited by hand")));
        }
        else if (ahead == Ahead.FolderAtItsSlot)
        {
            Directory.CreateDirectory(_share.HistoryPath(Record, 1));
        }
        else if (ahead == Ahead.TooManyEntries)
        {
            foreach (var name in (string[])["a.txt", "b.txt", "c.txt", "d.txt"])
            {
                File.WriteAllText(Path.Combine(history, name), "not a slot");
            }
        }

        File.Delete(_share.DocumentPath(Record));
        var gone = ListGone(copy);
        Assert.Equal(1, gone.LastGood);
        var slots = SlotsOnShare(Record).Select(name => File.ReadAllBytes(Path.Combine(history, name))).ToList();

        Assert.Equal(WriteOutcome.Behind, copy.WriteBack(gone).Outcome);

        Assert.False(File.Exists(_share.DocumentPath(Record)), "A copy that is behind wrote the document back.");
        Assert.Equal(slots, SlotsOnShare(Record).Select(name => File.ReadAllBytes(Path.Combine(history, name))));
        Assert.Equal(gone, Assert.Single(copy.ToRestore()));
    }

    /// <summary>The rename that does not replace decides, also when the name is taken while the write back runs, and its temporary file goes.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WriteBack_ANameTakenSinceTheList_IsNotWrittenOver(bool whileItWrites)
    {
        Create(Record, "record");
        var copy = OpenAt();
        copy.Load();
        var path = _share.DocumentPath(Record);
        File.Delete(path);
        var gone = ListGone(copy);
        var put = DocumentFormat.New(Record, Content("put by hand"));
        if (whileItWrites)
        {
            _files.Before = (step, _) =>
            {
                if (step == nameof(IFileSystem.CreateFile) && !File.Exists(path))
                {
                    TestFolder.WriteAsKiler(path, put);
                }
            };
        }
        else
        {
            TestFolder.WriteAsKiler(path, put);
        }

        Assert.Equal(WriteOutcome.Exists, copy.WriteBack(gone).Outcome);

        Assert.Equal(put, File.ReadAllBytes(path));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp"));
        Assert.True(File.Exists(KeptPath(Record)), "The kept bytes of a document that was not written back went.");
    }

    /// <summary>The folders are checked before the rename, as for a create (M2.3): an empty one made a link in place is refused.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WriteBack_WhenTheEmptyDocumentFolderIsMadeALinkInPlace_IsRefused(bool createThroughLinks)
    {
        Create(Record, "record");
        var copy = OpenAt();
        copy.Load();
        var records = Path.GetDirectoryName(_share.DocumentPath(Record))!;
        File.Delete(_share.DocumentPath(Record));
        var gone = ListGone(copy);
        var elsewhere = Directory.CreateDirectory(Path.Combine(_test.Root, "elsewhere")).FullName;
        TestFolder.SkipWithoutJunctions(records);
        _files.CreateThroughLinks = createThroughLinks;
        var made = false;
        _files.Before = (step, at) =>
        {
            if (step == nameof(IFileSystem.CreateFile) && !made && at.StartsWith(records + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                made = true;
                TestFolder.MakeMountPoint(records, elsewhere);
            }
        };

        Assert.Throws<IOException>(() => copy.WriteBack(gone));

        Assert.True(made, "The folder was not made a link.");
        Assert.Empty(Directory.GetFiles(elsewhere));
        Assert.Equal(gone, Assert.Single(copy.ToRestore()));
    }

    /// <summary>The history folder is checked after its listing: an empty one made a link in place would give another folder's slots.</summary>
    [Fact]
    public void WriteBack_WhenTheEmptyHistoryFolderIsMadeALinkInPlace_IsRefused()
    {
        Create(Record, "record");
        var copy = OpenAt();
        copy.Load();
        File.Delete(_share.DocumentPath(Record));
        var gone = ListGone(copy);
        var history = Directory.CreateDirectory(HistoryOnShare(Record)).FullName;
        var elsewhere = Directory.CreateDirectory(Path.Combine(_test.Root, "elsewhere")).FullName;
        TestFolder.SkipWithoutJunctions(history);
        var made = false;
        _files.Before = (step, at) =>
        {
            if (step == nameof(IFileSystem.List) && !made && string.Equals(at, history, StringComparison.OrdinalIgnoreCase))
            {
                made = true;
                TestFolder.MakeMountPoint(history, elsewhere);
            }
        };

        Assert.Throws<IOException>(() => copy.WriteBack(gone));

        Assert.True(made, "The history folder was not made a link.");
        Assert.False(File.Exists(_share.DocumentPath(Record)), "The document was written back after a listing through a link.");
    }

    /// <summary>Kiler never makes two documents of one id, one in its folder and one in the trash (M2.7a).</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WriteBack_ADocumentOfItsIdInTheOtherPlace_IsNotWrittenBack(bool fromTheTrash)
    {
        Create(Record, "record");
        if (fromTheTrash)
        {
            Assert.Equal(WriteOutcome.Deleted, _share.DeleteDocument(Record, 1).Outcome);
        }

        var copy = OpenAt();
        copy.Load();
        var (place, other) = fromTheTrash ? (_share.TrashPath(Record), _share.DocumentPath(Record)) : (_share.DocumentPath(Record), _share.TrashPath(Record));
        var bytes = File.ReadAllBytes(place);
        File.Delete(place);
        var gone = ListGone(copy);
        Directory.CreateDirectory(Path.GetDirectoryName(other)!);
        TestFolder.WriteAsKiler(other, bytes);

        Assert.Equal(WriteOutcome.Exists, copy.WriteBack(gone).Outcome);

        Assert.False(File.Exists(place), "The document was written back beside one of its id in the other place.");
    }

    /// <summary>A change is written back only as the list gave it (M2.9), so a screen never writes back one that it did not show.</summary>
    [Fact]
    public void WriteBack_AChangeThatIsNoLongerAsTheListGaveIt_WritesNothing()
    {
        Create(Record, "record");
        var copy = OpenAt();
        copy.Load();
        var path = _share.DocumentPath(Record);
        var good = File.ReadAllBytes(path);
        File.Delete(path);
        var gone = ListGone(copy);

        Assert.Equal(WriteOutcome.NotListed, copy.WriteBack(gone with { LastGood = 2 }).Outcome);
        Assert.Equal(WriteOutcome.NotListed, copy.WriteBack(gone with { InTrash = true }).Outcome);
        Assert.Equal(WriteOutcome.NotListed, copy.WriteBack(gone with { Key = OtherRecord }).Outcome);
        Assert.Equal(WriteOutcome.NotListed, copy.WriteBack(gone with { Key = TheirRecord }).Outcome);
        Assert.Throws<ArgumentNullException>(() => copy.WriteBack(default));

        // Back, and gone again for less than the settle time: it can be a move that the SMB cache shows late (M2.7a).
        TestFolder.WriteAsKiler(path, good);
        copy.Load();
        File.Delete(path);
        copy.Load();
        Assert.Equal(WriteOutcome.NotListed, copy.WriteBack(gone).Outcome);
        Assert.False(File.Exists(path), "A change that is not listed was written back.");

        _clock.Advance(InPlaceEdits.SettleTime);
        copy.Load();
        Assert.Equal(WriteOutcome.Written, copy.WriteBack(gone).Outcome);
    }

    /// <summary>The bytes in kept\ are checked against their hash before the write (M2.7a): other bytes are not the last good ones.</summary>
    [Fact]
    public void WriteBack_WhenTheCopyHasLostTheLastGoodBytes_WritesNothing()
    {
        Create(Record, "record");
        var copy = OpenAt();
        copy.Load();
        File.Delete(_share.DocumentPath(Record));
        var gone = ListGone(copy);
        File.WriteAllBytes(KeptPath(Record), DocumentFormat.New(Record, Content("other bytes")));

        Assert.Equal(WriteOutcome.Missing, copy.WriteBack(gone).Outcome);
        File.Delete(KeptPath(Record));
        Assert.Equal(WriteOutcome.Missing, copy.WriteBack(gone).Outcome);

        Assert.False(File.Exists(_share.DocumentPath(Record)), "Bytes that are not the last good ones were written back.");
    }

    [Fact]
    public void WriteBack_WhenTheLastGoodBytesCannotBeReadNow_IsBusy_AndCanBeTriedAgain()
    {
        Create(Record, "record");
        var copy = OpenAt();
        copy.Load();
        File.Delete(_share.DocumentPath(Record));
        var gone = ListGone(copy);

        using (new FileStream(KeptPath(Record), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Equal(WriteOutcome.Busy, copy.WriteBack(gone).Outcome);
        }

        Assert.False(File.Exists(_share.DocumentPath(Record)), "The document was written back while its bytes could not be read.");
        Assert.Equal(WriteOutcome.Written, copy.WriteBack(gone).Outcome);
    }

    private static JsonObject Content(string name) => new() { ["name"] = name };

    private static string? Name(LoadedDocument document) => (string?)document.Read.Document?.Content["name"];

    private static void Create(DataFolder root, DocumentKey key, string name) => Assert.Equal(WriteOutcome.Written, root.CreateDocument(key, Content(name)).Outcome);

    /// <summary>The files in a folder of the copy and in its folders, none when it is not there.</summary>
    private static string[] FilesUnder(string folder) => Directory.Exists(folder) ? Directory.GetFiles(folder, "*", SearchOption.AllDirectories) : [];

    /// <summary>A document on the share, as a write of another Kiler makes it.</summary>
    private void Create(DocumentKey key, string name) => Create(_share, key, name);

    /// <summary>A new version of a document on the share, as a write of another Kiler makes it, with the old one in its history slot.</summary>
    private void Replace(DocumentKey key, int version, string name) => Assert.Equal(WriteOutcome.Written, _share.ReplaceDocument(key, version, Content(name)).Outcome);

    /// <summary>Opens the copy on <see cref="_clock"/>, with an account and a budget or without.</summary>
    private DataCopy OpenAt(string? account = null, CopyLimits? limits = null) => DataCopy.Open(_share, _copy, _clock, limits ?? CopyLimits.Default, account);

    /// <summary>Opens a second copy of the share in a folder of its own, as on the machine of another user.</summary>
    private DataCopy OpenAnother() => DataCopy.Open(_share, Directory.CreateDirectory(Path.Combine(_test.Root, "another copy")).FullName, _clock, CopyLimits.Default);

    /// <summary>
    /// Loads a copy twice, <see cref="InPlaceEdits.SettleTime"/> apart, so it reports a document that went (M2.7a), and gives
    /// the one change that it lists.
    /// </summary>
    private OutsideChange ListGone(DataCopy copy)
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

    /// <summary>The history folder of a document on the share.</summary>
    private string HistoryOnShare(DocumentKey key) => Path.GetDirectoryName(_share.HistoryPath(key, 1))!;

    /// <summary>The names of the slots of a document in the history of the share, in order.</summary>
    private string[] SlotsOnShare(DocumentKey key)
    {
        var folder = HistoryOnShare(key);
        return Directory.Exists(folder) ? [.. Directory.GetFiles(folder).Select(path => Path.GetFileName(path)).Order(StringComparer.Ordinal)] : [];
    }

    /// <summary>Loads a copy on <see cref="_clock"/> a minute later, when the load takes the private folders of other users again (<see cref="DataCopy.OthersInterval"/>, M2.8c).</summary>
    private LoadResult LoadAMinuteLater(DataCopy copy)
    {
        _clock.Advance(DataCopy.OthersInterval);
        return copy.Load();
    }

    /// <summary>The folders under <paramref name="root"/> that the share lists from now on, filled as the steps come.</summary>
    private List<string> ListedUnder(string root)
    {
        var listed = new List<string>();
        _files.Before = (step, path) =>
        {
            if (step == nameof(IFileSystem.List) && path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                lock (listed)
                {
                    listed.Add(path);
                }
            }
        };
        return listed;
    }

    /// <summary>The names of the slots of a document in the history of the copy, in order.</summary>
    private string[] SlotCopies(DocumentKey key)
    {
        var folder = Path.Combine(_copy, "data", "history", key.Folder ?? "", key.Id);
        return Directory.Exists(folder) ? [.. Directory.GetFiles(folder).Select(path => Path.GetFileName(path)).Order(StringComparer.Ordinal)] : [];
    }

    /// <summary>The file of a document in the copy: a mirror of the share in <c>data\</c> (decided in M2.7).</summary>
    private string CopyPath(DocumentKey key) => Path.Combine(_copy, "data", key.Folder ?? "", key.Id + ".json");

    /// <summary>The last good bytes of a document kept apart: <c>kept\records\8f3a1c.json</c> (M2.7a).</summary>
    private string KeptPath(DocumentKey key) => Path.Combine(_copy, "kept", key.Folder ?? "", key.Id + ".json");
}

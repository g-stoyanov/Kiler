using System.Text.Json.Nodes;
using Kiler.Storage;

namespace Kiler.Core.Tests;

/// <summary>
/// The delete and the restore from the trash (M5.10): a move, which keeps the version, the dates and the authors; only an
/// editor who holds every level of the record makes them; a delete over another version is a conflict; and the trash in
/// memory follows the writes of this Kiler and the loads, also one that ended before a delete.
/// </summary>
public class RecordDeleteTests : IDisposable
{
    /// <summary>A note whose only group is at level 1.</summary>
    private const string NoteId = "n5p6t7e8";

    private const string NoTemplateId = "n0t1m2p3";

    private const string NotFitId = "f1t2s3n4";

    private readonly TestRecords _kiler = new();

    private readonly Records _records;

    public RecordDeleteTests()
    {
        _kiler.WriteStash(TestRecords.StashId, "Payments");
        _kiler.Write(_kiler.Database());
        _kiler.Write(_kiler.Record(NoteId, template: "note", groups: [(1, """{"text":"A note"}""")]));
        _kiler.Write(_kiler.Record(NoTemplateId, template: "gone", groups: [(1, """{"host":"db07.example.test"}""")]));
        Assert.Equal(WriteOutcome.Written, _kiler.Data.Copy.Create(RecordFile.KeyOf(NotFitId), new JsonObject { ["name"] = "Broken" }).Outcome);
        _records = _kiler.Open();
    }

    public void Dispose()
    {
        _records.Dispose();
        _kiler.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void ADeleteAndARestore_KeepTheVersionTheDatesAndTheAuthors()
    {
        var editor = _kiler.Reader(3);

        var deleted = RecordActions.Delete(editor, _records, TestRecords.Id, 1);

        Assert.Equal(RecordOutcome.Done, deleted.Outcome);
        AssertKept(deleted.Record);
        var set = _records.Read(editor);
        Assert.Equal([NotFitId, NoTemplateId, NoteId], set.Records.Keys);
        Assert.Equal([TestRecords.Id], set.Trash.Keys);
        Assert.Same(deleted.Record, set.Trash[TestRecords.Id]);
        Assert.Equal(("pay_read", null), (set.Trash[TestRecords.Id].Fields[4].Value?.Text, set.RecordName(TestRecords.Id)));

        // Another Records over the copy reads the same, as the copy followed the move.
        using (var again = _kiler.Open())
        {
            Assert.Equal([TestRecords.Id], again.Read(null).Trash.Keys);
            Assert.False(again.Read(null).Records.ContainsKey(TestRecords.Id), "The deleted record is still among the records of the copy.");
        }

        var restored = RecordActions.Restore(editor, _records, TestRecords.Id);

        Assert.Equal(RecordOutcome.Done, restored.Outcome);
        AssertKept(restored.Record);
        set = _records.Read(editor);
        Assert.Empty(set.Trash);
        Assert.Same(restored.Record, set.Records[TestRecords.Id]);
        Assert.Equal(SecretOutcome.Value, set.Records[TestRecords.Id].Secret("password", editor, out var value));
        Assert.True(value == TestRecords.Password, "The password is not the one that the record had.");
    }

    [Fact]
    public void ADeleteOverAnotherVersion_IsAConflict_WithTheRecordAsItIsNow()
    {
        var stale = RecordActions.Delete(_kiler.Reader(3), _records, TestRecords.Id, 2);

        Assert.Equal((RecordOutcome.Conflict, 1), (stale.Outcome, stale.Record?.File.Version));

        // Another Kiler has written a version that this one has not loaded yet: the delete meets it on the share.
        Assert.Equal(WriteOutcome.Written, _kiler.Data.OpenCopy().Replace(RecordFile.KeyOf(TestRecords.Id), 1, Elsewhere().ToContent()).Outcome);

        var conflict = RecordActions.Delete(_kiler.Reader(3), _records, TestRecords.Id, 1);

        Assert.Equal((RecordOutcome.Conflict, 2, "Changed elsewhere"), (conflict.Outcome, conflict.Record?.File.Version, conflict.Record?.File.Model?.Name));
        Assert.Equal((1, false), (_records.Read(null).Records[TestRecords.Id].File.Version, _records.Read(null).Trash.ContainsKey(TestRecords.Id)));
    }

    [Fact]
    public void AnEditorBelowALevelOfTheRecord_NeitherDeletesNorRestoresIt()
    {
        Assert.Equal(RecordOutcome.NotHeld, RecordActions.Delete(_kiler.Reader(2), _records, TestRecords.Id, 1).Outcome);
        Assert.True(_records.Read(null).Records.ContainsKey(TestRecords.Id), "A refused delete moved the record.");
        Assert.Equal(RecordOutcome.Done, RecordActions.Delete(_kiler.Reader(3), _records, TestRecords.Id, 1).Outcome);

        Assert.Equal(RecordOutcome.NotHeld, RecordActions.Restore(_kiler.Reader(2), _records, TestRecords.Id).Outcome);
        Assert.True(_records.Read(null).Trash.ContainsKey(TestRecords.Id), "A refused restore moved the record.");

        // An editor of level 1 holds the only level of the note.
        Assert.Equal(RecordOutcome.Done, RecordActions.Delete(_kiler.Reader(1), _records, NoteId, 1).Outcome);
        Assert.Equal(RecordOutcome.Done, RecordActions.Restore(_kiler.Reader(1), _records, NoteId).Outcome);
    }

    [Theory]
    [InlineData("y2z3a4b5")]
    [InlineData(NotFitId)]
    public void ARecordThatIsNotThere_OrDoesNotFit_IsNeitherDeletedNorRestored(string id)
    {
        var editor = _kiler.Reader(3);

        var deleted = RecordActions.Delete(editor, _records, id, 1);

        Assert.Equal((RecordOutcome.Missing, null), (deleted.Outcome, deleted.Record));
        Assert.Equal(RecordOutcome.Missing, RecordActions.Restore(editor, _records, id).Outcome);
        Assert.Equal(RecordOutcome.Missing, RecordActions.Restore(editor, _records, TestRecords.Id).Outcome);
        Assert.Empty(_records.Read(editor).Trash);
    }

    [Fact]
    public void ARecordWhoseTemplateIsMissing_IsDeletedAndRestored()
    {
        Assert.Equal(RecordOutcome.Done, RecordActions.Delete(_kiler.Reader(1), _records, NoTemplateId, 1).Outcome);
        Assert.Equal(RecordOutcome.Done, RecordActions.Restore(_kiler.Reader(1), _records, NoTemplateId).Outcome);

        Assert.Equal(1, _records.Read(null).Records[NoTemplateId].File.Version);
    }

    [Fact]
    public void ALoadThatEndedBeforeADelete_DoesNotBringTheRecordBack()
    {
        // The first load gives the record again, with the owner that the write of the copy did not know (core.md), at the
        // version that the delete then moves; the watch raises it after it has let go of the copy.
        var before = _kiler.Data.Copy.Load();
        Assert.Contains(before.Changed, loaded => loaded.Key == RecordFile.KeyOf(TestRecords.Id) && loaded.Read.Document?.Version == 1);
        Assert.Equal(RecordOutcome.Done, RecordActions.Delete(_kiler.Reader(3), _records, TestRecords.Id, 1).Outcome);

        Follow(before);

        var set = _records.Read(null);
        Assert.False(set.Records.ContainsKey(TestRecords.Id), "A load that ended before the delete brought the record back.");
        Assert.True(set.Trash.ContainsKey(TestRecords.Id), "A load that ended before the delete took the record out of the trash.");
    }

    [Fact]
    public void ARecordThatAnotherKilerRestored_IsFollowed_AndARestoreHereIsMissing()
    {
        Assert.Equal(RecordOutcome.Done, RecordActions.Delete(_kiler.Reader(3), _records, TestRecords.Id, 1).Outcome);
        var other = _kiler.Data.OpenCopy();
        other.Load();
        Assert.Equal(WriteOutcome.Written, other.RestoreFromTrash(RecordFile.KeyOf(TestRecords.Id)).Outcome);

        // The trash of this Kiler still has it, but the share has it in its folder again.
        Assert.Equal(RecordOutcome.Missing, RecordActions.Restore(_kiler.Reader(3), _records, TestRecords.Id).Outcome);

        // The first load after the delete gives the version deleted from its folder: the trash has lost the record since.
        var load = _kiler.Data.Copy.Load();
        Assert.True(load.TrashChanged, "The load did not see the record leave the trash.");
        Follow(load);

        var set = _records.Read(null);
        Assert.Equal((1, false), (set.Records[TestRecords.Id].File.Version, set.Trash.ContainsKey(TestRecords.Id)));
    }

    [Fact]
    public void ARestoreOfAnotherVersionThanTheTrashHas_ReadsTheCopyAgain()
    {
        Assert.Equal(RecordOutcome.Done, RecordActions.Delete(_kiler.Reader(3), _records, TestRecords.Id, 1).Outcome);

        // Another Kiler restores it, changes it and deletes it again, and this one loads none of it.
        var other = _kiler.Data.OpenCopy();
        other.Load();
        var key = RecordFile.KeyOf(TestRecords.Id);
        Assert.Equal(WriteOutcome.Written, other.RestoreFromTrash(key).Outcome);
        Assert.Equal(WriteOutcome.Written, other.Replace(key, 1, Elsewhere().ToContent()).Outcome);
        Assert.Equal(WriteOutcome.Deleted, other.Delete(key, 2).Outcome);
        Assert.Equal(1, _records.Read(null).Trash[TestRecords.Id].File.Version);

        var restored = RecordActions.Restore(_kiler.Reader(3), _records, TestRecords.Id);

        Assert.Equal((RecordOutcome.Done, 2, "Changed elsewhere"), (restored.Outcome, restored.Record?.File.Version, restored.Record?.File.Model?.Name));
        var set = _records.Read(null);
        Assert.Equal((2, false), (set.Records[TestRecords.Id].File.Version, set.Trash.ContainsKey(TestRecords.Id)));
    }

    [Fact]
    public void ALockedEditor_NeitherDeletesNorRestores()
    {
        Assert.Equal(RecordOutcome.Done, RecordActions.Delete(_kiler.Reader(3), _records, NoteId, 1).Outcome);
        var editor = _kiler.Reader(3);
        editor.Dispose();

        Assert.Throws<SessionLockedException>(() => RecordActions.Delete(editor, _records, TestRecords.Id, 1));
        Assert.Throws<SessionLockedException>(() => RecordActions.Restore(editor, _records, NoteId));

        var set = _records.Read(null);
        Assert.Equal((true, true), (set.Records.ContainsKey(TestRecords.Id), set.Trash.ContainsKey(NoteId)));
    }

    /// <summary>The database, renamed by another Kiler, without its groups.</summary>
    private static RecordFile Elsewhere() =>
        new(TestRecords.Id, "db", TestRecords.Authors, [TestRecords.StashId], "Changed elsewhere", [], new Dictionary<string, FieldValue>(), new Dictionary<int, string>(), new Dictionary<int, IReadOnlyList<string>>());

    /// <summary>The database of <paramref name="view"/> keeps its first version, its dates and its authors, as a move does (M2.5).</summary>
    private static void AssertKept(RecordView? view)
    {
        Assert.NotNull(view);
        var authors = Assert.IsType<RecordFile>(view.File.Model).Authorship;
        var made = TestRecords.Authors;
        Assert.Equal(
            (1, made.CreatedBy, made.CreatedAt, made.UpdatedBy, made.UpdatedAt),
            (view.File.Version, authors.CreatedBy, authors.CreatedAt, authors.UpdatedBy, authors.UpdatedAt));
    }

    private void Follow(LoadResult load)
    {
        _kiler.Team.Follow(load);
        _records.Follow(load);
    }
}

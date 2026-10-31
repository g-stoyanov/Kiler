using System.Text.Json.Nodes;
using Kiler.Storage;
using Microsoft.Extensions.Time.Testing;

namespace Kiler.Core.Tests;

/// <summary>
/// The history of a record and the restore of an old version (M5.11): the old versions that the copy keeps, the newest first;
/// a restore makes one the next version, its groups as they were, with the dates and the author of the restore; only an
/// editor who holds every level of the current version and of the old one makes it; one over another version is a conflict.
/// </summary>
public class RecordHistoryTests : IDisposable
{
    private const string NoteId = "n5p6t7e8";

    private const string NoTemplateId = "n0t1m2p3";

    private static readonly DateTime Second = new(2026, 10, 22, 7, 0, 0, DateTimeKind.Utc);

    private static readonly DateTime Third = new(2026, 10, 23, 16, 45, 0, DateTimeKind.Utc);

    private readonly TestRecords _kiler = new();

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 31, 9, 15, 30, 250, TimeSpan.Zero));

    private readonly Records _records;

    public RecordHistoryTests()
    {
        _kiler.WriteStash(TestRecords.StashId, "Payments");
        _kiler.Write(_kiler.Database());
        _kiler.Write(_kiler.Record(NoteId, template: "note", groups: [(1, """{"text":"A note"}""")]));
        _kiler.Write(_kiler.Record(NoTemplateId, template: "gone", groups: [(1, """{"host":"db07.example.test"}""")]));
        _records = _kiler.Open();
    }

    public void Dispose()
    {
        _records.Dispose();
        _kiler.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void TheHistory_OfARecordChangedSeveralTimes_GivesWhoChangedItAndWhen_TheNewestFirst()
    {
        Change(1, "Second", "demo.admin", Second, groups: [(1, """{"host":"db02.example.test"}""")]);
        Change(2, "Third", "demo.user", Third);

        using var again = _kiler.Open();
        foreach (var history in (IReadOnlyList<TeamFile<RecordFile>>[])[_records.History(TestRecords.Id, 3), again.History(TestRecords.Id, 3)])
        {
            Assert.Equal([2, 1], history.Select(file => file.Version.GetValueOrDefault()).ToArray());
            Assert.Equal(
                [("Second", "demo.admin", Second, "1"), ("Payments database", "demo.editor", TestRecords.Authors.UpdatedAt, "1,3")],
                history.Select(file => (file.Model!.Name, file.Model.Authorship.UpdatedBy, file.Model.Authorship.UpdatedAt, string.Join(',', file.Model.Protected.Keys))).ToArray());
        }

        // Only the versions below the one given.
        Assert.Equal([1], _records.History(TestRecords.Id, 2).Select(file => file.Version.GetValueOrDefault()).ToArray());
        Assert.Empty(_records.History(NoteId, 1));
    }

    [Fact]
    public void ARestoreOfAnOldVersion_MakesItTheNextVersion_WithTheDatesAndTheAuthorOfTheRestore()
    {
        var first = _records.Read(null).Records[TestRecords.Id].File.Model!;
        Change(1, "Second", "demo.admin", Second, groups: [(1, """{"host":"db02.example.test"}""")]);
        var editor = _kiler.Reader(3);

        var result = RecordActions.RestoreVersion(editor, _records, TestRecords.Id, 1, 2, _time);

        Assert.Equal(RecordOutcome.Done, result.Outcome);
        var view = Assert.IsType<RecordView>(result.Record);
        var restored = view.File.Model!;
        Assert.Equal(
            (3, "Payments database", "demo.admin", TestRecords.Authors.CreatedAt, "demo.user", _time.GetUtcNow().UtcDateTime),
            (view.File.Version, restored.Name, restored.Authorship.CreatedBy, restored.Authorship.CreatedAt, restored.Authorship.UpdatedBy, restored.Authorship.UpdatedAt));
        Assert.Equal(first.Protected, restored.Protected);
        Assert.Equal(first.Fields, restored.Fields);
        Assert.Equal(first.Public, restored.Public);
        Assert.Same(view, _records.Read(editor).Records[TestRecords.Id]);
        Assert.Equal(SecretOutcome.Value, view.Secret("password", editor, out var value));
        Assert.True(value == TestRecords.Password, "The password is not the one that the old version had.");

        // The version that the restore replaced is in the history, and another Records over the copy reads the restore.
        Assert.Equal([2, 1], _records.History(TestRecords.Id, 3).Select(file => file.Version.GetValueOrDefault()).ToArray());
        using var again = _kiler.Open();
        Assert.Equal((3, "Payments database"), (again.Read(null).Records[TestRecords.Id].File.Version, again.Read(null).Records[TestRecords.Id].File.Model?.Name));
    }

    [Fact]
    public void ARestoreOverAnotherVersion_IsAConflict_WithTheRecordAsItIsNow()
    {
        Change(1, "Second", "demo.admin", Second);

        var stale = RecordActions.RestoreVersion(_kiler.Reader(3), _records, TestRecords.Id, 1, 1, _time);

        Assert.Equal((RecordOutcome.Conflict, 2), (stale.Outcome, stale.Record?.File.Version));

        // Another Kiler has written a version that this one has not loaded yet: the restore meets it on the share.
        Assert.Equal(WriteOutcome.Written, _kiler.Data.OpenCopy().Replace(RecordFile.KeyOf(TestRecords.Id), 2, Version("Changed elsewhere", "demo.admin", Third).ToContent()).Outcome);

        var conflict = RecordActions.RestoreVersion(_kiler.Reader(3), _records, TestRecords.Id, 1, 2, _time);

        Assert.Equal((RecordOutcome.Conflict, 3, "Changed elsewhere"), (conflict.Outcome, conflict.Record?.File.Version, conflict.Record?.File.Model?.Name));
        Assert.Equal((2, "Second"), (_records.Read(null).Records[TestRecords.Id].File.Version, _records.Read(null).Records[TestRecords.Id].File.Model?.Name));
    }

    /// <summary>A restore writes the groups of every level, so the editor holds those of the current version and those of the old one ("Decided in M5.1").</summary>
    [Fact]
    public void AnEditorBelowALevelOfTheCurrentOrTheOldVersion_DoesNotRestoreIt()
    {
        // The second version has no group at level 3, the third has it again.
        Change(1, "Second", "demo.admin", Second, groups: [(1, """{"host":"db02.example.test"}""")]);
        Assert.Equal(RecordOutcome.NotHeld, RecordActions.RestoreVersion(_kiler.Reader(2), _records, TestRecords.Id, 1, 2, _time).Outcome);
        Change(2, "Third", "demo.admin", Third, groups: [(1, """{"host":"db03.example.test"}"""), (3, """{"user":"pay_admin"}""")]);
        Assert.Equal(RecordOutcome.NotHeld, RecordActions.RestoreVersion(_kiler.Reader(2), _records, TestRecords.Id, 2, 3, _time).Outcome);
        Assert.Equal((3, "Third"), (_records.Read(null).Records[TestRecords.Id].File.Version, _records.Read(null).Records[TestRecords.Id].File.Model?.Name));

        Assert.Equal(RecordOutcome.Done, RecordActions.RestoreVersion(_kiler.Reader(3), _records, TestRecords.Id, 2, 3, _time).Outcome);

        // An editor of level 1 holds the only level of the note, and of a record whose template is missing.
        foreach (var id in (string[])[NoteId, NoTemplateId])
        {
            Change(1, "Second", "demo.admin", Second, id, [(1, """{"text":"Another note"}""")]);
            Assert.Equal(RecordOutcome.Done, RecordActions.RestoreVersion(_kiler.Reader(1), _records, id, 1, 2, _time).Outcome);
            Assert.Equal((3, "Payments database"), (_records.Read(null).Records[id].File.Version, _records.Read(null).Records[id].File.Model?.Name));
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void AnOldVersionThatTheHistoryDoesNotHave_IsNotRestored(int old)
    {
        // The second version does not fit, so the history leaves it out.
        Assert.Equal(WriteOutcome.Written, _kiler.Data.Copy.Replace(RecordFile.KeyOf(TestRecords.Id), 1, new JsonObject { ["name"] = "Broken" }).Outcome);
        Change(2, "Third", "demo.admin", Third);

        var result = RecordActions.RestoreVersion(_kiler.Reader(3), _records, TestRecords.Id, old, 3, _time);

        Assert.Equal((RecordOutcome.NoVersion, null), (result.Outcome, result.Record));
        Assert.Equal([1], _records.History(TestRecords.Id, 3).Select(file => file.Version.GetValueOrDefault()).ToArray());
        Assert.Equal(3, _records.Read(null).Records[TestRecords.Id].File.Version);
    }

    /// <summary>
    /// A delete keeps the version deleted in the history of the copy (M2.7a), and a restore from the trash makes it the current
    /// one again: the history gives only the versions below it, so the current version is not an old one.
    /// </summary>
    [Fact]
    public void TheHistory_OfARecordRestoredFromTheTrash_LeavesOutTheCurrentVersion()
    {
        var editor = _kiler.Reader(1);
        Assert.Equal(RecordOutcome.Done, RecordActions.Delete(editor, _records, NoteId, 1).Outcome);
        Assert.Equal(RecordOutcome.Done, RecordActions.Restore(editor, _records, NoteId).Outcome);

        Assert.Empty(_records.History(NoteId, 1));
        Assert.Equal(RecordOutcome.NoVersion, RecordActions.RestoreVersion(editor, _records, NoteId, 1, 1, _time).Outcome);
    }

    [Fact]
    public void ARecordThatIsNotThere_IsNotRestored()
    {
        Change(1, "Second", "demo.admin", Second);
        Assert.Equal(RecordOutcome.Done, RecordActions.Delete(_kiler.Reader(3), _records, TestRecords.Id, 2).Outcome);

        Assert.Equal(RecordOutcome.Missing, RecordActions.RestoreVersion(_kiler.Reader(3), _records, TestRecords.Id, 1, 2, _time).Outcome);
        Assert.Equal(RecordOutcome.Missing, RecordActions.RestoreVersion(_kiler.Reader(3), _records, "y2z3a4b5", 1, 2, _time).Outcome);
        Assert.True(_records.Read(null).Trash.ContainsKey(TestRecords.Id), "A refused restore of an old version moved the record.");
    }

    [Fact]
    public void ALockedEditor_DoesNotRestore()
    {
        Change(1, "Second", "demo.admin", Second);
        var editor = _kiler.Reader(3);
        editor.Dispose();

        Assert.Throws<SessionLockedException>(() => RecordActions.RestoreVersion(editor, _records, TestRecords.Id, 1, 2, _time));

        Assert.Equal(2, _records.Read(null).Records[TestRecords.Id].File.Version);
    }

    /// <summary>The database as another write makes it, with <paramref name="groups"/>: its name, and who changed it and when.</summary>
    private RecordFile Version(string name, string by, DateTime at, string id = TestRecords.Id, params (int Level, string Json)[] groups) =>
        new(
            id,
            _records.Read(null).Records[id].File.Model!.Template,
            new Authorship(TestRecords.Authors.CreatedBy, TestRecords.Authors.CreatedAt, by, at),
            [TestRecords.StashId],
            name,
            [],
            new Dictionary<string, FieldValue>(),
            groups.ToDictionary(group => group.Level, group => _kiler.Group(id, group.Level, group.Json)),
            groups.ToDictionary(group => group.Level, group => (IReadOnlyList<string>)[.. JsonNode.Parse(group.Json)!.AsObject().Select(member => member.Key)]));

    /// <summary>Replaces <paramref name="version"/> of the record through <see cref="Records"/>, as an edit of this Kiler does.</summary>
    private void Change(int version, string name, string by, DateTime at, string id = TestRecords.Id, params (int Level, string Json)[] groups) =>
        Assert.Equal(WriteOutcome.Written, _records.Replace(Version(name, by, at, id, groups), version).Outcome);
}

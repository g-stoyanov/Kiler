using Kiler.Storage;
using Microsoft.Extensions.Time.Testing;

namespace Kiler.Core.Tests;

/// <summary>
/// The stashes of an admin (M6.22, docs/tasks/M6.md, "Rename, merge and delete"): the list with the number of the shared
/// records of each, through the merged and deleted stashes; the rename, which every path then shows; the merge into another
/// live stash, which the paths then read; and the delete of a stash that no shared record has. Each is over the version of
/// the stash, and refuses what it must.
/// </summary>
public class StashActionsTests : IDisposable
{
    // Live stashes: Payments has the record of TestRecords, under it Prod; Reports and Spare have none.
    private const string Payments = TestRecords.StashId;
    private const string Prod = "e5f6g7h8";
    private const string Reports = "j9k0m1n2";
    private const string Spare = "p3q4r5s6";

    // Merged into Prod, and deleted; and a stash that no file has.
    private const string OldProd = "m3m3m3m3";
    private const string Gone = "t9t9t9t9";
    private const string Missing = "w1w1w1w1";

    private readonly TestRecords _kiler = new();

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 31, 9, 15, 30, TimeSpan.Zero));

    private readonly Records _records;

    public StashActionsTests()
    {
        _kiler.WriteStash(Payments, "Payments");
        _kiler.WriteStash(Prod, "Prod");
        _kiler.WriteStash(Reports, "Reports");
        _kiler.WriteStash(Spare, "Spare");
        Write(new StashFile(OldProd, "Old prod", TestRecords.Authors, redirect: Prod));
        Write(new StashFile(Gone, "Archive", TestRecords.Authors, deleted: true));

        // One record under Payments and Prod, one under the merged stash, which reads as Prod, and one in the trash under Reports.
        _kiler.Write(WithPath(_kiler.Record(), Payments, Prod));
        _kiler.Write(WithPath(_kiler.Record("q5r6s7t8"), Payments, OldProd));
        var trashed = WithPath(_kiler.Record("v9w0x1y2"), Reports);
        _kiler.Write(trashed);
        Assert.Equal(WriteOutcome.Deleted, _kiler.Data.Copy.Delete(trashed.Key, 1).Outcome);
        _records = _kiler.Open();
    }

    private Session Admin => _kiler.Reader(3);

    public void Dispose()
    {
        _records.Dispose();
        _kiler.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void TheList_HasTheLiveStashesByName_WithTheSharedRecordsOfEach_ThroughAMerge_AndNotTheTrash()
    {
        var list = StashActions.List(_records.Read(Admin));

        Assert.Equal(
            [new(Payments, "Payments", 1, 2), new(Prod, "Prod", 1, 2), new(Reports, "Reports", 1, 0), new(Spare, "Spare", 1, 0)],
            list);
    }

    [Fact]
    public void ARename_IsWrittenOverItsVersion_AndIsSeenWhereverThePathShows()
    {
        var result = StashActions.Rename(Admin, _records, Prod, 1, "  Production  ", _time);

        Assert.Equal(StashOutcome.Done, result.Outcome);
        var set = _records.Read(Admin);
        var file = set.Stashes[Prod];
        Assert.Equal((2, "Production", "demo.user", _time.GetUtcNow().UtcDateTime), (file.Version, file.Model!.Name, file.Model.Authorship.UpdatedBy, file.Model.Authorship.UpdatedAt));
        Assert.Equal(("demo.admin", TestRecords.Authors.CreatedAt), (file.Model.Authorship.CreatedBy, file.Model.Authorship.CreatedAt));
        Assert.Equal("Production", set.StashName(Prod));
        var payments = Assert.Single(RecordSearch.Find(set, new RecordQuery()).Tree);
        Assert.Equal(("Production", 2), (Assert.Single(payments.Children).Name, Assert.Single(payments.Children).Count));
        Assert.Contains(RecordSearch.Find(set, new RecordQuery { Text = "production" }).Records, item => item.File.Model?.Id == TestRecords.Id);

        // The copy has it too, so a new start reads it.
        using var again = _kiler.Open();
        Assert.Equal("Production", again.Read(null).StashName(Prod));
    }

    [Theory]
    [InlineData("reports", StashOutcome.Exists, null)]
    [InlineData(" SPARE ", StashOutcome.Exists, null)]
    [InlineData("Archive", StashOutcome.Done, null)]
    [InlineData("Old prod", StashOutcome.Done, null)]
    [InlineData("PROD", StashOutcome.Done, null)]
    [InlineData("   ", StashOutcome.Invalid, "name")]
    [InlineData(null, StashOutcome.Invalid, "name")]
    public void ARename_RefusesTheNameOfAnotherLiveStash_AndTakesThatOfAMergedOrDeletedOne_OrItsOwnInAnotherCase(string? name, StashOutcome outcome, string? field)
    {
        var result = StashActions.Rename(Admin, _records, Prod, 1, name, _time);

        Assert.Equal((outcome, field), (result.Outcome, result.Field));
        Assert.Equal(outcome == StashOutcome.Done ? 2 : 1, _records.Read(Admin).Stashes[Prod].Version);
    }

    [Fact]
    public void AMerge_WritesTheRedirect_AndThePathsAndTheCountsAreReadThroughIt()
    {
        var result = StashActions.Merge(Admin, _records, Prod, 1, Reports, _time);

        Assert.Equal(StashOutcome.Done, result.Outcome);
        var set = _records.Read(Admin);
        Assert.Equal((2, Reports, false, "Prod"), (set.Stashes[Prod].Version, set.Stashes[Prod].Model!.Redirect, set.Stashes[Prod].Model!.Deleted, set.Stashes[Prod].Model!.Name));
        Assert.Equal([Payments, Reports], set.PathOf(set.Records[TestRecords.Id].File.Model!.Stash));
        Assert.Equal([Payments, Prod], set.Records[TestRecords.Id].File.Model!.Stash);
        Assert.Equal(
            [new(Payments, "Payments", 1, 2), new(Reports, "Reports", 1, 2), new(Spare, "Spare", 1, 0)],
            StashActions.List(set));
    }

    [Theory]
    [InlineData(Prod, StashActions.IntoItself)]
    [InlineData(OldProd, StashActions.TargetGone)]
    [InlineData(Gone, StashActions.TargetGone)]
    [InlineData(Missing, StashActions.TargetGone)]
    [InlineData("", StashActions.NoTarget)]
    [InlineData(null, StashActions.NoTarget)]
    public void AMerge_IntoItself_OrIntoAStashThatIsNotLive_IsRefused(string? into, string invalid)
    {
        var result = StashActions.Merge(Admin, _records, Prod, 1, into, _time);

        Assert.Equal((StashOutcome.Invalid, "into", invalid), (result.Outcome, result.Field, result.Invalid));
        Assert.Equal(1, _records.Read(Admin).Stashes[Prod].Version);
    }

    [Fact]
    public void AStashThatASharedRecordHas_IsNotDeleted_EvenThroughAMerge()
    {
        Assert.Equal(StashOutcome.Done, StashActions.Merge(Admin, _records, Prod, 1, Spare, _time).Outcome);

        var result = StashActions.Delete(Admin, _records, Spare, 1, _time);

        Assert.Equal((StashOutcome.Used, 2), (result.Outcome, result.Records));
        Assert.Equal(1, _records.Read(Admin).Stashes[Spare].Version);
    }

    [Fact]
    public void AStashThatOnlyARecordOfTheTrashHas_IsDeleted_AndThatRecordRestoredIsCutBeforeIt()
    {
        var result = StashActions.Delete(Admin, _records, Reports, 1, _time);

        Assert.Equal(StashOutcome.Done, result.Outcome);
        var set = _records.Read(Admin);
        Assert.Equal((2, true, null), (set.Stashes[Reports].Version, set.Stashes[Reports].Model!.Deleted, set.Stashes[Reports].Model!.Redirect));
        Assert.Empty(set.PathOf(set.Trash["v9w0x1y2"].File.Model!.Stash));
        Assert.DoesNotContain(StashActions.List(set), stash => stash.Id == Reports);
    }

    [Theory]
    [InlineData("rename")]
    [InlineData("merge")]
    [InlineData("delete")]
    public void AnActionOverAnotherVersion_IsAConflict_AndOnAStashThatIsNotLive_FindsNone(string action)
    {
        StashResult Act(string id, int version) => action switch
        {
            "rename" => StashActions.Rename(Admin, _records, id, version, "Renamed", _time),
            "merge" => StashActions.Merge(Admin, _records, id, version, Payments, _time),
            _ => StashActions.Delete(Admin, _records, id, version, _time),
        };

        Assert.Equal(StashOutcome.Conflict, Act(Spare, 2).Outcome);
        Assert.Equal(StashOutcome.Conflict, Act(Spare, 0).Outcome);
        Assert.Equal(StashOutcome.Missing, Act(OldProd, 1).Outcome);
        Assert.Equal(StashOutcome.Missing, Act(Gone, 1).Outcome);
        Assert.Equal(StashOutcome.Missing, Act(Missing, 1).Outcome);
        Assert.Equal(1, _records.Read(Admin).Stashes[Spare].Version);
    }

    [Fact]
    public void AnActionOverAVersionThatTheShareNoLongerHas_IsAConflict()
    {
        // Another Kiler has renamed the stash, and no load has brought it in.
        var other = _kiler.Data.OpenCopy();
        other.Load();
        Assert.Equal(WriteOutcome.Written, other.Replace(StashFile.KeyOf(Spare), 1, new StashFile(Spare, "Spare parts", TestRecords.Authors).ToContent()).Outcome);

        Assert.Equal(StashOutcome.Conflict, StashActions.Rename(Admin, _records, Spare, 1, "Renamed", _time).Outcome);
        Assert.Equal((1, "Spare"), (_records.Read(Admin).Stashes[Spare].Version, _records.Read(Admin).StashName(Spare)));
    }

    [Fact]
    public void ANameThatMakesTheStashLargerThanADocument_IsRefused()
    {
        var result = StashActions.Rename(Admin, _records, Spare, 1, new string('x', DocumentFormat.MaxSize), _time);

        Assert.Equal((StashOutcome.Invalid, "name", StashActions.LongName), (result.Outcome, result.Field, result.Invalid));
    }

    [Fact]
    public void ALockedSession_IsRefused()
    {
        var admin = Admin;
        admin.Dispose();

        Assert.Throws<SessionLockedException>(() => StashActions.Rename(admin, _records, Spare, 1, "Renamed", _time));
    }

    private static RecordFile WithPath(RecordFile record, params string[] stash) =>
        new(record.Id, record.Template, record.Authorship, stash, record.Name, record.Tags, record.Public, record.Protected, record.Fields);

    /// <summary>Writes a stash through the copy.</summary>
    private void Write(StashFile stash) => Assert.Equal(WriteOutcome.Written, _kiler.Data.Copy.Create(stash.Key, stash.ToContent()).Outcome);
}

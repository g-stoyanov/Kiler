using Microsoft.Extensions.Time.Testing;

namespace Kiler.Storage.Tests;

/// <summary>The rule that reports a document as edited in place only when its stamp has stayed so (M2.3), on a fake clock.</summary>
public sealed class InPlaceEditsTests
{
    private static readonly DocumentKey Record = DocumentKey.InFolder(DataFolder.Records, "8f3a1c");
    private static readonly DocumentKey Other = DocumentKey.InFolder(DataFolder.Records, "b72e09");
    private static readonly DateTimeOffset Created = new(2026, 10, 26, 8, 0, 0, TimeSpan.Zero);

    /// <summary>As Kiler writes a file: its creation time is its write time.</summary>
    private static readonly FileStamp Written = new(120, Created, Created);

    /// <summary>The same file after an edit in place a minute later.</summary>
    private static readonly FileStamp Edited = new(121, Created, Created.AddMinutes(1));

    private readonly FakeTimeProvider _time = new();
    private readonly InPlaceEdits _edits;

    public InPlaceEditsTests() => _edits = new InPlaceEdits(_time);

    [Fact]
    public void ShouldReport_AFileWrittenByKiler_IsNeverReported()
    {
        Assert.False(_edits.ShouldReport(Record, Written));
        _time.Advance(TimeSpan.FromMinutes(5));
        Assert.False(_edits.ShouldReport(Record, Written));
    }

    [Fact]
    public void ShouldReport_AnEditedFile_IsReportedOnlyOnceItHasStayedSoForTheSettleTime()
    {
        Assert.False(_edits.ShouldReport(Record, Edited));
        _time.Advance(InPlaceEdits.SettleTime - TimeSpan.FromSeconds(1));
        Assert.False(_edits.ShouldReport(Record, Edited));
        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.True(_edits.ShouldReport(Record, Edited));
        _time.Advance(TimeSpan.FromMinutes(5));
        Assert.True(_edits.ShouldReport(Record, Edited));
    }

    /// <summary>
    /// A write of Kiler looks edited in place until its times are set after the rename, and another machine can go on seeing
    /// that for about 10 s through its SMB cache.
    /// </summary>
    [Fact]
    public void ShouldReport_TheMomentAfterAWriteOfKiler_IsNotReported()
    {
        var tunneled = new FileStamp(130, Created.AddDays(-30), Created);
        var settled = new FileStamp(130, Created, Created);

        Assert.False(_edits.ShouldReport(Record, tunneled));
        _time.Advance(TimeSpan.FromSeconds(10));
        Assert.False(_edits.ShouldReport(Record, tunneled));
        _time.Advance(TimeSpan.FromSeconds(10));
        Assert.False(_edits.ShouldReport(Record, settled));
        _time.Advance(TimeSpan.FromMinutes(5));
        Assert.False(_edits.ShouldReport(Record, settled));
    }

    [Fact]
    public void ShouldReport_AFileEditedAgain_WaitsTheSettleTimeFromTheLastEdit()
    {
        var editedAgain = Edited with { Size = 125, LastWriteTime = Created.AddMinutes(2) };

        Assert.False(_edits.ShouldReport(Record, Edited));
        _time.Advance(TimeSpan.FromSeconds(20));
        Assert.False(_edits.ShouldReport(Record, editedAgain));
        _time.Advance(TimeSpan.FromSeconds(20));
        Assert.False(_edits.ShouldReport(Record, editedAgain));
        _time.Advance(InPlaceEdits.SettleTime - TimeSpan.FromSeconds(20));
        Assert.True(_edits.ShouldReport(Record, editedAgain));
    }

    [Fact]
    public void ShouldReport_AFileThatKilerWritesAgain_IsNoLongerReportedAndStartsAfresh()
    {
        Assert.False(_edits.ShouldReport(Record, Edited));
        _time.Advance(InPlaceEdits.SettleTime);
        Assert.True(_edits.ShouldReport(Record, Edited));

        Assert.False(_edits.ShouldReport(Record, Written));
        Assert.False(_edits.ShouldReport(Record, Edited));
    }

    [Fact]
    public void ShouldReport_TwoDocuments_AreWatchedApart()
    {
        Assert.False(_edits.ShouldReport(Record, Edited));
        _time.Advance(TimeSpan.FromSeconds(20));
        Assert.False(_edits.ShouldReport(Other, Edited));
        _time.Advance(TimeSpan.FromSeconds(10));

        Assert.True(_edits.ShouldReport(Record, Edited));
        Assert.False(_edits.ShouldReport(Other, Edited));
    }

    [Fact]
    public void Forget_ADocument_StartsItsWaitAfresh()
    {
        Assert.False(_edits.ShouldReport(Record, Edited));
        _time.Advance(InPlaceEdits.SettleTime);

        _edits.Forget(Record);

        Assert.False(_edits.ShouldReport(Record, Edited));
    }
}

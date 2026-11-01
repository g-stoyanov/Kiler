using Kiler.Storage;

namespace Kiler.Core.Tests;

/// <summary>
/// Merged and deleted stashes (M6.21, design 6.6, docs/tasks/M6.md, "Merged and deleted stashes"): <see cref="StashFile"/>
/// reads and writes <c>redirect</c> and <c>deleted</c>, and <see cref="RecordSet.PathOf"/> reads a path through them, so the
/// records keep their places without being written again.
/// </summary>
public class StashPathTests : IDisposable
{
    // Live stashes.
    private const string Payments = "a1b2c3d4";
    private const string Prod = "e5f6g7h8";
    private const string Reports = "j9k0m1n2";

    // Merged into Prod, into Reports and then Payments, into each other, and into a stash that no file has.
    private const string OldProd = "m3m3m3m3";
    private const string Chain = "n4n4n4n4";
    private const string Middle = "p5p5p5p5";
    private const string LoopA = "q6q6q6q6";
    private const string LoopB = "r7r7r7r7";
    private const string ToMissing = "s8s8s8s8";

    // Deleted, merged into a deleted stash, and a stash that no file has.
    private const string Gone = "t9t9t9t9";
    private const string ToGone = "v0v0v0v0";
    private const string Missing = "w1w1w1w1";

    private readonly TestRecords _kiler = new();

    public StashPathTests()
    {
        _kiler.WriteStash(Payments, "Payments");
        _kiler.WriteStash(Prod, "Prod");
        _kiler.WriteStash(Reports, "Reports");
        Write(OldProd, Prod);
        Write(Chain, Middle);
        Write(Middle, Payments);
        Write(LoopA, LoopB);
        Write(LoopB, LoopA);
        Write(ToMissing, Missing);
        Write(Gone, deleted: true);
        Write(ToGone, Gone);
    }

    public void Dispose()
    {
        _kiler.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void AStash_ReadsWhatItWrites_ItsRedirectAndDeleted_EachLeftOutOtherwise()
    {
        var merged = new StashFile(OldProd, "Prod", TestRecords.Authors, redirect: Prod);
        var deleted = new StashFile(Gone, "Gone", TestRecords.Authors, deleted: true);
        var live = new StashFile(Prod, "Prod", TestRecords.Authors);

        Assert.Equal($$"""{"name":"Prod","createdBy":"demo.admin","createdAt":"2026-09-30T08:10:00Z","updatedBy":"demo.editor","updatedAt":"2026-10-21T11:32:00Z","redirect":"{{Prod}}"}""", merged.ToContent().ToJsonString());
        Assert.EndsWith("""Z","deleted":true}""", deleted.ToContent().ToJsonString(), StringComparison.Ordinal);
        Assert.EndsWith("""Z"}""", live.ToContent().ToJsonString(), StringComparison.Ordinal);
        foreach (var stash in new[] { merged, deleted, live })
        {
            Assert.True(StashFile.TryRead(stash.Key, stash.ToContent(), out var read, out var invalid), invalid);
            Assert.Equal((stash.Redirect, stash.Deleted, stash.IsLive), (read.Redirect, read.Deleted, read.IsLive));
        }

        Assert.Equal((false, false, true), (merged.IsLive, deleted.IsLive, live.IsLive));
    }

    [Theory]
    [InlineData("A1B2C3D4")]
    [InlineData("")]
    [InlineData("../x")]
    public void ARedirectThatIsNotTheIdOfAStash_DoesNotFit(string redirect)
    {
        var content = new StashFile(OldProd, "Prod", TestRecords.Authors).ToContent();
        content["redirect"] = redirect;

        Assert.False(StashFile.TryRead(StashFile.KeyOf(OldProd), content, out _, out var invalid));
        Assert.Equal("redirect is not the id of a stash.", invalid);
        Assert.Throws<ArgumentException>(() => new StashFile(OldProd, "Prod", TestRecords.Authors, redirect: redirect));
    }

    [Theory]
    [InlineData("a merge keeps the path", new[] { Payments, OldProd }, new[] { Payments, Prod })]
    [InlineData("a chain of two merges", new[] { Chain }, new[] { Payments })]
    [InlineData("a stash twice after a merge stays at its first position", new[] { Payments, Prod, Chain }, new[] { Payments, Prod })]
    [InlineData("a loop stays, as a stash that cannot be read", new[] { Payments, LoopA }, new[] { Payments, LoopA })]
    [InlineData("a merge into a missing stash stays, as one that cannot be read", new[] { ToMissing, Prod }, new[] { ToMissing, Prod })]
    [InlineData("a missing stash stays, as before", new[] { Missing, Prod }, new[] { Missing, Prod })]
    [InlineData("the path is cut before a deleted stash", new[] { Payments, Gone, Reports }, new[] { Payments })]
    [InlineData("and before a merge into one", new[] { Payments, Prod, ToGone }, new[] { Payments, Prod })]
    [InlineData("a deleted first stash leaves no path", new[] { Gone, Prod }, new string[0])]
    public void APath_IsReadThroughTheMergedAndDeletedStashes(string what, string[] stash, string[] path)
    {
        using var records = _kiler.Open();
        var set = records.Read(null);

        Assert.True(path.SequenceEqual(set.PathOf(stash)), $"{what}: {string.Join(",", set.PathOf(stash))}");
    }

    [Fact]
    public void OnlyALiveStash_HasAName_SoAWayThatCannotBeFollowedReadsAsAStashThatCannotBeRead()
    {
        using var records = _kiler.Open();
        var set = records.Read(null);

        Assert.Equal(["Prod", null, null, null, null], new[] { Prod, OldProd, LoopA, ToMissing, Gone }.Select(set.StashName));
    }

    [Theory]
    [InlineData(8, true)]
    [InlineData(9, false)]
    public void AnIdFollowsAtMostEightRedirects(int redirects, bool followed)
    {
        // x0000001 leads to x0000002 and so on, and the last of the chain to Reports.
        var ids = Enumerable.Range(1, redirects).Select(number => $"x000000{number}").ToArray();
        for (var i = 0; i < ids.Length; i++)
        {
            Write(ids[i], i + 1 < ids.Length ? ids[i + 1] : Reports);
        }

        using var records = _kiler.Open();

        Assert.Equal(followed ? Reports : ids[0], Assert.Single(records.Read(null).PathOf([ids[0]])));
    }

    [Fact]
    public void ARecord_KeepsItsFile_AndItsPathIsReadWhereverItShows()
    {
        var record = _kiler.Record();
        _kiler.Write(new RecordFile(record.Id, record.Template, record.Authorship, [OldProd, Gone], record.Name, record.Tags, record.Public, record.Protected, record.Fields));
        using var records = _kiler.Open();
        var set = records.Read(null);

        Assert.Equal([OldProd, Gone], set.Records[TestRecords.Id].File.Model!.Stash);
        Assert.Equal([Prod], set.PathOf(set.Records[TestRecords.Id].File.Model!.Stash));
        var node = Assert.Single(RecordSearch.Find(set, new RecordQuery()).Tree);
        Assert.Equal((Prod, "Prod", 1, 0), (node.Id, node.Name, node.Count, node.Children.Count));
    }

    /// <summary>Writes a stash merged into <paramref name="redirect"/>, or deleted, through the copy.</summary>
    private void Write(string id, string? redirect = null, bool deleted = false) =>
        Assert.Equal(WriteOutcome.Written, _kiler.Data.Copy.Create(StashFile.KeyOf(id), new StashFile(id, $"Stash {id}", TestRecords.Authors, redirect, deleted).ToContent()).Outcome);
}

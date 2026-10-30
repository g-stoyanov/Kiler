using System.Diagnostics;
using System.Globalization;
using Kiler.Core;

namespace Kiler.DemoData.Tests;

/// <summary>
/// The volume mode of the demo (M5.5): <see cref="DemoRecords.VolumeCount"/> records in all, and the timing of the search
/// over them (M5.6). The data folder is made once for the class, as its writes take some 30 s.
/// </summary>
public sealed class DemoVolumeTests(DemoVolumeTests.Made made) : IClassFixture<DemoVolumeTests.Made>
{
    [Fact]
    public void TheVolumeMode_Makes2000Records_EachReadWholeByTheAdmin()
    {
        using var admin = made.SignIn(DemoKiler.Admin);

        var set = made.Read(admin);

        Assert.Equal(2000, set.Records.Count);
        Assert.Equal(DemoRecords.Stashes.Count + DemoRecords.VolumeSystems, set.Stashes.Count);
        Assert.All(set.Stashes.Values, stash => Assert.True(stash.IsValid, stash.Invalid));

        // Every record fits its template, and the admin, who holds every level, reads each of its fields: no padlock, and
        // nothing that does not decrypt.
        Assert.All(set.Records.Values, view => Assert.True(
            view.Template is not null && view.Fields.All(field => field.Kind is FieldKind.Value or FieldKind.Empty or FieldKind.Secret),
            view.File.Model?.Name));
        var files = set.Records.Values.Select(view => view.File.Model!).ToList();
        Assert.Equal(["db", "link", "note", "sql"], files.Select(file => file.Template).Distinct().Order(StringComparer.Ordinal));
        Assert.Equal([1, 2, 3], files.SelectMany(file => file.Protected.Keys).Distinct().Order());
    }

    /// <summary>The search, the tree and the facets over the 2000 records (M5.6, design 11): a few milliseconds, without an index of words.</summary>
    [Fact]
    public void TheSearch_Over2000Records_TakesAFewMilliseconds()
    {
        using var admin = made.SignIn(DemoKiler.Admin);
        var set = made.Read(admin);

        // The first search of a set makes the search text of each record.
        var first = Stopwatch.StartNew();
        var all = RecordSearch.Find(set, new RecordQuery());
        first.Stop();
        Assert.Equal(2000, all.Records.Count);
        Assert.Equal(2000, all.Tree.Sum(node => node.Count));

        // As the page asks while the user types: two words, in another case, and a filter.
        const int Rounds = 20;
        var query = new RecordQuery { Text = "DATABASE prod", Fields = new Dictionary<string, string> { ["env"] = "prod" } };
        var searches = Stopwatch.StartNew();
        var found = RecordSearch.Find(set, query);
        for (var round = 1; round < Rounds; round++)
        {
            RecordSearch.Find(set, query);
        }

        searches.Stop();
        Assert.NotEmpty(found.Records);
        Assert.All(found.Records, view => Assert.Equal("prod", view.Fields[0].Value?.Text));
        var each = searches.Elapsed / Rounds;
        TestContext.Current.TestOutputHelper?.WriteLine(
            string.Create(CultureInfo.InvariantCulture, $"The first search: {first.Elapsed.TotalMilliseconds:0.0} ms; each search after it: {each.TotalMilliseconds:0.00} ms, {found.Records.Count} records."));

        // Bounds for a slow CI machine: the design measures under a millisecond a search.
        Assert.True(first.Elapsed < TimeSpan.FromSeconds(1), $"The first search took {first.Elapsed.TotalMilliseconds} ms.");
        Assert.True(each < TimeSpan.FromMilliseconds(50), $"A search took {each.TotalMilliseconds} ms.");
    }

    public sealed class Made() : DemoFolder(volume: true);
}

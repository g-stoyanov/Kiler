using Kiler.Core;

namespace Kiler.DemoData.Tests;

/// <summary>
/// The volume mode of the demo (M5.5): <see cref="DemoRecords.VolumeCount"/> records in all, for the timing of the search
/// (M5.6). The data folder is made once for the class, as its writes take some 30 s.
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

    public sealed class Made() : DemoFolder(volume: true);
}

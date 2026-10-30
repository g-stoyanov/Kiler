namespace Kiler.DemoData.Tests;

/// <summary>
/// The place of the demo data (M4.10): the data folder and its local copy are made only where neither is, and
/// <c>--replace</c> deletes both, but only a data folder on a local disk that holds nothing else, and nothing while a Kiler
/// holds the copy. Each test works in a temporary folder, deleted after it.
/// </summary>
public sealed class DemoTargetTests : IDisposable
{
    private readonly string _folder = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "Kiler.Tests", Guid.NewGuid().ToString("N"))).FullName;
    private readonly DemoTarget _target;

    public DemoTargetTests() => _target = DemoTarget.For(Path.Combine(_folder, "data"), Path.Combine(_folder, "local"));

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void CopyKey_IsTheKeyOfTheLocalCopyThatKilerTakes()
    {
        // InstancePlaceTests.Key_IsTheValueThatTheDemoDataTakes holds InstancePlace.Key of Kiler.App to the same value.
        Assert.Equal("9f4636314a0bdf9a", DemoTarget.CopyKey(@"D:\Demo\data"));
        Assert.Equal(Path.Combine(@"C:\Local\Kiler", "copies", "9f4636314a0bdf9a"), DemoTarget.For(@"d:\demo\DATA\", @"C:\Local\Kiler").CopyFolder);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Prepare_WithNeitherFolder_IsReady(bool replace)
    {
        Assert.Null(_target.Prepare(replace));
    }

    [Fact]
    public void Prepare_WithoutReplace_RefusesADataFolderThatIsThere_AndDeletesNothing()
    {
        MakeDataFolder();

        Assert.Contains("--replace", _target.Prepare(replace: false), StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(_target.Folder, "levels.json")));
    }

    [Fact]
    public void Prepare_WithoutReplace_RefusesALocalCopyThatIsThere_AndDeletesNothing()
    {
        MakeCopy();

        Assert.Contains("--replace", _target.Prepare(replace: false), StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(_target.CopyFolder, "index.json")));
    }

    [Fact]
    public void Prepare_WithReplace_DeletesTheDataFolderAndItsLocalCopy()
    {
        MakeDataFolder();
        MakeCopy();

        Assert.Null(_target.Prepare(replace: true));
        Assert.False(Path.Exists(_target.Folder));
        Assert.False(Path.Exists(_target.CopyFolder));
    }

    [Fact]
    public void Prepare_WithReplace_WhileAKilerHoldsTheLocalCopy_RefusesAndDeletesNothing()
    {
        MakeDataFolder();
        MakeCopy();
        using var held = new FileStream(Path.Combine(_target.CopyFolder, "lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

        Assert.Contains("stop it first", _target.Prepare(replace: true), StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(_target.Folder, "levels.json")));
        Assert.True(File.Exists(Path.Combine(_target.CopyFolder, "index.json")));
    }

    [Fact]
    public void Prepare_WithReplace_RefusesAFolderThatHoldsMoreThanADataFolder_AndDeletesNothing()
    {
        MakeDataFolder();
        File.WriteAllText(Path.Combine(_target.Folder, "notes.txt"), "made up");

        Assert.Contains("holds more than a data folder", _target.Prepare(replace: true), StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(_target.Folder, "levels.json")));
    }

    [Fact]
    public void Prepare_WithReplace_RefusesAFileAtTheName()
    {
        File.WriteAllText(_target.Folder, "made up");

        Assert.Contains("is not a folder", _target.Prepare(replace: true), StringComparison.Ordinal);
        Assert.True(File.Exists(_target.Folder));
    }

    [Fact]
    public void Prepare_WithReplace_RefusesAPathOnANetwork_WithoutLookingAtIt()
    {
        // The name of a server that cannot exist (.invalid, RFC 2606): a look at it would wait for the network.
        var target = DemoTarget.For(@"\\demo-server.invalid\share\data", Path.Combine(_folder, "local"));

        Assert.Contains("only on a local disk", target.Prepare(replace: true), StringComparison.Ordinal);
    }

    /// <summary>A data folder as Kiler leaves it at its top, with made-up content.</summary>
    private void MakeDataFolder()
    {
        foreach (var folder in (string[])["templates", "stashes", "users", "records", "history", "trash", "audit", "private"])
        {
            Directory.CreateDirectory(Path.Combine(_target.Folder, folder));
        }

        File.WriteAllText(Path.Combine(_target.Folder, "levels.json"), "{}");
        File.WriteAllText(Path.Combine(_target.Folder, "settings.json"), "{}");
        File.WriteAllText(Path.Combine(_target.Folder, "users", "demo.user.json"), "{}");
    }

    /// <summary>A local copy of the data folder, with made-up content.</summary>
    private void MakeCopy()
    {
        Directory.CreateDirectory(_target.CopyFolder);
        File.WriteAllText(Path.Combine(_target.CopyFolder, "index.json"), "{}");
    }
}

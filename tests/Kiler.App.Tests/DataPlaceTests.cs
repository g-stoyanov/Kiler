namespace Kiler.App.Tests;

public sealed class DataPlaceTests : IDisposable
{
    private const string Exe = @"\\server\share\Kiler\Kiler.exe";
    private const string Local = @"C:\Users\demo.user\AppData\Local\Kiler";

    private readonly TempFolder _folder = new();
    private readonly ShareFile _share;

    public DataPlaceTests()
    {
        _share = new ShareFile(Path.Combine(_folder.Path, "Kiler", "share.json"));
        _share.Write(@"\\old-server\share\Kiler");
    }

    public void Dispose() => _folder.Dispose();

    [Fact]
    public void Choose_TheDataFolderFromTheCommandLine_WinsOverTheSharedFolder()
    {
        Assert.Equal(new DataPlace(@"D:\Demo\data", Local), DataPlace.Choose(@"D:\Demo\data", new LocalCopyDecision.RunHere(), _share, Local));
        Assert.Equal(@"D:\Demo\data", DataPlace.ChooseFolder(@"D:\Demo\data", new LocalCopyDecision.StartCopy(Exe), _share));
    }

    [Fact]
    public void Choose_StartedFromASharedFolder_TakesTheDataNextToTheExe_NotTheFolderInShareJson()
    {
        Assert.Equal(@"\\server\share\Kiler\data", DataPlace.ChooseFolder(null, new LocalCopyDecision.CopyAndStart(Exe), _share));
        Assert.Equal(@"\\server\share\Kiler\data", DataPlace.ChooseFolder(null, new LocalCopyDecision.StartCopy(Exe), _share));
    }

    [Fact]
    public void Choose_StartedLocally_TakesTheDataInTheFolderFromShareJson()
    {
        Assert.Equal(new DataPlace(@"\\old-server\share\Kiler\data", Local), DataPlace.Choose(null, new LocalCopyDecision.RunHere(), _share, Local));
    }

    [Fact]
    public void Choose_StartedLocallyWithoutShareJson_IsNull()
    {
        File.Delete(_share.Path);

        Assert.Null(DataPlace.Choose(null, new LocalCopyDecision.RunHere(), _share, Local));
    }

    [Fact]
    public void For_ARelativeFolder_TakesItsFullPath()
    {
        Assert.Equal(Path.GetFullPath("data"), DataPlace.For("data", Local).DataFolder);
    }

    [Fact]
    public void CopyFolder_IsInCopies_UnderTheKeyOfTheInstanceOfTheDataFolder()
    {
        var place = DataPlace.For(@"\\server\share\Kiler\data", Local);
        var key = InstancePlace.Key(@"\\server\share\Kiler\data");

        Assert.Equal(Path.Combine(Local, "copies"), place.CopiesFolder);
        Assert.Equal(Path.Combine(Local, "copies", key), place.CopyFolder);
        Assert.EndsWith($"-{key}.json", InstancePlace.For(place.DataFolder, Local, sessionId: 1).FilePath, StringComparison.Ordinal);
    }

    [Fact]
    public void CopyFolder_IsTheSameForAnotherCaseOrATrailingSeparator_AndAnotherForAnotherFolder()
    {
        var copy = DataPlace.For(@"\\server\share\Kiler\data", Local).CopyFolder;

        Assert.Equal(copy, DataPlace.For(@"\\SERVER\Share\Kiler\Data\", Local).CopyFolder);
        Assert.NotEqual(copy, DataPlace.For(@"\\server\share\Other\data", Local).CopyFolder);
    }
}

namespace Kiler.App.Tests;

public sealed class DiagTargetTests : IDisposable
{
    private const string Exe = @"\\server\share\Kiler\Kiler.exe";

    private readonly TempFolder _folder = new();
    private readonly ShareFile _share;

    public DiagTargetTests()
    {
        _share = new ShareFile(Path.Combine(_folder.Path, "Kiler", "share.json"));
        _share.Write(@"\\old-server\share\Kiler");
    }

    public void Dispose() => _folder.Dispose();

    [Fact]
    public void Choose_TheFolderAfterDiag_WinsOverTheDataFolderAndTheSharedFolder()
    {
        var options = new StartOptions { Mode = StartMode.Diag, DiagFolder = @"D:\Measure", DataFolder = @"D:\Demo\data" };

        Assert.Equal(@"D:\Measure", DiagTarget.Choose(options, new LocalCopyDecision.StartCopy(Exe), _share));
    }

    [Fact]
    public void Choose_NoFolderAfterDiag_TakesTheDataFolder()
    {
        var options = new StartOptions { Mode = StartMode.Diag, DataFolder = @"D:\Demo\data" };

        Assert.Equal(@"D:\Demo\data", DiagTarget.Choose(options, new LocalCopyDecision.StartCopy(Exe), _share));
    }

    [Fact]
    public void Choose_StartedFromASharedFolder_TakesTheDataNextToTheExe_NotTheFolderInShareJson()
    {
        var options = new StartOptions { Mode = StartMode.Diag };

        Assert.Equal(@"\\server\share\Kiler\data", DiagTarget.Choose(options, new LocalCopyDecision.CopyAndStart(Exe), _share));
        Assert.Equal(@"\\server\share\Kiler\data", DiagTarget.Choose(options, new LocalCopyDecision.StartCopy(Exe), _share));
    }

    [Fact]
    public void Choose_StartedLocally_TakesTheDataInTheFolderFromShareJson()
    {
        var options = new StartOptions { Mode = StartMode.Diag };

        Assert.Equal(@"\\old-server\share\Kiler\data", DiagTarget.Choose(options, new LocalCopyDecision.RunHere(), _share));
    }

    [Fact]
    public void Choose_StartedLocallyWithoutShareJson_IsNull()
    {
        var options = new StartOptions { Mode = StartMode.Diag };
        File.Delete(_share.Path);

        Assert.Null(DiagTarget.Choose(options, new LocalCopyDecision.RunHere(), _share));
    }
}

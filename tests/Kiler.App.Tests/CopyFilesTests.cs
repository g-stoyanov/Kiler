namespace Kiler.App.Tests;

public sealed class CopyFilesTests : IDisposable
{
    private readonly TempFolder _folder = new();
    private readonly string _source;
    private readonly string _target;

    public CopyFilesTests()
    {
        _source = Path.Combine(_folder.Path, "share", "Kiler.exe");
        _target = Path.Combine(_folder.Path, "Programs", "Kiler", "Kiler.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(_source)!);
        File.WriteAllBytes(_source, [1, 2, 3, 4]);
    }

    public void Dispose() => _folder.Dispose();

    [Fact]
    public void CopyNew_NoTarget_MakesTheFolderCopiesTheBytesAndLeavesNoOtherFile()
    {
        Assert.True(new CopyFiles().CopyNew(_source, _target));

        Assert.Equal([1, 2, 3, 4], File.ReadAllBytes(_target));
        Assert.Equal([_target], Directory.GetFiles(Path.GetDirectoryName(_target)!));
    }

    [Fact]
    public void CopyNew_TheTargetExists_LeavesItAsItIs()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_target)!);
        File.WriteAllBytes(_target, [9]);

        Assert.False(new CopyFiles().CopyNew(_source, _target));

        Assert.Equal([9], File.ReadAllBytes(_target));
        Assert.Equal([_target], Directory.GetFiles(Path.GetDirectoryName(_target)!));
    }

    [Fact]
    public void CopyNew_TheSourceIsMissing_ThrowsAndLeavesNoFile()
    {
        File.Delete(_source);

        Assert.ThrowsAny<IOException>(() => new CopyFiles().CopyNew(_source, _target));

        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(_target)!));
    }

    [Theory]
    [InlineData(@"\\server\share\Kiler\Kiler.exe")]
    [InlineData("//server/share/Kiler/Kiler.exe")]
    [InlineData(@"\\?\UNC\server\share\Kiler\Kiler.exe")]
    [InlineData(@"\\.\UNC\server\share\Kiler\Kiler.exe")]
    public void IsNetworkPath_AUncPath_IsTrue(string path)
    {
        Assert.True(CopyFiles.IsNetworkPath(path));
    }

    [Fact]
    public void IsNetworkPath_TheSystemDrive_IsFalse()
    {
        var exe = Path.Combine(Environment.SystemDirectory, "Kiler.exe");

        Assert.False(CopyFiles.IsNetworkPath(exe));
        Assert.False(CopyFiles.IsNetworkPath(@"\\?\" + exe));
    }

    [Theory]
    [InlineData("Kiler.exe")]
    [InlineData(@"\\?\Volume{00000000-0000-0000-0000-000000000000}\Kiler.exe")]
    public void IsNetworkPath_NoDriveLetterAndNoServer_IsFalse(string path)
    {
        Assert.False(CopyFiles.IsNetworkPath(path));
    }
}

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

    [Fact]
    public void OpenShared_WhileOpen_OthersCanReadAndRenameButNotWriteIt()
    {
        using (var stream = new CopyFiles().OpenShared(_source))
        {
            Assert.ThrowsAny<IOException>(() => OpenToWrite(_source).Dispose());
            Assert.Equal([1, 2, 3, 4], File.ReadAllBytes(_source));
            File.Move(_source, _source + ".renamed");

            Assert.Equal(1, stream.ReadByte());
        }

        File.Delete(_source + ".renamed");
    }

    [Fact]
    public void OpenShared_AFileThatIsNotThere_ThrowsFileNotFound()
    {
        File.Delete(_source);

        Assert.Throws<FileNotFoundException>(() => new CopyFiles().OpenShared(_source));
    }

    [Fact]
    public void CreateUpdate_WhileOpen_OthersCanReadAndRenameButNotWriteIt()
    {
        var update = _target + ".new";
        Directory.CreateDirectory(Path.GetDirectoryName(update)!);
        using (var stream = new CopyFiles().CreateUpdate(update))
        {
            stream.Write([5, 6, 7]);
            stream.Flush();

            Assert.ThrowsAny<IOException>(() => OpenToWrite(update).Dispose());
            Assert.ThrowsAny<IOException>(() => new CopyFiles().CreateUpdate(update).Dispose());

            // A reader lets a writer be, as this stream writes.
            Assert.Equal([5, 6, 7], ReadWhileWritten(update));
            File.Move(update, _target);

            stream.Position = 0;
            Assert.Equal(5, stream.ReadByte());
        }

        Assert.Equal([5, 6, 7], File.ReadAllBytes(_target));
    }

    [Fact]
    public void CreateUpdate_AFileOfThatNameIsThere_ThrowsAndLeavesIt()
    {
        var update = _target + ".new";
        Directory.CreateDirectory(Path.GetDirectoryName(update)!);
        File.WriteAllBytes(update, [9, 9, 9, 9, 9]);

        Assert.ThrowsAny<IOException>(() => new CopyFiles().CreateUpdate(update).Dispose());

        Assert.Equal([9, 9, 9, 9, 9], File.ReadAllBytes(update));
    }

    [Fact]
    public void TryDelete_AFileThatThisStreamHolds_DeletesItsName()
    {
        var update = _target + ".new";
        Directory.CreateDirectory(Path.GetDirectoryName(update)!);
        using (var stream = new CopyFiles().CreateUpdate(update))
        {
            stream.Write([5]);

            Assert.True(new CopyFiles().TryDelete(update));
        }

        Assert.False(File.Exists(update));
    }

    [Fact]
    public void TryDelete_AFileThatIsThere_DeletesIt()
    {
        Assert.True(new CopyFiles().TryDelete(_source));

        Assert.False(File.Exists(_source));
    }

    [Fact]
    public void TryDelete_AFileThatIsNotThere_IsTrue()
    {
        Assert.True(new CopyFiles().TryDelete(_source + ".new"));
    }

    [Fact]
    public void TryDelete_AFileOpenWithoutDeleteSharing_IsFalseAndLeavesIt()
    {
        using (File.Open(_source, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.False(new CopyFiles().TryDelete(_source));
        }

        Assert.True(File.Exists(_source));
    }

    /// <summary>
    /// Opens a file to write, sharing everything, so only the share mode of a stream that holds it can refuse it.
    /// <see cref="File.OpenWrite"/> shares nothing, so it is refused by any stream, whatever its share mode.
    /// </summary>
    internal static FileStream OpenToWrite(string path) =>
        new(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);

    /// <summary>The bytes of a file that a stream with write access holds: <see cref="File.ReadAllBytes"/> does not share the write.</summary>
    internal static byte[] ReadWhileWritten(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var bytes = new MemoryStream();
        stream.CopyTo(bytes);
        return bytes.ToArray();
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

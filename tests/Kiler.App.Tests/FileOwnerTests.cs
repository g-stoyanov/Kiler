using System.Security.AccessControl;
using System.Security.Principal;
using Kiler.Storage;

namespace Kiler.App.Tests;

/// <summary>
/// The owner that Windows records for a file or a folder (design 7.8): by path for <c>--diag</c> (M1.11), and through a
/// handle for the data layer (M2.6). On the local disk it is the owner that the process gives new files, which is the user
/// unless the process runs as administrator (<see cref="FileDiagTests.OwnerIsTheUser"/>). The tests run in the folder that
/// <see cref="TempFolder.Variable"/> names when it is set, so they show what a file server gives (M2.11).
/// </summary>
public sealed class FileOwnerTests : IDisposable
{
    private readonly TempFolder _folder = TempFolder.OnTheTestDisk();

    public void Dispose() => _folder.Dispose();

    [Fact]
    public void Of_ANewFileAndFolder_IsTheOwnerThatTheProcessGivesThem()
    {
        var file = Path.Combine(_folder.Path, "new.json");
        File.WriteAllText(file, "{}");
        var folder = Directory.CreateDirectory(Path.Combine(_folder.Path, "new")).FullName;
        using var identity = WindowsIdentity.GetCurrent();

        Assert.Equal(identity.Owner, FileOwner.Of(new FileInfo(file)));
        Assert.Equal(identity.Owner, FileOwner.Of(new DirectoryInfo(folder)));
    }

    [Fact]
    public void Of_AFileThatIsNotThere_Throws()
    {
        Assert.ThrowsAny<IOException>(() => FileOwner.Of(new FileInfo(Path.Combine(_folder.Path, "missing.json"))));
    }

    [Fact]
    public void Of_AHandleToANewFile_IsTheOwnerThatTheProcessGivesIt_AlsoAsTheOwnerOfTheDataLayer()
    {
        var file = Path.Combine(_folder.Path, "new.json");
        File.WriteAllText(file, "{}");
        using var identity = WindowsIdentity.GetCurrent();
        using var handle = File.OpenHandle(file);

        Assert.Equal(identity.Owner, FileOwner.Of(handle));
        Assert.Equal(identity.Owner?.Value, ((IFileOwner)FileOwner.Instance).Of(handle));
    }

    [Fact]
    public void Of_AHandleWithoutTheRightToReadTheOwner_IsNothing()
    {
        var file = Path.Combine(_folder.Path, "new.json");
        File.WriteAllText(file, "{}");

        // The right to read the data, without READ_CONTROL, which the owner needs.
        using var stream = new FileInfo(file).Create(FileMode.Open, FileSystemRights.ReadData, FileShare.ReadWrite, 4096, FileOptions.None, null);

        Assert.Null(FileOwner.Of(stream.SafeFileHandle));
        Assert.Null(((IFileOwner)FileOwner.Instance).Of(stream.SafeFileHandle));
    }

    [Fact]
    public void CreatePrivate_WithTheOwnersOfWindows_MakesThePrivateFolderOfTheUser()
    {
        // The owner of a folder is read through the handles that make and hold it (M2.6), so they must be able to read it.
        var data = DataFolder.Create(Path.Combine(_folder.Path, "data"), FileOwner.Instance);
        using var identity = WindowsIdentity.GetCurrent();

        var mine = data.CreatePrivate("demo.user");

        Assert.True(mine.IsPrivate);
        Assert.True(Directory.Exists(Path.Combine(mine.Root, DataFolder.Records)));
        Assert.Equal(identity.Owner, FileOwner.Of(new DirectoryInfo(mine.Root)));
    }
}

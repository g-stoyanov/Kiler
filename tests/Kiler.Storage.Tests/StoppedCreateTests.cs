using System.Text.Json.Nodes;
using Microsoft.Extensions.Time.Testing;

namespace Kiler.Storage.Tests;

/// <summary>
/// The creates of a private folder that stopped (M2.7e): <c>private\&lt;account&gt;.&lt;8 hex digits&gt;.tmp\</c>, which a
/// create leaves when it stops before the rename. It is never taken for a private folder. The Kiler of the account deletes an
/// old one that holds nothing but empty folders of the owner of its private folder, at a load at most once in 10 minutes,
/// and leaves and reports any other (decided by the user).
/// </summary>
public sealed class StoppedCreateTests : IDisposable
{
    private const string Me = "demo.user";
    private const string Other = "demo.other";

    private static readonly DocumentKey Record = DocumentKey.InFolder(DataFolder.Records, "8f3a1c");

    /// <summary>The folders that a create makes in the temporary folder, in the order it makes them.</summary>
    private static readonly string[] Folders = [DataFolder.Records, DataFolder.History, DataFolder.Trash];

    /// <summary>Past the age from which a stopped create is old, with a margin for the time the test takes.</summary>
    private static readonly TimeSpan Old = DataFolder.TemporaryFileAge + TimeSpan.FromMinutes(1);

    private readonly TestFolder _test = new();
    private readonly TestFileSystem _files = new();
    private readonly FakeTimeProvider _clock = new(DateTimeOffset.UtcNow);
    private readonly DataFolder _share;
    private readonly string _copy;
    private readonly string _private;

    public StoppedCreateTests()
    {
        _share = DataFolder.Create(Path.Combine(_test.Root, "data"), _files);
        _copy = Directory.CreateDirectory(Path.Combine(_test.Root, "copy")).FullName;
        _private = Path.Combine(_share.Root, DataFolder.Private);
    }

    /// <summary>What someone puts in a stopped create, besides its empty folders.</summary>
    public enum More
    {
        FileInAFolder,
        HiddenFileInAFolder,
        FileAtItsRoot,
        AnotherFolder,
        FolderInAFolder,
    }

    public void Dispose() => _test.Dispose();

    [Fact]
    public void Load_AStoppedCreate_IsNeverTakenForAPrivateFolder()
    {
        var stopped = StopACreate(Me);
        var planted = Path.Combine(stopped, DataFolder.Records, "8f3a1c.json");
        TestFolder.WriteAsKiler(planted, DocumentFormat.New(Record, Content("planted")));
        var copy = OpenAt(Me);
        copy.CreatePrivate();
        var name = Path.GetFileName(stopped);

        Assert.Throws<ArgumentException>(() => copy.CopyOthers([name]));
        Assert.Throws<ArgumentException>(() => DataCopy.Open(_share, _copy, _clock, name));
        _clock.Advance(Old);
        var load = copy.Load();

        Assert.Empty(load.Changed);
        Assert.Empty(copy.Documents());
        Assert.Equal([Reported(stopped)], load.Skipped);
        Assert.True(File.Exists(planted), "The document in the stopped create is gone.");
        Assert.Empty(Directory.GetFileSystemEntries(_copy, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public void Load_AnOldStoppedCreateOfTheCurrentUser_IsDeleted_AndAYoungOneIsNot()
    {
        var stopped = StopACreate(Me);
        var copy = OpenAt(Me);
        copy.CreatePrivate();

        var young = copy.Load();

        Assert.True(Directory.Exists(stopped), "A young stopped create was deleted.");
        Assert.Empty(young.Skipped);
        _clock.Advance(Old);

        var load = copy.Load();

        Assert.False(Directory.Exists(stopped), "The old stopped create is still there.");
        Assert.Empty(load.Skipped);
        Assert.Equal([Me], Directory.GetDirectories(_private).Select(Path.GetFileName));
        Assert.Equal([DataFolder.History, DataFolder.Records, DataFolder.Trash], Directory.GetDirectories(Path.Combine(_private, Me)).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    /// <summary>A create that stopped while it made the folders of the temporary folder leaves the first of them.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Load_AnOldStoppedCreateWithOnlySomeOfItsFolders_IsDeleted(int made)
    {
        var stopped = StopACreate(Me);
        foreach (var folder in Folders[made..])
        {
            Directory.Delete(Path.Combine(stopped, folder));
        }

        var copy = OpenAt(Me);
        copy.CreatePrivate();
        _clock.Advance(Old);

        var load = copy.Load();

        Assert.False(Directory.Exists(stopped), "The old stopped create is still there.");
        Assert.Empty(load.Skipped);
    }

    [Theory]
    [InlineData(More.FileInAFolder)]
    [InlineData(More.HiddenFileInAFolder)]
    [InlineData(More.FileAtItsRoot)]
    [InlineData(More.AnotherFolder)]
    [InlineData(More.FolderInAFolder)]
    public void Load_AnOldStoppedCreateWithMoreInIt_IsLeftAsItIs_AndReported(More more)
    {
        var stopped = StopACreate(Me);
        var copy = OpenAt(Me);
        copy.CreatePrivate();
        var added = more switch
        {
            More.FileInAFolder or More.HiddenFileInAFolder => Path.Combine(stopped, DataFolder.Trash, "notes.txt"),
            More.FileAtItsRoot => Path.Combine(stopped, "key.json"),
            More.AnotherFolder => Path.Combine(stopped, DataFolder.Stashes),
            _ => Path.Combine(stopped, DataFolder.Trash, DataFolder.Records),
        };
        if (more is More.AnotherFolder or More.FolderInAFolder)
        {
            Directory.CreateDirectory(added);
        }
        else
        {
            File.WriteAllText(added, "made up");
            if (more == More.HiddenFileInAFolder)
            {
                File.SetAttributes(added, FileAttributes.Hidden | FileAttributes.System);
            }
        }

        _clock.Advance(Old);

        var load = copy.Load();

        Assert.Equal([Reported(stopped)], load.Skipped);
        Assert.True(Path.Exists(added), "What was put in the stopped create is gone.");
        AssertAllFoldersThere(stopped);
    }

    /// <summary>A junction in place of a folder of the stopped create, to a folder with a file or to an empty one, is never followed.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Load_AnOldStoppedCreateWithAJunctionInIt_DoesNotFollowIt_AndIsLeft_AndReported(bool withAFile)
    {
        var stopped = StopACreate(Me);
        var copy = OpenAt(Me);
        copy.CreatePrivate();
        var history = Path.Combine(stopped, DataFolder.History);
        TestFolder.SkipWithoutJunctions(history);
        var elsewhere = Directory.CreateDirectory(Path.Combine(_test.Root, "elsewhere")).FullName;
        var file = Path.Combine(elsewhere, "notes.txt");
        if (withAFile)
        {
            File.WriteAllText(file, "made up");
        }

        Directory.Delete(history);
        TestFolder.CreateJunction(history, elsewhere);
        _clock.Advance(Old);

        var load = copy.Load();

        Assert.Equal([Reported(stopped)], load.Skipped);
        Assert.True(File.GetAttributes(history).HasFlag(FileAttributes.ReparsePoint), "The junction is gone.");
        Assert.True(Directory.Exists(elsewhere), "The folder that the junction names is gone.");
        Assert.Equal(withAFile, File.Exists(file));
        Assert.True(Directory.Exists(Path.Combine(stopped, DataFolder.Records)), "A folder of the stopped create is gone.");
        Assert.True(Directory.Exists(Path.Combine(stopped, DataFolder.Trash)), "A folder of the stopped create is gone.");
    }

    [Fact]
    public void Load_AnOldStoppedCreateWithASymbolicLinkInIt_DoesNotFollowIt_AndIsLeft_AndReported()
    {
        var stopped = StopACreate(Me);
        var copy = OpenAt(Me);
        copy.CreatePrivate();
        var target = Path.Combine(_test.Root, "notes.txt");
        File.WriteAllText(target, "made up");
        TestFolder.CreateFileLink(Path.Combine(stopped, DataFolder.Trash, "notes.txt"), target);
        _clock.Advance(Old);

        var load = copy.Load();

        Assert.Equal([Reported(stopped)], load.Skipped);
        Assert.Equal("made up", File.ReadAllText(target));
        AssertAllFoldersThere(stopped);
    }

    /// <summary>
    /// A junction at the name of a stopped create, to a folder of empty folders as a create leaves, is never opened: what it
    /// names stays, and so does the junction. Nor is a file at such a name.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Load_AJunctionOrAFileAtTheNameOfAStoppedCreate_IsNeverOpened_AndIsLeft_AndReported(bool junction)
    {
        var copy = OpenAt(Me);
        copy.CreatePrivate();
        var stopped = Path.Combine(_private, Me + ".0123abcd.tmp");
        var elsewhere = Directory.CreateDirectory(Path.Combine(_test.Root, "elsewhere")).FullName;
        foreach (var folder in Folders)
        {
            Directory.CreateDirectory(Path.Combine(elsewhere, folder));
        }

        if (junction)
        {
            TestFolder.CreateJunction(stopped, elsewhere);
        }
        else
        {
            File.WriteAllText(stopped, "made up");
        }

        var opened = new List<string>();
        _files.Before = (step, path) =>
        {
            if (path.StartsWith(stopped, StringComparison.OrdinalIgnoreCase))
            {
                opened.Add(step);
            }
        };
        _clock.Advance(Old);

        var load = copy.Load();

        Assert.Equal([Reported(stopped)], load.Skipped);
        Assert.Empty(opened);
        Assert.Equal(junction, File.GetAttributes(stopped).HasFlag(FileAttributes.ReparsePoint));
        AssertAllFoldersThere(elsewhere);
    }

    /// <summary>A stopped create whose times are after the year 9999 is never taken for old (M2.7 note).</summary>
    [Fact]
    public void Load_AStoppedCreateWithATimeAfterTheYear9999_IsNeverTakenForOld_AndIsNotReported()
    {
        var stopped = StopACreate(Me);
        var copy = OpenAt(Me);
        copy.CreatePrivate();
        TestFolder.SetRawTimes(stopped, TestFolder.FarFuture, TestFolder.FarFuture);
        _clock.Advance(Old);

        var load = copy.Load();

        Assert.Empty(load.Skipped);
        AssertAllFoldersThere(stopped);
    }

    /// <summary>
    /// An owner that cannot be read is never the owner to compare with, also when the copy knows no private folder: in C#,
    /// <c>null == null</c> is true.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Load_AStoppedCreateWhoseOwnerCannotBeRead_IsLeft_AndReported(bool knowsAPrivateFolder)
    {
        var stopped = StopACreate(Me);
        var copy = OpenAt(Me);
        if (knowsAPrivateFolder)
        {
            copy.CreatePrivate();
        }

        foreach (var folder in (string[])[stopped, .. Directory.GetDirectories(stopped)])
        {
            _files.Owners.Set(folder, null);
        }

        _clock.Advance(Old);

        var load = copy.Load();

        Assert.Equal([Reported(stopped)], load.Skipped);
        AssertAllFoldersThere(stopped);
    }

    [Theory]
    [InlineData("")]
    [InlineData(DataFolder.Records)]
    [InlineData(DataFolder.History)]
    [InlineData(DataFolder.Trash)]
    public void Load_AnOldStoppedCreateWithAnotherOwner_IsLeft_AndReported(string folder)
    {
        var stopped = StopACreate(Me);
        var copy = OpenAt(Me);
        copy.CreatePrivate();
        _files.Owners.Set(Path.Combine(stopped, folder), TestOwners.Other);
        _clock.Advance(Old);

        var load = copy.Load();

        Assert.Equal([Reported(stopped)], load.Skipped);
        AssertAllFoldersThere(stopped);
    }

    /// <summary>The owner to compare with is that of the private folder the copy knows, so a stopped create waits for one.</summary>
    [Fact]
    public void Load_AnOldStoppedCreate_WhileTheCopyKnowsNoPrivateFolderOfTheUser_IsLeft_AndReported_UntilItKnowsOne()
    {
        var stopped = StopACreate(Me);
        var copy = OpenAt(Me);
        _clock.Advance(Old);

        var load = copy.Load();

        Assert.Equal([Reported(stopped)], load.Skipped);
        AssertAllFoldersThere(stopped);
        copy.CreatePrivate();
        _clock.Advance(DataFolder.TemporaryFileAge);

        var after = copy.Load();

        Assert.False(Directory.Exists(stopped), "The old stopped create is still there.");
        Assert.Empty(after.Skipped);
    }

    [Fact]
    public void Load_TheStoppedCreatesOfOtherAccounts_AndOtherNames_AreLeft_AndNotReported()
    {
        var copy = OpenAt(Me);
        copy.CreatePrivate();
        copy.CopyOthers([Other]);
        string[] left =
        [
            StopACreate(Other),
            StopACreate(Me + ".x"),
            StopACreate("demo"),
            .. new[] { Me + ".tmp", Me + ".0123ABCD.tmp", Me + ".0123abc.tmp", Me + ".0123abcd.tmp.old", Me + ".0123abcd" }
                .Select(name => Directory.CreateDirectory(Path.Combine(_private, name)).FullName),
        ];
        _clock.Advance(Old);

        var load = copy.Load();

        Assert.Empty(load.Skipped);
        foreach (var folder in left)
        {
            Assert.True(Directory.Exists(folder), "A folder that is not a stopped create of the user is gone.");
        }
    }

    /// <summary>
    /// A <c>private\</c> of more entries than a listing of the copy takes (<c>CopyLimits.Listed</c>) is not looked through; one
    /// of as many is.
    /// </summary>
    [Theory]
    [InlineData(6, true)]
    [InlineData(7, false)]
    public void Load_APrivateFolderOfTooManyEntries_DeletesNoStoppedCreate_AndReportsNone(int others, bool deleted)
    {
        var stopped = StopACreate(Me);

        // The data folder lists its 8 folders, so 8 entries keep its listing whole; private\ has the user's folder, the
        // stopped create and the others.
        var copy = DataCopy.Open(_share, _copy, _clock, CopyLimits.Default with { Listed = 8 }, Me);
        copy.CreatePrivate();
        for (var i = 0; i < others; i++)
        {
            Directory.CreateDirectory(Path.Combine(_private, $"demo.user{i}"));
        }

        _clock.Advance(Old);

        var load = copy.Load();

        Assert.Equal(deleted, !Directory.Exists(stopped));
        Assert.DoesNotContain(load.Skipped, entry => entry.Reason == SkipReason.StoppedCreate);
    }

    [Fact]
    public void Load_LooksForTheStoppedCreatesAtMostOnceIn10Minutes_AndOnlyWithAnAccount()
    {
        var copy = OpenAt(Me);
        copy.CreatePrivate();
        var listings = 0;
        _files.Before = (step, path) => listings += step == nameof(IFileSystem.List) && path == _private ? 1 : 0;

        copy.Load();
        _clock.Advance(DataFolder.TemporaryFileAge - TimeSpan.FromSeconds(1));
        copy.Load();

        Assert.Equal(1, listings);
        _clock.Advance(TimeSpan.FromSeconds(1));
        copy.Load();
        Assert.Equal(2, listings);
        OpenAt(null).Load();
        Assert.Equal(2, listings);
    }

    /// <summary>The file system refuses to delete a folder that is not empty, so a file put in one after its listing stays.</summary>
    [Fact]
    public void Load_AFilePutInAFolderOfAStoppedCreateRightBeforeItsDelete_StopsTheDelete_AndStays()
    {
        var stopped = StopACreate(Me);
        var copy = OpenAt(Me);
        copy.CreatePrivate();
        var trash = Path.Combine(stopped, DataFolder.Trash);
        var planted = Path.Combine(trash, "notes.txt");
        _files.Before = (step, path) =>
        {
            if (step == nameof(IFileSystem.DeleteHeldFolder) && path == trash)
            {
                _files.Before = null;
                File.WriteAllText(planted, "made up");
            }
        };
        _clock.Advance(Old);

        var load = copy.Load();

        Assert.Equal([Reported(stopped)], load.Skipped);
        Assert.Equal("made up", File.ReadAllText(planted));
    }

    /// <summary>
    /// An empty folder of a stopped create made a mount point in place while it is held, as anyone who can write it can do
    /// (M2.3), gives the listing of another folder: the stopped create is left, and what the mount point names stays.
    /// </summary>
    [Fact]
    public void Load_AFolderOfAStoppedCreateMadeAMountPointWhileItIsChecked_IsNotFollowed_AndTheStoppedCreateIsLeft()
    {
        var stopped = StopACreate(Me);
        var copy = OpenAt(Me);
        copy.CreatePrivate();
        var trash = Path.Combine(stopped, DataFolder.Trash);
        TestFolder.SkipWithoutJunctions(trash);
        var elsewhere = Directory.CreateDirectory(Path.Combine(_test.Root, "elsewhere")).FullName;
        _files.Before = (step, path) =>
        {
            if (step == nameof(IFileSystem.List) && path == trash)
            {
                _files.Before = null;
                TestFolder.MakeMountPoint(trash, elsewhere);
            }
        };
        _clock.Advance(Old);

        var load = copy.Load();

        Assert.Equal([Reported(stopped)], load.Skipped);
        Assert.True(Directory.Exists(elsewhere), "The folder that the mount point names is gone.");
        Assert.True(Directory.Exists(Path.Combine(stopped, DataFolder.Records)), "A folder of the stopped create is gone.");
        Assert.True(Directory.Exists(Path.Combine(stopped, DataFolder.History)), "A folder of the stopped create is gone.");
    }

    /// <summary>
    /// A stopped create of no folders, as one that stopped right after it made the temporary folder, made a mount point in place
    /// while it is checked, would give the folders of another folder, through the link: they stay, and so does the stopped
    /// create.
    /// </summary>
    [Fact]
    public void Load_AnEmptyStoppedCreateMadeAMountPointWhileItIsChecked_IsNotFollowed_AndIsLeft()
    {
        var stopped = StopACreate(Me);
        foreach (var folder in Folders)
        {
            Directory.Delete(Path.Combine(stopped, folder));
        }

        var copy = OpenAt(Me);
        copy.CreatePrivate();
        TestFolder.SkipWithoutJunctions(stopped);
        var elsewhere = Directory.CreateDirectory(Path.Combine(_test.Root, "elsewhere")).FullName;
        foreach (var folder in Folders)
        {
            Directory.CreateDirectory(Path.Combine(elsewhere, folder));
        }

        _files.Before = (step, path) =>
        {
            if (step == nameof(IFileSystem.List) && path == stopped)
            {
                _files.Before = null;
                TestFolder.MakeMountPoint(stopped, elsewhere);
            }
        };
        _clock.Advance(Old);

        var load = copy.Load();

        Assert.Equal([Reported(stopped)], load.Skipped);
        AssertAllFoldersThere(elsewhere);
    }

    /// <summary>
    /// An empty <c>private\</c>, as after the private folder of the user went, made a mount point in place while it is held
    /// would give the listing of another folder: nothing of that folder is deleted.
    /// </summary>
    [Fact]
    public void Load_PrivateMadeAMountPointWhileItIsListed_DeletesNothingThroughIt()
    {
        var copy = OpenAt(Me);
        copy.CreatePrivate();
        Directory.Delete(Path.Combine(_private, Me), recursive: true);
        TestFolder.SkipWithoutJunctions(_private);
        var elsewhere = Directory.CreateDirectory(Path.Combine(_test.Root, "elsewhere")).FullName;
        var stopped = Path.Combine(elsewhere, Me + ".0123abcd.tmp");
        foreach (var folder in Folders)
        {
            Directory.CreateDirectory(Path.Combine(stopped, folder));
        }

        _files.Before = (step, path) =>
        {
            if (step == nameof(IFileSystem.List) && path == _private)
            {
                _files.Before = null;
                TestFolder.MakeMountPoint(_private, elsewhere);
            }
        };
        _clock.Advance(Old);

        copy.Load();

        AssertAllFoldersThere(stopped);
    }

    /// <summary>
    /// <c>private\</c>, emptied and made a mount point in place after its listing, as anyone who can write it can do while it is
    /// held (security review of M2.7e), would give a stopped create of another folder to the open by its path: nothing of it
    /// is deleted.
    /// </summary>
    [Fact]
    public void Load_PrivateMadeAMountPointAfterItsListing_DeletesNothingThroughIt()
    {
        var stopped = StopACreate(Me);
        var copy = OpenAt(Me);
        copy.CreatePrivate();
        TestFolder.SkipWithoutJunctions(_private);
        var elsewhere = Directory.CreateDirectory(Path.Combine(_test.Root, "elsewhere")).FullName;
        var moved = Path.Combine(elsewhere, Path.GetFileName(stopped));
        _files.Before = (step, path) =>
        {
            if (step == nameof(IFileSystem.HoldFolderToDelete) && path == stopped)
            {
                _files.Before = null;
                Directory.Move(stopped, moved);
                Directory.Delete(Path.Combine(_private, Me), recursive: true);
                TestFolder.MakeMountPoint(_private, elsewhere);
            }
        };
        _clock.Advance(Old);

        var load = copy.Load();

        Assert.Equal([Reported(stopped)], load.Skipped);
        AssertAllFoldersThere(moved);
    }

    /// <summary>
    /// A new copy of a user who has a private folder takes it as first seen in the first load (M2.7d), and the same load
    /// deletes the old stopped creates of the user, without waiting for the next look.
    /// </summary>
    [Fact]
    public void Load_TheFirstLoadOfANewCopy_DeletesTheOldStoppedCreates_WithThePrivateFolderFirstSeen()
    {
        var stopped = StopACreate(Me);
        _share.CreatePrivate(Me);
        _clock.Advance(Old);

        var load = OpenAt(Me).Load();

        Assert.False(Directory.Exists(stopped), "The old stopped create is still there.");
        Assert.Empty(load.Skipped);
    }

    /// <summary>A clock put back looks again at once, rather than wait until it is past the last look again.</summary>
    [Fact]
    public void Load_AfterTheClockIsPutBack_LooksForTheStoppedCreatesAgain()
    {
        var clock = new SettableClock(_clock.GetUtcNow());
        var copy = DataCopy.Open(_share, _copy, clock, Me);
        copy.CreatePrivate();
        var listings = 0;
        _files.Before = (step, path) => listings += step == nameof(IFileSystem.List) && path == _private ? 1 : 0;

        copy.Load();
        clock.Now -= TimeSpan.FromHours(1);
        copy.Load();

        Assert.Equal(2, listings);
    }

    private static JsonObject Content(string name) => new() { ["name"] = name };

    private static SkippedEntry Reported(string stopped) => new(DataFolder.Private, Path.GetFileName(stopped), SkipReason.StoppedCreate);

    private static void AssertAllFoldersThere(string root)
    {
        foreach (var folder in Folders)
        {
            Assert.True(Directory.Exists(Path.Combine(root, folder)), "A folder of the stopped create is gone.");
        }
    }

    /// <summary>Opens the copy on <see cref="_clock"/>.</summary>
    private DataCopy OpenAt(string? account) => DataCopy.Open(_share, _copy, _clock, account);

    /// <summary>
    /// Makes the private folder of an account as <see cref="DataFolder.CreatePrivate(string)"/> does, and stops it before the
    /// rename, as a crash or a refusal there does: gives the temporary folder that it leaves, with its empty folders.
    /// </summary>
    private string StopACreate(string account)
    {
        string[] before = Directory.Exists(_private) ? Directory.GetDirectories(_private) : [];
        _files.Before = (step, _) =>
        {
            if (step == nameof(IFileSystem.MoveFolder))
            {
                throw new IOException("The test stopped the create before the rename.");
            }
        };
        try
        {
            Assert.Throws<IOException>(() => _share.CreatePrivate(account));
        }
        finally
        {
            _files.Before = null;
        }

        return Assert.Single(Directory.GetDirectories(_private).Except(before));
    }

    /// <summary>A clock that the test can also put back, which <see cref="FakeTimeProvider"/> refuses.</summary>
    private sealed class SettableClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}

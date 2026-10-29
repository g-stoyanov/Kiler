using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Principal;

namespace Kiler.Storage.Tests;

/// <summary>
/// What the layer relies on that only a file server answers, measured on the file server of the shared folder in M2.11a.
/// They run only when <see cref="TestFolder.Variable"/> names a folder on a file server, such as a temporary subfolder of the
/// shared folder, so they run again at the trial use in the real environment and after a move to another server: a failure
/// says which answer has changed and what it means for the layer. What they measure goes to the output of the test, which
/// a run of the test exe with <c>-class Kiler.Storage.Tests.FileServerTests -showLiveOutput</c> shows.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class FileServerTests : IDisposable
{
    private const string NotOnAFileServer = $"Runs only when {TestFolder.Variable} names a folder on a file server.";

    /// <summary><c>FILE_SUPPORTS_REPARSE_POINTS</c> of the flags of a volume.</summary>
    private const uint SupportsReparsePoints = 0x80;

    /// <summary>How many slots <see cref="AListing_RightAfterASlotIsRenamedIntoItsFolder_ShowsIt"/> makes.</summary>
    private const int Slots = 20;

    /// <summary>The SMB client keeps a listing for about 10 s (M2.7a); a change shows after it at the latest.</summary>
    private static readonly TimeSpan ListingCache = TimeSpan.FromSeconds(15);

    /// <summary>How long a file server may take to let go of a name once its last handle is closed.</summary>
    private static readonly TimeSpan CloseTakes = TimeSpan.FromSeconds(5);

    private static readonly FileSystem Files = new(new TestOwners());

    private readonly TestFolder _test = new();

    /// <summary>A value that is not a full path runs the tests too, so <see cref="TestFolder"/> refuses it with its message.</summary>
    public static bool OnAFileServer =>
        Environment.GetEnvironmentVariable(TestFolder.Variable) is { Length: > 0 } folder
        && !(Path.IsPathFullyQualified(folder) && TestFolder.IsOnLocalDisk(folder));

    public void Dispose() => _test.Dispose();

    /// <summary>
    /// The id of a file (M2.5) and the hold of a folder (M2.3) were measured on NTFS. The volume of the shared folder says
    /// NTFS too, without reparse points or POSIX deletes (M2.11); its flags go to the output.
    /// </summary>
    [Fact(Skip = NotOnAFileServer, SkipUnless = nameof(OnAFileServer))]
    public void TheVolume_IsNtfs()
    {
        var (name, flags) = TestFolder.Volume(_test.Root);

        Report($"The volume: {name}, flags 0x{flags:X8}.");
        Assert.True(name == "NTFS", $"The file system is {name}, not NTFS: check the id of a file across a move (M2.5) and the holds of folders (M2.3) there.");
    }

    /// <summary>
    /// No link can be made on the share: neither a mount point in place of a folder, held or not, nor a symbolic link. So a
    /// create-new or a rename without replace never meets one there (M2.1, M2.3), a folder above <c>data\</c> that someone
    /// renames cannot be put back as a link to elsewhere on the file server (the M2.11 limit), and the marker (M2.5) has
    /// nothing to stop. Whoever can make a link on the file server itself is outside the threat model. The volume says so too,
    /// so a mount point refused only for the form of its target, a network path, does not pass. A machine that makes no
    /// symbolic link itself, without developer mode, cannot tell whether the file server refuses one.
    /// </summary>
    [Fact(Skip = NotOnAFileServer, SkipUnless = nameof(OnAFileServer))]
    public void NoLink_CanBeMade_NeitherAMountPointInPlaceOfAFolderHeldOrNot_NorASymbolicLink()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_test.Root, "trash")).FullName;
        var held = Directory.CreateDirectory(Path.Combine(_test.Root, "records")).FullName;
        var elsewhere = Directory.CreateDirectory(Path.Combine(_test.Root, "elsewhere")).FullName;

        var free = TestFolder.TrySetMountPoint(folder, elsewhere);
        int whileHeld;
        using (Files.HoldFolder(held))
        {
            whileHeld = TestFolder.TrySetMountPoint(held, elsewhere);
        }

        var reparsePoints = (TestFolder.Volume(_test.Root).Flags & SupportsReparsePoints) != 0;
        var symbolic = MakesSymbolicLinks()
            ? Record.Exception(() => Directory.CreateSymbolicLink(Path.Combine(_test.Root, "link"), elsewhere))?.GetType().Name ?? "made"
            : "not tried, as this machine makes none";

        Report($"A mount point: error {free}, on a held folder error {whileHeld}; reparse points in the flags: {reparsePoints}. A symbolic link: {symbolic}.");
        Assert.False(reparsePoints, "The volume takes reparse points: check that no one can make a link there, and that a create-new and a rename without replace do not follow one (M2.11a).");
        Assert.True(free != 0, "The file server made a folder a mount point: check that a create-new and a rename without replace do not follow one (M2.11a).");
        Assert.True(whileHeld != 0, "The file server made a held folder a mount point: check that a create-new and a rename without replace do not follow one (M2.11a).");
        Assert.True(symbolic != "made", "The file server made a symbolic link: check that the layer refuses it (M2.3).");
    }

    /// <summary>
    /// The layer tells a file by its id after a move to the trash or back (M2.5), and a private folder by its id and owner
    /// (M2.6, M2.7d): neither id is 0, a move or a rename keeps it, a rename keeps the owner of a folder, and the folders that
    /// one user makes have the user as their owner (on a NAS that gives all files one owner, only the id would tell them
    /// apart).
    /// </summary>
    [Fact(Skip = NotOnAFileServer, SkipUnless = nameof(OnAFileServer))]
    public void TheIdOfAFileAndOfAFolder_IsNotZero_AndAMoveKeepsIt_AndTheOwnerOfAFolder_IsTheUser()
    {
        var records = Directory.CreateDirectory(Path.Combine(_test.Root, "records")).FullName;
        var trash = Directory.CreateDirectory(Path.Combine(_test.Root, "trash")).FullName;
        var path = Path.Combine(records, "8f3a1c.json");
        var made = Path.Combine(_test.Root, "demo.user.1a2b3c4d.tmp");
        var renamed = Path.Combine(_test.Root, "demo.user");
        File.WriteAllText(path, "{}");

        FileId file;
        using (var times = Files.OpenTimes(path)!)
        {
            file = times.Id;
            Assert.True(Files.MoveFile(path, Path.Combine(trash, "8f3a1c.json"), replace: false));
            Assert.True(Files.MoveFile(Path.Combine(trash, "8f3a1c.json"), path, replace: false));
        }

        FileId folder;
        using (var held = Files.HoldNewFolder(made)!)
        {
            folder = Files.Id(held);
        }

        var owner = TestFolder.OwnerOf(made);
        Files.MoveFolder(made, renamed);
        using var heldAgain = Files.HoldFolder(renamed);
        using var moved = Files.OpenTimes(path)!;
        using var user = WindowsIdentity.GetCurrent();

        Assert.True(file.Index != 0, "A file has the id 0: the check of a move to the trash or back tells nothing (M2.5).");
        Assert.True(file == moved.Id, "A move changed the id of a file: every move to the trash or back is refused after it is done (M2.5).");
        Assert.True(folder.Index != 0, "A folder has the id 0: every create of a private folder is refused (M2.6).");
        Assert.True(folder == Files.Id(heldAgain), "A rename changed the id of a folder: every create of a private folder is refused (M2.6).");
        Assert.True(owner == TestFolder.OwnerOf(renamed), "A rename changed the owner of a folder: every create of a private folder is refused (M2.6).");
        Assert.True(
            user.User!.Value == owner,
            "The owner of a folder is not the user who made it, as an administrator may get the Administrators group (M2.7d, M2.11b): the owner of a private folder then proves nothing.");
        Assert.True(owner == TestFolder.OwnerOf(records), "Two folders of the user have different owners: the private folders are checked against another owner (M2.7d).");
    }

    /// <summary>
    /// A stopped create is deleted folder by folder through their handles (M2.7e). The file server deletes a folder as it
    /// deletes a file: without the POSIX delete (M2.11), its name stays taken until the last handle to it is closed, here that
    /// of a reader. Whether a listing still shows the folder goes to the output: the listing of the layer did not show it on
    /// the shared folder, while it still showed such a file. It refuses to delete a folder that is not empty with
    /// ERROR_DIR_NOT_EMPTY, so what is put in a folder after its check stops the delete.
    /// </summary>
    [Fact(Skip = NotOnAFileServer, SkipUnless = nameof(OnAFileServer))]
    public void DeleteHeldFolder_FreesTheNameAsTheDeleteOfAFileDoes_AndRefusesAFolderThatIsNotEmpty()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_test.Root, "history")).FullName;
        var full = Directory.CreateDirectory(Path.Combine(_test.Root, "trash")).FullName;
        File.WriteAllText(Path.Combine(full, "notes.txt"), "made up");
        var posix = TestFolder.HasPosixDeletes(_test.Root);

        bool takenWhileRead;
        bool listedWhileRead;
        using (TestFolder.OpenFolderToList(folder))
        {
            using (var held = Files.HoldFolderToDelete(folder))
            {
                Files.DeleteHeldFolder(held);
            }

            listedWhileRead = IsListed(_test.Root, "history");
            using var again = Files.HoldNewFolder(folder);
            takenWhileRead = again is null;
        }

        IOException refused;
        using (var held = Files.HoldFolderToDelete(full))
        {
            refused = Assert.Throws<IOException>(() => Files.DeleteHeldFolder(held));
        }

        // The client may close a handle of a folder a moment late.
        var clock = Stopwatch.StartNew();
        var afterReader = Files.HoldNewFolder(folder);
        while (afterReader is null && clock.Elapsed < CloseTakes)
        {
            Thread.Sleep(200);
            afterReader = Files.HoldNewFolder(folder);
        }

        afterReader?.Dispose();

        Report(
            $"A folder deleted while someone reads it: its name {(takenWhileRead ? "stays taken until the reader lets go" : "goes at once")}, "
            + $"and a listing {(listedWhileRead ? "still shows it" : "no longer shows it")}.");
        Assert.True(
            takenWhileRead != posix,
            $"The volume says POSIX deletes: {posix}; the name of a deleted folder stays taken while it is read: {takenWhileRead}. The tests that ask HasPosixDeletes expect the two to agree.");
        Assert.True(afterReader is not null, $"The name of the deleted folder is still taken {CloseTakes.TotalSeconds} s after its last handle was closed.");
        Assert.True(refused.HResult == unchecked((int)0x80070091), $"A folder that is not empty was refused with 0x{refused.HResult:X8}, not ERROR_DIR_NOT_EMPTY.");
        Assert.True(Directory.Exists(full), "A folder that is not empty was deleted: what is put in a folder after the check of a stopped create goes with it (M2.7e).");
    }

    /// <summary>
    /// A write reads the times of its new file through the handle right after the write (<c>SetCreationToWriteTime</c>), and
    /// the copy compares them with those that the next listings give (M2.3, M2.7a), so the two must be the same. The age of a
    /// slot is the write time of its new file against that of the slot, both read through handles (M2.4). The offset to this
    /// machine's clock goes to the output: one machine cannot tell the clock of the file server from its own while they agree
    /// (with the clock of the machine some minutes off: M2.11b).
    /// </summary>
    [Fact(Skip = NotOnAFileServer, SkipUnless = nameof(OnAFileServer))]
    public void TheTimesOfANewFile_ReadThroughItsHandleRightAfterTheWrite_AreThoseThatAListingGives()
    {
        var path = Path.Combine(_test.Root, "8f3a1c.json");

        var before = DateTimeOffset.UtcNow;
        FileStamp stamp;
        using (var file = Files.CreateFile(path, "{}\n"u8)!)
        {
            stamp = Files.SetCreationToWriteTime(file);
        }

        var after = DateTimeOffset.UtcNow;
        var listed = Assert.Single(Files.List(_test.Root));

        Report(
            $"The write time of a new file: {(stamp.LastWriteTime - before).TotalMilliseconds:F0} ms after this machine's clock at the start of the write, "
            + $"{(after - stamp.LastWriteTime).TotalMilliseconds:F0} ms before it at its end.");
        Assert.True(stamp == listed.Stamp, $"A listing gives {listed.Stamp}, the handle gave {stamp}: every document that this Kiler writes looks changed at the next load (M2.7a).");
    }

    /// <summary>
    /// A stopped create is old by the write time of its folder in a listing of <c>private\</c> (M2.7e), and a create under
    /// way makes folders in it: each moves that time on to the creation time of the new folder, on the clock of the file
    /// server. The listing of the folder above can be cached for about 10 s, so the time it takes to show goes to the output.
    /// </summary>
    [Fact(Skip = NotOnAFileServer, SkipUnless = nameof(OnAFileServer))]
    public void TheWriteTimeOfAFolderInAListing_MovesToTheTimeAFolderIsMadeInIt()
    {
        const string Name = "demo.user.1a2b3c4d.tmp";
        using (Files.HoldNewFolder(Path.Combine(_test.Root, Name)))
        {
        }

        var first = Listed(_test.Root, Name).LastWriteTime;

        // The clock of the file server moves in steps of about 15 ms.
        Thread.Sleep(100);
        using (Files.HoldNewFolder(Path.Combine(_test.Root, Name, "records")))
        {
        }

        var made = Listed(Path.Combine(_test.Root, Name), "records").CreationTime;
        var clock = Stopwatch.StartNew();
        var moved = Listed(_test.Root, Name).LastWriteTime;
        while (moved == first && clock.Elapsed < ListingCache)
        {
            Thread.Sleep(200);
            moved = Listed(_test.Root, Name).LastWriteTime;
        }

        Report($"The write time of the folder in the listing moved after {clock.Elapsed.TotalMilliseconds:F0} ms, {(moved - made).TotalMilliseconds:F0} ms after the creation time of the folder made in it.");
        Assert.True(moved > first, $"The listing did not show the folder's write time move within {ListingCache.TotalSeconds} s.");
        Assert.True((moved - made).Duration() < TimeSpan.FromSeconds(1), "The write time of the folder is not the creation time of the folder made in it.");
    }

    /// <summary>
    /// A load lists the history folder of a document whose version changed and takes its new slots (M2.7a). A listing that
    /// the SMB client gives from its cache would not show a slot yet, and the copy would not take it. A slot that this
    /// machine makes changes the folder through the same client, and every listing right after it shows the slot (a slot
    /// that another machine makes: M2.11b).
    /// </summary>
    [Fact(Skip = NotOnAFileServer, SkipUnless = nameof(OnAFileServer))]
    public void AListing_RightAfterASlotIsRenamedIntoItsFolder_ShowsIt()
    {
        var history = Directory.CreateDirectory(Path.Combine(_test.Root, "8f3a1c")).FullName;
        var missed = 0;

        for (var version = 1; version <= Slots; version++)
        {
            // The listing of the load before, which the client may keep.
            Files.List(history);
            var temporary = Path.Combine(history, $"v{version}.1a2b3c4d.tmp");
            using (Files.CreateFile(temporary, "{}\n"u8))
            {
                Assert.True(Files.MoveFile(temporary, Path.Combine(history, $"v{version}.json"), replace: false));
            }

            if (!IsListed(history, $"v{version}.json"))
            {
                missed++;
            }
        }

        Report($"A listing right after a slot of this machine missed it {missed} times of {Slots}.");
        Assert.True(missed == 0, $"A listing missed {missed} of {Slots} slots right after this machine made them: the copy does not take them (M2.7a).");
    }

    /// <summary>
    /// A move into a name that another file left in the last 15 s takes that file's creation time on NTFS (file tunneling,
    /// M2.3), as a move to the trash over an older copy, so the layer puts the creation time back through the handle of its
    /// times (<c>KeepCreationTime</c>, M2.5). Whether the file server tunnels goes to the output; the creation time is right
    /// either way.
    /// </summary>
    [Fact(Skip = NotOnAFileServer, SkipUnless = nameof(OnAFileServer))]
    public void AMoveOverAnOlderCopy_KeepsItsCreationTime_ThroughKeepCreationTime()
    {
        var path = Path.Combine(Directory.CreateDirectory(Path.Combine(_test.Root, "records")).FullName, "8f3a1c.json");
        var older = Path.Combine(Directory.CreateDirectory(Path.Combine(_test.Root, "trash")).FullName, "8f3a1c.json");
        var created = new DateTime(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc);
        var olderCreated = new DateTime(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc);
        File.WriteAllText(older, "{}\n");
        File.SetCreationTimeUtc(older, olderCreated);
        File.WriteAllText(path, "{}\n");
        File.SetCreationTimeUtc(path, created);

        bool tunneled;
        using (var times = Files.OpenTimes(path)!)
        {
            Assert.True(Files.MoveFile(path, older, replace: true));
            tunneled = File.GetCreationTimeUtc(older) == olderCreated;

            Files.KeepCreationTime(times);
        }

        Report($"A move over an older copy {(tunneled ? "took its creation time" : "kept its own creation time")}.");
        Assert.True(created == File.GetCreationTimeUtc(older), "KeepCreationTime did not put the creation time back after the move: a document moved to the trash or back looks edited in place (M2.5).");
    }

    /// <summary>Whether this machine makes a symbolic link at all, on its own disk: an ordinary user needs developer mode.</summary>
    private static bool MakesSymbolicLinks()
    {
        var link = Path.Combine(Path.GetTempPath(), $"Kiler.Tests-link-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateSymbolicLink(link, Path.GetTempPath());
            Directory.Delete(link);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void Report(string line) => TestContext.Current.TestOutputHelper?.WriteLine(line);

    private static bool IsListed(string folder, string name) => Files.List(folder).Any(entry => entry.Name == name);

    private static FileEntry Listed(string folder, string name) => Assert.Single(Files.List(folder), entry => entry.Name == name);
}

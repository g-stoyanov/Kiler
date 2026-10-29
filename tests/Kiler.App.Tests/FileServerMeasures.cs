using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using Kiler.Storage;
using Microsoft.Extensions.Time.Testing;

namespace Kiler.App.Tests;

/// <summary>
/// The measures of M2.11c on the file server of the shared folder, with the owners of Windows (<see cref="FileOwner"/>): the
/// published exe on 2000 records end to end and a second start, and the load of an admin with the private folders of 20 other
/// users. They run only when <see cref="TempFolder.Variable"/> names a folder on a file server, the one of the exe also with
/// <see cref="KilerExe.Variable"/>, so they run again at the trial use in the real environment and after a move to another
/// server. They assert only that what they measure worked; the times go to the output of the test, which a run of the test exe
/// with <c>-class Kiler.App.Tests.FileServerMeasures -showLiveOutput</c> shows. The measures that need the internals of the
/// layer are in <c>Kiler.Storage.Tests.FileServerMeasures</c>.
/// </summary>
public sealed class FileServerMeasures : IDisposable
{
    private const string NotOnAFileServer = $"Runs only when {TempFolder.Variable} names a folder on a file server.";

    private const string NoExeOrNoFileServer =
        $"Runs only when {TempFolder.Variable} names a folder on a file server and {KilerExe.Variable} the published exe.";

    private const string FirstLoad = "Loaded the data folder: ";

    /// <summary>The records of design 5.3. The other folders get a few documents each, as a data folder in use has.</summary>
    private const int Records = 2000;

    private const int DocumentsOfEachOtherFolder = 10;

    private const int OtherUsers = 20;

    private const int RecordsOfEachUser = 50;

    /// <summary>How many loads of all the other private folders are timed, a minute apart on the clock of the copy.</summary>
    private const int MinuteLoads = 3;

    /// <summary>How long the first load of the exe may take, with the open of the data folder and of the local copy.</summary>
    private static readonly TimeSpan LoadTakes = TimeSpan.FromMinutes(10);

    private readonly TempFolder _share = TempFolder.OnTheTestDisk();
    private readonly TempFolder _local = new();

    public static bool OnAFileServer => TempFolder.OnAFileServer;

    public static bool OnAFileServerWithTheExe => TempFolder.OnAFileServer && KilerExe.Published is not null;

    public void Dispose()
    {
        try
        {
            _share.Dispose();
        }
        finally
        {
            _local.Dispose();
        }
    }

    /// <summary>
    /// The published exe on a data folder of the share with 2000 records, a few documents in each other folder and one in each
    /// folder of the trash: the time of its first load, which counts the open of the data folder and of the local copy and
    /// reads every document with its owner (M2.10), and the time of the first load of a second start, which lists them all and
    /// reads none again (M2.7a). Also a listing of the 2000 records (M2.7) and the writes that made them, each through a local
    /// copy, as a write of Kiler.
    /// </summary>
    [Fact(Skip = NoExeOrNoFileServer, SkipUnless = nameof(OnAFileServerWithTheExe))]
    public async Task ThePublishedExe_LoadsTheDocumentsOfTheShare_AndASecondStartReadsNoneAgain()
    {
        var data = Path.Combine(_share.Path, "data");
        var writer = Writer(DataFolder.Create(data, FileOwner.Instance), account: null);
        var making = Stopwatch.StartNew();
        string[] folders = [DataFolder.Templates, DataFolder.Stashes, DataFolder.Users, DataFolder.Records];
        foreach (var folder in folders)
        {
            var gone = DocumentKey.InFolder(folder, "gone");
            Written(writer.Create(gone, Content(gone.Id)));
            Assert.Equal(WriteOutcome.Deleted, writer.Delete(gone, 1).Outcome);
        }

        DocumentKey[] keys =
        [
            DocumentKey.AtRoot(DataFolder.Settings),
            .. folders[..^1].SelectMany(folder => Enumerable.Range(0, DocumentsOfEachOtherFolder).Select(n => DocumentKey.InFolder(folder, $"d{n:D2}"))),
            .. Enumerable.Range(0, Records).Select(n => DocumentKey.InFolder(DataFolder.Records, $"r{n:D4}")),
        ];
        foreach (var key in keys)
        {
            Written(writer.Create(key, Content(key.Id)));
        }

        var made = making.Elapsed;
        var writes = keys.Length + (2 * folders.Length);
        var listing = Stopwatch.StartNew();
        var listed = Directory.EnumerateFiles(Path.Combine(data, DataFolder.Records)).Count();
        var listed2000 = listing.Elapsed;

        using var exe = new KilerExe(data);
        var (first, firstTime) = await FirstLoadAsync(exe);
        var index = new FileInfo(Path.Combine(exe.CopyFolder, "index.json")).Length;
        var (second, secondTime) = await FirstLoadAsync(exe);

        Assert.Equal(Records, listed);
        Assert.StartsWith($"{FirstLoad}{keys.Length} new or changed, 0 gone, 0 changed outside Kiler, 0 skipped", first);
        Assert.StartsWith($"{FirstLoad}0 new or changed, 0 gone, 0 changed outside Kiler, 0 skipped", second);
        Report($"Made {keys.Length} documents and {folders.Length} in the trash with {writes} writes through a local copy in {made.TotalSeconds:F1} s, {made.TotalMilliseconds / writes:F0} ms a write.");
        Report($"A listing of the {Records} records: {listed2000.TotalMilliseconds:F0} ms.");
        Report($"The first start says \"{first}\", {firstTime.TotalSeconds:F1} s after the start of the process; the index of its copy has {index / 1024} KiB.");
        Report($"The second start says \"{second}\", {secondTime.TotalSeconds:F1} s after the start of the process.");
    }

    /// <summary>
    /// An admin's load with the private folders of 20 other users of 50 records each (M2.7c, M2.7d): the hold of each of them
    /// reads the owner and the id of its root and the owner of each folder in it. The first load that takes them reads the head
    /// of each document; a load within the minute takes none of them; a load a minute later lists them all and reads none. One
    /// load a minute takes them all and holds the lock of the copy meanwhile, and one that takes about 20 s or more closes the
    /// windows of 30 s in which the next load takes a folder again for a late listing (M2.8c). Nothing of the copy changes when
    /// a load takes folders that are as they were, so that the loads a minute later took them shows only in their times,
    /// against the load within the minute; the first load is checked by the files it copied.
    /// </summary>
    [Fact(Skip = NotOnAFileServer, SkipUnless = nameof(OnAFileServer))]
    public void AnAdminsLoad_OfThePrivateFoldersOf20OtherUsers()
    {
        var share = DataFolder.Create(Path.Combine(_share.Path, "data"), FileOwner.Instance);
        var accounts = Enumerable.Range(1, OtherUsers).Select(n => $"demo.user{n:D2}").ToArray();
        var making = Stopwatch.StartNew();
        foreach (var account in accounts)
        {
            var writer = Writer(share, account);
            writer.CreatePrivate();
            for (var n = 0; n < RecordsOfEachUser; n++)
            {
                var key = DocumentKey.InFolder(DataFolder.Records, $"p{n:D2}").InPrivate(account);
                Written(writer.Create(key, Content(key.Id)));
            }
        }

        var made = making.Elapsed;
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var copyFolder = Directory.CreateDirectory(Path.Combine(_local.Path, "admin")).FullName;
        var admin = DataCopy.Open(share, copyFolder, clock, "demo.admin");
        var (_, alone) = Timed(admin.Load);
        admin.CopyOthers(accounts);
        var (first, firstTime) = Timed(admin.Load);
        var (within, withinTime) = Timed(admin.Load);
        var minutes = new List<TimeSpan>();
        for (var n = 0; n < MinuteLoads; n++)
        {
            clock.Advance(DataCopy.OthersInterval);
            var (load, time) = Timed(admin.Load);
            Assert.Empty(load.Skipped);
            Assert.Empty(load.Outside);
            minutes.Add(time);
        }

        Assert.Empty(first.Skipped);
        Assert.Empty(within.Skipped);
        Assert.All(accounts, account => Assert.Equal(
            RecordsOfEachUser,
            Directory.GetFiles(Path.Combine(copyFolder, "data", DataFolder.Private, account, DataFolder.Records)).Length));
        Report($"Made {OtherUsers} private folders of {RecordsOfEachUser} records each through the copies of their users in {made.TotalSeconds:F1} s.");
        Report($"The admin's load of the data folder alone: {alone.TotalMilliseconds:F0} ms.");
        Report($"The first load with the {OtherUsers} others, which reads the head of each of their {OtherUsers * RecordsOfEachUser} documents: {firstTime.TotalMilliseconds:F0} ms.");
        Report($"A load within the minute, which takes none of them: {withinTime.TotalMilliseconds:F0} ms.");
        Report($"A load a minute later, which lists them all and reads none: {string.Join(", ", minutes.Select(time => $"{time.TotalMilliseconds:F0} ms"))}.");
    }

    /// <summary>Starts the exe, waits for the line of its first load, and ends it; the time is from the start of the process.</summary>
    private static async Task<(string Line, TimeSpan Time)> FirstLoadAsync(KilerExe exe)
    {
        // No page opens, and the load is said only while Kiler runs, so the first wait outlasts the load.
        using var run = exe.Start(["--first-wait", ((int)LoadTakes.TotalSeconds).ToString(CultureInfo.InvariantCulture)]);
        var line = await run.WaitForLineAsync(FirstLoad, LoadTakes);
        var time = Stopwatch.GetElapsedTime(run.Started);
        await run.KillAsync();
        return (line, time);
    }

    /// <summary>A local copy that writes to the share, as the Kiler of a user, in a folder of its own on the local disk.</summary>
    private DataCopy Writer(DataFolder share, string? account) =>
        DataCopy.Open(share, Directory.CreateDirectory(Path.Combine(_local.Path, account ?? "writer")).FullName, TimeProvider.System, account);

    private static (LoadResult Load, TimeSpan Time) Timed(Func<LoadResult> load)
    {
        var clock = Stopwatch.StartNew();
        var result = load();
        return (result, clock.Elapsed);
    }

    private static void Written(WriteResult result) => Assert.Equal(WriteOutcome.Written, result.Outcome);

    /// <summary>A made-up record of about 1.6 KB, as the 2000 records of 2–4 MB of design 5.3.</summary>
    private static JsonObject Content(string name) => new()
    {
        ["template"] = "db",
        ["name"] = $"Demo record {name}",
        ["host"] = $"{name}.demo.test",
        ["port"] = 5432,
        ["account"] = "demo.user",
        ["notes"] = string.Concat(Enumerable.Repeat($"Made-up notes of {name}. ", 60)),
    };

    private static void Report(string line) => TestContext.Current.TestOutputHelper?.WriteLine(line);
}

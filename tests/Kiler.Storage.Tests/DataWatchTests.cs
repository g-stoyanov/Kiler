using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Time.Testing;

namespace Kiler.Storage.Tests;

/// <summary>
/// The watching of the share (M2.8): a load at the start and then every <see cref="DataWatch.Interval"/> on the clock that the
/// watch is given, on a thread of its own, each load given to the upper layers as one event; the writes and the loads of the
/// copy kept apart; a stop that does not wait for a load beyond its limit.
/// </summary>
public sealed class DataWatchTests : IDisposable
{
    private const string Me = "demo.user";

    private static readonly DocumentKey Record = DocumentKey.InFolder(DataFolder.Records, "8f3a1c");
    private static readonly DocumentKey OtherRecord = DocumentKey.InFolder(DataFolder.Records, "b72e09");
    private static readonly DocumentKey ThirdRecord = DocumentKey.InFolder(DataFolder.Records, "c3d4e5");
    private static readonly DocumentKey Settings = DocumentKey.AtRoot(DataFolder.Settings);

    /// <summary>How long a test waits on the real clock for what the watch does, with a margin for a slow CI machine.</summary>
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    /// <summary>How long a test waits on the real clock to see that something does not happen.</summary>
    private static readonly TimeSpan Moment = TimeSpan.FromMilliseconds(300);

    private readonly TestFolder _test = new();
    private readonly TestFileSystem _files = new();
    private readonly FakeTimeProvider _clock = new(DateTimeOffset.UtcNow);
    private readonly DataFolder _share;
    private readonly DataCopy _copy;
    private readonly DataWatch _watch;
    private readonly BlockingCollection<LoadResult> _loads = [];
    private readonly BlockingCollection<Exception> _failures = [];

    /// <summary>Lets go of a load that a test holds (<see cref="HoldTheNextListing"/>).</summary>
    private readonly ManualResetEventSlim _release = new();

    public DataWatchTests()
    {
        _share = DataFolder.Create(Path.Combine(_test.Root, "data"), _files);
        var copy = Directory.CreateDirectory(Path.Combine(_test.Root, "copy")).FullName;
        _copy = DataCopy.Open(_share, copy, _clock, Me);
        _watch = new DataWatch(_copy, _clock);
        _watch.Loaded += (_, load) => _loads.Add(load);
        _watch.Failed += (_, ex) => _failures.Add(ex);
    }

    /// <summary>How a test calls a public member of the copy while a load is under way.</summary>
    public enum Member
    {
        Documents,
        Trash,
        CopyOthers,
        CreatePrivate,
        AcceptPrivateFolder,
        Load,
        Create,
        Replace,
        Delete,
        RestoreFromTrash,
    }

    public void Dispose()
    {
        // A load that a test held would keep the thread of the watch.
        _release.Set();
        var stopped = _watch.Stop(Wait);
        _watch.Dispose();
        _release.Dispose();
        _loads.Dispose();
        _failures.Dispose();
        _test.Dispose();
        Assert.True(stopped, "The watch did not stop.");
    }

    [Fact]
    public void Start_LoadsAtOnce_AndGivesTheDocumentsOfTheShare()
    {
        Create(Record, "record");
        Create(Settings, "settings");

        _watch.Start();

        var load = NextLoad();
        Assert.Equal(Keys([Record, Settings]), Keys(load.Changed));
        Assert.Empty(load.Gone);
        Assert.Empty(load.Skipped);
        Assert.False(load.TrashChanged, "A load of a share without a trash says that the trash changed.");
    }

    [Fact]
    public void EveryInterval_ANewAChangedAndADeletedDocument_GiveTheirEvents_AndNoLoadComesBefore()
    {
        Create(Record, "record");
        Create(OtherRecord, "deleted");
        _watch.Start();
        NextLoad();
        Create(ThirdRecord, "new");
        Assert.Equal(WriteOutcome.Written, _share.ReplaceDocument(Record, 1, Content("changed")).Outcome);
        Assert.Equal(WriteOutcome.Deleted, _share.DeleteDocument(OtherRecord, 1).Outcome);

        _clock.Advance(DataWatch.Interval - TimeSpan.FromMilliseconds(1));

        AssertNoLoad();
        _clock.Advance(TimeSpan.FromMilliseconds(1));
        var load = NextLoad();
        Assert.Equal(Keys([Record, ThirdRecord]), Keys(load.Changed));
        Assert.Equal("changed", Name(load.Changed.Single(document => document.Key == Record)));
        Assert.Equal(OtherRecord, Assert.Single(load.Gone));
        Assert.True(load.TrashChanged, "A load that saw a delete does not say that the trash changed.");
        Assert.Empty(load.Outside);
        Assert.Empty(load.Skipped);
        _clock.Advance(DataWatch.Interval);
        var still = NextLoad();
        Assert.Empty(still.Changed);
        Assert.Empty(still.Gone);
        Assert.False(still.TrashChanged, "A load that saw nothing says that the trash changed.");
    }

    [Fact]
    public void ADamagedFile_IsReportedOnce_AndDoesNotStopTheOthers()
    {
        Create(Record, "good");
        Create(OtherRecord, "other");
        _watch.Start();
        NextLoad();
        TestFolder.WriteAsKiler(_share.DocumentPath(Record), "{"u8.ToArray());
        Assert.Equal(WriteOutcome.Written, _share.ReplaceDocument(OtherRecord, 1, Content("changed")).Outcome);
        Create(ThirdRecord, "new");

        _clock.Advance(DataWatch.Interval);

        var load = NextLoad();
        Assert.Equal(Keys([OtherRecord, Record, ThirdRecord]), Keys(load.Changed));
        Assert.Equal(DocumentDamage.NotJson, load.Changed.Single(document => document.Key == Record).Read.Damage);
        Assert.Equal("changed", Name(load.Changed.Single(document => document.Key == OtherRecord)));
        Assert.Equal("new", Name(load.Changed.Single(document => document.Key == ThirdRecord)));
        Assert.Equal(new OutsideChange(Record, InTrash: false, OutsideReason.Damaged, LastGood: 1), Assert.Single(load.Outside));
        _clock.Advance(DataWatch.Interval);
        var next = NextLoad();
        Assert.Empty(next.Changed);
        Assert.Empty(next.Outside);
        Assert.Empty(next.Skipped);
    }

    [Fact]
    public void AnUnknownFolder_TemporaryFiles_AndFilesWithoutJson_AreIgnored()
    {
        Create(Record, "record");
        var diag = Directory.CreateDirectory(Path.Combine(_share.Root, "kiler-diag-0123abcd")).FullName;
        TestFolder.WriteAsKiler(Path.Combine(diag, "settings.json"), DocumentFormat.New(Settings, Content("diag")));
        var records = Path.Combine(_share.Root, DataFolder.Records);
        TestFolder.WriteAsKiler(Path.Combine(records, "b72e09.json.0123abcd.tmp"), DocumentFormat.New(OtherRecord, Content("temporary")));
        TestFolder.WriteAsKiler(Path.Combine(records, "c3d4e5.txt"), DocumentFormat.New(ThirdRecord, Content("text")));
        TestFolder.WriteAsKiler(Path.Combine(_share.Root, "settings.json.bak"), DocumentFormat.New(Settings, Content("backup")));
        var listed = new ConcurrentBag<string>();
        _files.Before = (step, path) =>
        {
            if (step == nameof(IFileSystem.List))
            {
                listed.Add(path);
            }
        };

        _watch.Start();

        var load = NextLoad();
        Assert.Equal(Record, Assert.Single(load.Changed).Key);
        Assert.Empty(load.Skipped);
        Assert.DoesNotContain(listed, path => path.Contains("kiler-diag", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(Record, Assert.Single(_copy.Documents()).Key);
    }

    [Fact]
    public void ALoadThatThrows_IsReported_AndTheNextLoadRuns()
    {
        Create(Record, "record");
        _files.Before = (step, _) =>
        {
            if (step == nameof(IFileSystem.List))
            {
                throw new InvalidOperationException("The test failed the load.");
            }
        };

        _watch.Start();

        Assert.True(_failures.TryTake(out var failure, Wait), "No failure came.");
        Assert.IsType<InvalidOperationException>(failure);
        AssertNoLoad();
        _files.Before = null;
        _clock.Advance(DataWatch.Interval);
        Assert.Equal(Record, Assert.Single(NextLoad().Changed).Key);
        Assert.Empty(_failures);
    }

    /// <summary>
    /// A load that throws after it took a document leaves it in the copy, written into its index, so no later load gives it
    /// (code review of M2.8): a handler of the failure reads the copy again.
    /// </summary>
    [Fact]
    public void ALoadThatThrowsAfterItTookADocument_LeavesItInTheCopy_AndNoLaterLoadGivesIt()
    {
        Create(Record, "record");
        var privateFolders = Path.Combine(_share.Root, DataFolder.Private);

        // The look for the stopped creates lists private\ after the documents are taken, and lets a fault of the code through.
        _files.Before = (step, path) =>
        {
            if (step == nameof(IFileSystem.List) && path == privateFolders)
            {
                throw new InvalidOperationException("The test failed the load after its documents.");
            }
        };

        _watch.Start();

        Assert.True(_failures.TryTake(out _, Wait), "No failure came.");
        _files.Before = null;
        Assert.Equal(Record, Assert.Single(_copy.Documents()).Key);
        _clock.Advance(DataWatch.Interval);
        Assert.Empty(NextLoad().Changed);
    }

    /// <summary>A handler may call the copy, as the load has let go of it (code review of M2.8).</summary>
    [Fact]
    public void Loaded_IsRaisedAfterTheLoadHasLetGoOfTheCopy()
    {
        var called = false;
        _watch.Loaded += (_, _) =>
        {
            var call = new Thread(() => _copy.Documents());
            call.Start();
            called = call.Join(TimeSpan.FromSeconds(5));
        };

        _watch.Start();

        NextLoad();
        Assert.True(_watch.Stop(Wait), "The watch did not stop.");
        Assert.True(called, "A call of the copy from another thread waited for the handler of the load.");
    }

    /// <summary>A stop from a handler does not wait for the thread that runs it (code review of M2.8).</summary>
    [Fact]
    public void Stop_FromAHandler_ReturnsAtOnce_AndTheWatchEndsAfterIt()
    {
        bool? stopped = null;
        var took = TimeSpan.MaxValue;
        _watch.Loaded += (_, _) =>
        {
            var started = Stopwatch.GetTimestamp();
            stopped = _watch.Stop(Wait);
            took = Stopwatch.GetElapsedTime(started);
        };

        _watch.Start();

        NextLoad();
        Assert.True(_watch.Stop(Wait), "The watch did not end after the handler.");
        Assert.False(stopped, "A stop from a handler says that the watch has ended.");
        Assert.True(took < TimeSpan.FromSeconds(5), $"A stop from a handler waited: {took}.");
        _clock.Advance(DataWatch.Interval);
        AssertNoLoad();
    }

    /// <summary>
    /// The writes come from requests and the loads from the watch (M2.7a note), so each public member of the copy waits for a
    /// load under way, and one that the load holds would otherwise run in the middle of it.
    /// </summary>
    [Theory]
    [InlineData(Member.Documents)]
    [InlineData(Member.Trash)]
    [InlineData(Member.CopyOthers)]
    [InlineData(Member.CreatePrivate)]
    [InlineData(Member.AcceptPrivateFolder)]
    [InlineData(Member.Load)]
    [InlineData(Member.Create)]
    [InlineData(Member.Replace)]
    [InlineData(Member.Delete)]
    [InlineData(Member.RestoreFromTrash)]
    public void EachPublicMemberOfTheCopy_WaitsForALoadUnderWay(Member member)
    {
        Create(Record, "record");
        Create(OtherRecord, "deleted");
        Assert.Equal(WriteOutcome.Deleted, _share.DeleteDocument(OtherRecord, 1).Outcome);
        _copy.Load();
        using var held = HoldTheNextListing();
        _watch.Start();
        Assert.True(held.Wait(Wait, TestContext.Current.CancellationToken), "The load did not start.");
        Exception? failure = null;
        var call = new Thread(() =>
        {
            try
            {
                Call(member);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });

        call.Start();

        Assert.False(call.Join(Moment), $"{member} did not wait for the load under way.");
        _release.Set();
        Assert.True(call.Join(Wait), $"{member} did not end after the load.");
        Assert.Null(failure);
        NextLoad();
    }

    [Fact]
    public void Stop_WhileALoadBlocks_ReturnsWithinItsLimit_AndNoEventFollows()
    {
        Create(Record, "record");
        using var held = HoldTheNextListing();
        _watch.Start();
        Assert.True(held.Wait(Wait, TestContext.Current.CancellationToken), "The load did not start.");
        var started = Stopwatch.GetTimestamp();

        var stopped = _watch.Stop(TimeSpan.FromMilliseconds(100));

        var took = Stopwatch.GetElapsedTime(started);
        Assert.False(stopped, "The watch ended while its load was held.");
        Assert.True(took < TimeSpan.FromSeconds(5), $"The stop waited for the load: {took}.");
        _release.Set();
        Assert.True(_watch.Stop(Wait), "The watch did not end after its load.");
        Assert.Empty(_loads);
        Assert.Empty(_failures);
        Assert.Equal(Record, Assert.Single(_copy.Documents()).Key);
    }

    [Fact]
    public void Stop_BetweenTwoLoads_EndsTheWatch_AndNoLoadFollows()
    {
        _watch.Start();
        NextLoad();
        var listings = 0;
        _files.Before = (step, _) => listings += step == nameof(IFileSystem.List) ? 1 : 0;

        Assert.True(_watch.Stop(Wait), "The watch did not stop.");

        _clock.Advance(DataWatch.Interval);
        AssertNoLoad();
        Assert.Equal(0, listings);
    }

    [Fact]
    public void Start_Twice_OrAfterTheStop_IsRefused()
    {
        _watch.Start();

        Assert.Throws<InvalidOperationException>(_watch.Start);
        NextLoad();
        Assert.True(_watch.Stop(Wait), "The watch did not stop.");
        Assert.Throws<InvalidOperationException>(_watch.Start);
    }

    [Fact]
    public void Stop_BeforeTheStart_EndsAWatchThatNeverLoads()
    {
        Assert.True(_watch.Stop(TimeSpan.Zero), "A watch that never started is not ended.");

        Assert.Throws<InvalidOperationException>(_watch.Start);
        AssertNoLoad();
    }

    private static JsonObject Content(string name) => new() { ["name"] = name };

    private static string? Name(LoadedDocument document) => (string?)document.Read.Document?.Content["name"];

    private static string[] Keys(IEnumerable<DocumentKey> keys) => [.. keys.Select(key => key.ToString()).Order(StringComparer.Ordinal)];

    private static string[] Keys(IEnumerable<LoadedDocument> documents) => Keys(documents.Select(document => document.Key));

    private void Create(DocumentKey key, string name) => Assert.Equal(WriteOutcome.Written, _share.CreateDocument(key, Content(name)).Outcome);

    /// <summary>
    /// The next load that the watch gives. The watch sets the timer of the next load before it gives one, so a test can move
    /// the clock on as soon as it has the load.
    /// </summary>
    private LoadResult NextLoad()
    {
        Assert.True(_loads.TryTake(out var load, Wait), $"No load came; failures: {string.Join("; ", _failures.Select(ex => ex.Message))}");
        return load;
    }

    private void AssertNoLoad() => Assert.False(_loads.TryTake(out _, Moment), "A load came before its time.");

    /// <summary>Holds the next listing of the share until <see cref="_release"/> is set: gives what is set once it is held.</summary>
    private ManualResetEventSlim HoldTheNextListing()
    {
        var held = new ManualResetEventSlim();
        var once = 0;
        _files.Before = (step, _) =>
        {
            if (step == nameof(IFileSystem.List) && Interlocked.Exchange(ref once, 1) == 0)
            {
                held.Set();
                _release.Wait(Wait);
            }
        };

        return held;
    }

    private void Call(Member member)
    {
        switch (member)
        {
            case Member.Documents:
                _copy.Documents();
                break;
            case Member.Trash:
                _copy.Trash();
                break;
            case Member.CopyOthers:
                _copy.CopyOthers(["demo.other"]);
                break;
            case Member.CreatePrivate:
                _copy.CreatePrivate();
                break;
            case Member.AcceptPrivateFolder:
                _copy.AcceptPrivateFolder("demo.other");
                break;
            case Member.Load:
                _copy.Load();
                break;
            case Member.Create:
                _copy.Create(ThirdRecord, Content("new"));
                break;
            case Member.Replace:
                _copy.Replace(Record, 1, Content("changed"));
                break;
            case Member.Delete:
                _copy.Delete(Record, 1);
                break;
            case Member.RestoreFromTrash:
                _copy.RestoreFromTrash(OtherRecord);
                break;
        }
    }
}

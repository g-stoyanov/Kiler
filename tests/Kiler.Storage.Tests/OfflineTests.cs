using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Time.Testing;

namespace Kiler.Storage.Tests;

/// <summary>
/// Working offline (M2.8a, PLAN M2 step 8): when the root of the data folder cannot be held or listed, the local copy is
/// offline, the watch says so with an event of its own, the writes through the copy are refused before they touch the share,
/// and the loop goes on trying and comes back by itself, with the same event. A file or a private folder that cannot be
/// opened, and a private folder that is not the one the copy knows, do not make it offline.
/// </summary>
public sealed class OfflineTests : IDisposable
{
    private const string Me = "demo.user";

    private static readonly DocumentKey Record = DocumentKey.InFolder(DataFolder.Records, "8f3a1c");
    private static readonly DocumentKey OtherRecord = DocumentKey.InFolder(DataFolder.Records, "b72e09");
    private static readonly DocumentKey ThirdRecord = DocumentKey.InFolder(DataFolder.Records, "c3d4e5");

    /// <summary>How long a test waits on the real clock for what the watch does, with a margin for a slow CI machine.</summary>
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    /// <summary>How long a test waits on the real clock to see that something does not happen.</summary>
    private static readonly TimeSpan Moment = TimeSpan.FromMilliseconds(300);

    private readonly TestFolder _test = new();
    private readonly TestFileSystem _files = new();
    private readonly FakeTimeProvider _clock = new(DateTimeOffset.UtcNow);
    private readonly DataFolder _share;
    private readonly string _copyFolder;
    private readonly DataCopy _copy;
    private readonly DataWatch _watch;

    /// <summary>The events of the watch in the order they came: a <see cref="LoadResult"/>, an <see cref="OfflineChange"/> or a failure.</summary>
    private readonly BlockingCollection<object> _events = [];

    /// <summary>Lets go of a step on the share that a test holds (<see cref="HoldTheNextTry"/>).</summary>
    private readonly ManualResetEventSlim _release = new();

    public OfflineTests()
    {
        _share = DataFolder.Create(Path.Combine(_test.Root, "data"), _files);
        _copyFolder = Directory.CreateDirectory(Path.Combine(_test.Root, "copy")).FullName;
        _copy = DataCopy.Open(_share, _copyFolder, _clock, Me);
        _watch = new DataWatch(_copy, _clock);
        Watch(_watch);
    }

    /// <summary>A write through the copy, or the read of the audit, which reaches the share as a write does (M6.17).</summary>
    public enum Write
    {
        Create,
        Replace,
        Delete,
        RestoreFromTrash,
        CreatePrivate,
        WriteBack,
        AppendAudit,
        ReadAudit,
    }

    /// <summary>A member of the copy that reads it, or that a sign-in calls, without touching the share.</summary>
    public enum Member
    {
        Documents,
        Trash,
        CopyOthers,
        AcceptPrivateFolder,
        ToRestore,
        Forget,
        ForgetPrivate,
    }

    public void Dispose()
    {
        // A step that a test held would keep the thread of the watch.
        _release.Set();
        var stopped = _watch.Stop(Wait);
        _watch.Dispose();
        _release.Dispose();
        _events.Dispose();
        _test.Dispose();
        Assert.True(stopped, "The watch did not stop.");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AShareThatCannotBeReached_MakesTheCopyOffline_WithOneEvent_AndItsReturnGivesTheSameEvent(bool unauthorized)
    {
        Create(Record, "record");
        Assert.False(_copy.Offline, "A new copy is offline.");
        var tried = GoAway(unauthorized);

        _watch.Start();

        var offline = Next<OfflineChange>();
        Assert.True(offline.Offline, "The event of a share that cannot be reached does not say offline.");
        Assert.IsType(unauthorized ? typeof(UnauthorizedAccessException) : typeof(IOException), offline.Cause);
        Assert.True(_copy.Offline, "The copy is not offline.");

        // The loop goes on trying, and says nothing more while the share stays away.
        _clock.Advance(DataWatch.Interval);
        Assert.True(SpinWait.SpinUntil(() => tried.Count >= 2, Wait), "The loop did not try the share again.");
        AssertNoEvent();
        Back();
        AdvanceUntilAnEvent();

        var online = Next<OfflineChange>();
        Assert.False(online.Offline, "The event of the return says offline.");
        Assert.Null(online.Cause);
        Assert.Equal(Record, Assert.Single(Next<LoadResult>().Changed).Key);
        Assert.False(_copy.Offline, "The copy is still offline.");
        _clock.Advance(DataWatch.Interval);
        Assert.Empty(Next<LoadResult>().Changed);
    }

    /// <summary>
    /// A start while the share cannot be reached (M2.8a, after the code review): the data folder opens without its check, the
    /// copy gives what it has, and the first load says offline.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AStartWhileTheShareCannotBeReached_GivesWhatTheCopyHas_AndItsFirstLoadSaysOffline(bool unauthorized)
    {
        Create(Record, "record");
        _copy.Load();
        GoAway(unauthorized);

        var share = DataFolder.Open(_share.Root, _files);

        var copy = DataCopy.Open(share, _copyFolder, _clock, Me);
        using var watch = new DataWatch(copy, _clock);
        Watch(watch);
        Assert.Equal(Record, Assert.Single(copy.Documents()).Key);
        watch.Start();
        var offline = Next<OfflineChange>();
        Assert.True(offline.Offline, "The first load of a share that cannot be reached does not say offline.");
        Assert.IsType(unauthorized ? typeof(UnauthorizedAccessException) : typeof(IOException), offline.Cause);
        Assert.Equal(WriteOutcome.Offline, copy.Create(ThirdRecord, Content("new")).Outcome);
        Back();
        _clock.Advance(DataWatch.Interval);
        Assert.False(Next<OfflineChange>().Offline, "The copy did not come back.");
        Assert.Empty(Next<LoadResult>().Changed);
        Assert.True(watch.Stop(Wait), "The watch did not stop.");
    }

    /// <summary>
    /// A data folder opened without its check (M2.8a) is held as itself by every load, so a root that is a link when the share
    /// is back is refused and never followed, as one that became a link after a check (M2.3).
    /// </summary>
    [Fact]
    public void AStartWhileTheShareCannotBeReached_ARootThatIsALinkOnceItIsBack_IsNeverFollowed()
    {
        Create(Record, "record");
        _copy.Load();
        GoAway();
        var share = DataFolder.Open(_share.Root, _files);
        Back();
        var moved = _share.Root + "-moved";
        Directory.Move(_share.Root, moved);
        TestFolder.WriteAsKiler(Path.Combine(moved, DataFolder.Records, "b72e09.json"), DocumentFormat.New(OtherRecord, Content("elsewhere")));
        TestFolder.CreateJunction(_share.Root, moved);
        var copy = DataCopy.Open(share, _copyFolder, _clock, Me);

        Assert.Throws<IOException>(() => copy.Load());

        Assert.True(copy.Offline, "A root that is a link did not leave the copy offline.");
        Assert.Equal(Record, Assert.Single(copy.Documents()).Key);
    }

    /// <summary>Only a load decides that the copy is offline (decided in M2.8a, by the user): a write that cannot reach the share throws as before.</summary>
    [Fact]
    public void AWriteThatCannotReachTheShare_Throws_AndOnlyTheNextLoadMakesTheCopyOffline()
    {
        _watch.Start();
        Next<LoadResult>();
        GoAway();

        Assert.Throws<IOException>(() => _copy.Create(Record, Content("new")));

        Assert.False(_copy.Offline, "A write made the copy offline.");
        AssertNoEvent();
        _clock.Advance(DataWatch.Interval);
        Assert.True(Next<OfflineChange>().Offline, "The next load did not make the copy offline.");
    }

    [Theory]
    [InlineData(Write.Create)]
    [InlineData(Write.Replace)]
    [InlineData(Write.Delete)]
    [InlineData(Write.RestoreFromTrash)]
    [InlineData(Write.CreatePrivate)]
    [InlineData(Write.AppendAudit)]
    [InlineData(Write.ReadAudit)]
    public void AWriteWhileOffline_IsRefusedBeforeItTouchesTheShare_AndGoesThroughOnceTheShareIsBack(Write write)
    {
        Create(Record, "record");
        Create(OtherRecord, "deleted");
        Assert.Equal(WriteOutcome.Deleted, _share.DeleteDocument(OtherRecord, 1).Outcome);
        _watch.Start();
        Next<LoadResult>();
        var tried = GoAway();
        _clock.Advance(DataWatch.Interval);
        Assert.True(Next<OfflineChange>().Offline, "The copy did not go offline.");
        var before = tried.Count;

        Assert.Equal(WriteOutcome.Offline, Attempt(write));

        Assert.Equal(before, tried.Count);
        Back();
        _clock.Advance(DataWatch.Interval);
        Assert.False(Next<OfflineChange>().Offline, "The copy did not come back.");
        Next<LoadResult>();
        Assert.Equal(write == Write.Delete ? WriteOutcome.Deleted : WriteOutcome.Written, Attempt(write));
        if (write == Write.CreatePrivate)
        {
            Assert.True(Directory.Exists(Path.Combine(_share.Root, DataFolder.Private, Me)), "The private folder was not made.");
        }
    }

    /// <summary>A write back is a write through the copy (M2.9a), refused while offline as the others are, with a change that the list gave.</summary>
    [Fact]
    public void AWriteBackWhileOffline_IsRefusedBeforeItTouchesTheShare_AndGoesThroughOnceTheShareIsBack()
    {
        Create(Record, "record");
        _watch.Start();
        Next<LoadResult>();
        File.Delete(_share.DocumentPath(Record));
        _clock.Advance(DataWatch.Interval);
        Next<LoadResult>();
        _clock.Advance(InPlaceEdits.SettleTime);
        Assert.Single(Next<LoadResult>().Outside);
        var gone = Assert.Single(_copy.ToRestore());
        var tried = GoAway();
        _clock.Advance(DataWatch.Interval);
        Assert.True(Next<OfflineChange>().Offline, "The copy did not go offline.");
        var before = tried.Count;

        Assert.Equal(WriteOutcome.Offline, _copy.WriteBack(gone).Outcome);

        Assert.Equal(before, tried.Count);
        Back();
        _clock.Advance(DataWatch.Interval);
        Assert.False(Next<OfflineChange>().Offline, "The copy did not come back.");
        Next<LoadResult>();
        Assert.Equal(WriteOutcome.Written, _copy.WriteBack(gone).Outcome);
        Assert.True(File.Exists(_share.DocumentPath(Record)), "The document was not written back.");
    }

    /// <summary>
    /// The writes are refused before the lock of the copy (decided in M2.8a, by the user), so a request does not wait up to
    /// about 60 s for a load blocked on a share that has gone (the M2.8 code review).
    /// </summary>
    [Theory]
    [InlineData(Write.Create)]
    [InlineData(Write.Replace)]
    [InlineData(Write.Delete)]
    [InlineData(Write.RestoreFromTrash)]
    [InlineData(Write.CreatePrivate)]
    [InlineData(Write.WriteBack)]
    [InlineData(Write.AppendAudit)]
    [InlineData(Write.ReadAudit)]
    public void AWriteWhileOffline_IsRefusedAtOnce_AlsoWhileALoadIsUnderWay(Write write)
    {
        GoAway();
        _watch.Start();
        Assert.True(Next<OfflineChange>().Offline, "The copy did not go offline.");
        using var held = HoldTheNextTry();
        _clock.Advance(DataWatch.Interval);
        Assert.True(held.Wait(Wait, TestContext.Current.CancellationToken), "The load did not start.");
        WriteOutcome? outcome = null;
        bool? offline = null;
        Exception? failure = null;
        var call = new Thread(() =>
        {
            try
            {
                offline = _copy.Offline;
                outcome = Attempt(write);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });

        call.Start();

        Assert.True(call.Join(TimeSpan.FromSeconds(5)), $"{write} while offline waited for the load under way.");
        Assert.Null(failure);
        Assert.True(offline, "The copy did not say that it is offline.");
        Assert.Equal(WriteOutcome.Offline, outcome);
        _release.Set();
        AssertNoEvent();
    }

    /// <summary>
    /// While offline, a load that reaches the root holds the lock of the copy for its listings (M2.8c), which can block on a
    /// share that has gone again; a write looks before the lock, so it does not wait for that load either.
    /// </summary>
    [Theory]
    [InlineData(Write.Create)]
    [InlineData(Write.Replace)]
    [InlineData(Write.Delete)]
    [InlineData(Write.RestoreFromTrash)]
    [InlineData(Write.CreatePrivate)]
    [InlineData(Write.WriteBack)]
    [InlineData(Write.AppendAudit)]
    [InlineData(Write.ReadAudit)]
    public void AWriteWhileOffline_IsRefusedAtOnce_AlsoWhileALoadHoldsTheCopy(Write write)
    {
        GoAway();
        _watch.Start();
        Assert.True(Next<OfflineChange>().Offline, "The copy did not go offline.");
        using var held = HoldTheNextListing();
        _clock.Advance(DataWatch.Interval);
        Assert.True(held.Wait(Wait, TestContext.Current.CancellationToken), "The load did not list.");
        WriteOutcome? outcome = null;
        Exception? failure = null;
        var call = new Thread(() =>
        {
            try
            {
                outcome = Attempt(write);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });

        call.Start();

        Assert.True(call.Join(TimeSpan.FromSeconds(5)), $"{write} while offline waited for the load that holds the copy.");
        Assert.Null(failure);
        Assert.Equal(WriteOutcome.Offline, outcome);
        _release.Set();
        AssertNoEvent();
    }

    /// <summary>A write that waited for a load that went offline in the meantime looks again under the lock, and is refused.</summary>
    [Theory]
    [InlineData(Write.Create)]
    [InlineData(Write.Replace)]
    [InlineData(Write.Delete)]
    [InlineData(Write.RestoreFromTrash)]
    [InlineData(Write.CreatePrivate)]
    [InlineData(Write.WriteBack)]
    [InlineData(Write.AppendAudit)]
    [InlineData(Write.ReadAudit)]
    public void AWriteThatWaitedForALoadThatWentOffline_IsRefused(Write write)
    {
        _watch.Start();
        Next<LoadResult>();
        using var held = HoldTheNextTry();
        _clock.Advance(DataWatch.Interval);
        Assert.True(held.Wait(Wait, TestContext.Current.CancellationToken), "The load did not start.");
        WriteOutcome? outcome = null;
        Exception? failure = null;
        var call = new Thread(() =>
        {
            try
            {
                outcome = Attempt(write);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });

        call.Start();

        Assert.False(call.Join(Moment), $"{write} did not wait for the load under way.");
        _release.Set();
        Assert.True(call.Join(Wait), $"{write} did not end after the load.");
        Assert.Null(failure);
        Assert.Equal(WriteOutcome.Offline, outcome);
        Assert.True(Next<OfflineChange>().Offline, "The copy did not go offline.");
    }

    /// <summary>
    /// While offline, a load tries the share before it takes the lock of the copy (decided in M2.8c, by the user), so a read of
    /// the copy, or a call of a sign-in, does not wait up to about 60 s for a try at a share that has gone (the code review of
    /// M2.8a); the try that fails says nothing, and the one that reaches the share loads it.
    /// </summary>
    [Theory]
    [InlineData(Member.Documents)]
    [InlineData(Member.Trash)]
    [InlineData(Member.CopyOthers)]
    [InlineData(Member.AcceptPrivateFolder)]
    [InlineData(Member.ToRestore)]
    [InlineData(Member.Forget)]
    [InlineData(Member.ForgetPrivate)]
    public void WhileOffline_ATryOfTheShare_DoesNotHoldTheCopy(Member member)
    {
        Create(Record, "record");
        _watch.Start();
        Next<LoadResult>();
        GoAway();
        _clock.Advance(DataWatch.Interval);
        Assert.True(Next<OfflineChange>().Offline, "The copy did not go offline.");
        using var held = HoldTheNextTry();
        _clock.Advance(DataWatch.Interval);
        Assert.True(held.Wait(Wait, TestContext.Current.CancellationToken), "The try did not start.");
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

        Assert.True(call.Join(TimeSpan.FromSeconds(5)), $"{member} while offline waited for the try under way.");
        Assert.Null(failure);
        _release.Set();
        AssertNoEvent();
        Assert.True(_copy.Offline, "A try that failed brought the copy back.");
        Back();
        AdvanceUntilAnEvent();
        Assert.False(Next<OfflineChange>().Offline, "The copy did not come back.");
        Next<LoadResult>();
    }

    /// <summary>Only the share that cannot be reached is offline (M2.8a): not a folder that is not the one the copy knows (M2.7d), nor a fault of the code.</summary>
    [Fact]
    public void IsUnreachable_TellsTheShareThatHasGone_FromAForeignFolderAndAFaultOfTheCode()
    {
        Assert.True(DataCopy.IsUnreachable(new IOException("Made up.")), "An IOException is not the share that has gone.");
        Assert.True(DataCopy.IsUnreachable(new DirectoryNotFoundException("Made up.")), "A root that has gone is not the share that has gone.");
        Assert.True(DataCopy.IsUnreachable(new UnauthorizedAccessException("Made up.")), "A root that is denied is not the share that has gone.");
        Assert.False(DataCopy.IsUnreachable(new ForeignFolderException("Made up.")), "A foreign folder is the share that has gone.");
        Assert.False(DataCopy.IsUnreachable(new InvalidOperationException("Made up.")), "A fault of the code is the share that has gone.");
    }

    [Fact]
    public void AFileThatCannotBeOpened_IsReported_AndTheCopyStaysOnline()
    {
        Create(Record, "record");
        Create(OtherRecord, "other");
        var path = _share.DocumentPath(Record);
        _files.Before = (step, at) =>
        {
            if (step == nameof(IFileSystem.OpenRead) && at == path)
            {
                throw new IOException("The test holds the file.");
            }
        };

        _watch.Start();

        var load = Next<LoadResult>();
        Assert.Equal(OtherRecord, Assert.Single(load.Changed).Key);
        Assert.Equal(new SkippedEntry(DataFolder.Records, "8f3a1c.json", SkipReason.Unreadable), Assert.Single(load.Skipped));
        Assert.False(_copy.Offline, "A file that cannot be opened made the copy offline.");
        Assert.Equal(WriteOutcome.Written, _copy.Create(ThirdRecord, Content("third")).Outcome);
    }

    [Fact]
    public void APrivateFolderThatCannotBeOpened_IsReported_AndNeitherItNorAWriteToItMakesTheCopyOffline()
    {
        var mine = _share.CreatePrivate(Me);
        Create(Record, "record");
        _files.Before = (step, at) =>
        {
            if (step == nameof(IFileSystem.Attributes) && at == mine.Root)
            {
                throw new UnauthorizedAccessException("The test denies the folder.");
            }
        };

        _watch.Start();

        var load = Next<LoadResult>();
        Assert.Equal(Record, Assert.Single(load.Changed).Key);
        Assert.Equal(new SkippedEntry(@"private\demo.user", null, SkipReason.Unreadable), Assert.Single(load.Skipped));
        Assert.Throws<UnauthorizedAccessException>(() => _copy.Create(ThirdRecord.InPrivate(Me), Content("mine")));
        Assert.False(_copy.Offline, "A private folder that cannot be opened made the copy offline.");
        Assert.Equal(WriteOutcome.Written, _copy.Create(ThirdRecord, Content("third")).Outcome);
        _clock.Advance(DataWatch.Interval);
        Next<LoadResult>();
    }

    [Fact]
    public void APrivateFolderThatIsNotTheOneTheCopyKnows_IsReported_AndAWriteToItIsRefusedWithoutMakingTheCopyOffline()
    {
        var mine = _share.CreatePrivate(Me);
        _watch.Start();
        Next<LoadResult>();
        _files.Owners.Set(mine.Root, TestOwners.Other);
        _clock.Advance(DataWatch.Interval);

        var load = Next<LoadResult>();

        Assert.Equal(new SkippedEntry(@"private\demo.user", null, SkipReason.ForeignFolder), Assert.Single(load.Skipped));
        Assert.Throws<ForeignFolderException>(() => _copy.Create(Record.InPrivate(Me), Content("mine")));
        Assert.False(_copy.Offline, "A private folder of another owner made the copy offline.");
        Assert.Equal(WriteOutcome.Written, _copy.Create(Record, Content("shared")).Outcome);
        _clock.Advance(DataWatch.Interval);
        Next<LoadResult>();
    }

    /// <summary>A fault of the code is a failure, not the share that went away, and leaves the state as it was.</summary>
    [Fact]
    public void ALoadThatThrowsAnythingElse_IsAFailure_AndKeepsTheStateOfTheCopy()
    {
        Create(Record, "record");
        FailTheListings();
        _watch.Start();

        Assert.IsType<InvalidOperationException>(Next<Exception>());
        Assert.False(_copy.Offline, "A fault of the code made the copy offline.");
        GoAway();
        _clock.Advance(DataWatch.Interval);
        Assert.True(Next<OfflineChange>().Offline, "The copy did not go offline.");
        FailTheListings();
        _clock.Advance(DataWatch.Interval);
        Assert.IsType<InvalidOperationException>(Next<Exception>());
        Assert.True(_copy.Offline, "A fault of the code brought the copy back.");
        Back();
        _clock.Advance(DataWatch.Interval);
        Assert.False(Next<OfflineChange>().Offline, "The copy did not come back.");
        Assert.Equal(Record, Assert.Single(Next<LoadResult>().Changed).Key);
    }

    /// <summary>
    /// A load that went offline after it took documents has written them into the copy, and no later load gives them (the
    /// M2.8 note), so a handler of the return reads the copy again.
    /// </summary>
    [Fact]
    public void ALoadThatWentOfflineAfterItTookADocument_LeavesItInTheCopy_AndTheReturnDoesNotGiveItAgain()
    {
        Create(Record, "record");
        _copy.CopyOthers(["demo.other"]);
        var privateFolders = Path.Combine(_share.Root, DataFolder.Private);
        var gone = false;

        // The look for the stopped creates lists private\ after the documents of the user's own are taken, and the share
        // goes away after it, before the private folders of other users.
        _files.Before = (step, path) =>
        {
            if (step == nameof(IFileSystem.List) && path == privateFolders)
            {
                gone = true;
            }
            else if (gone && IsOnShare(path))
            {
                throw new IOException("The test took the share away.");
            }
        };

        _watch.Start();

        Assert.True(Next<OfflineChange>().Offline, "The copy did not go offline.");
        Back();
        Assert.Equal(Record, Assert.Single(_copy.Documents()).Key);
        _clock.Advance(DataWatch.Interval);
        Assert.False(Next<OfflineChange>().Offline, "The copy did not come back.");
        Assert.Empty(Next<LoadResult>().Changed);
    }

    private static JsonObject Content(string name) => new() { ["name"] = name };

    private void Create(DocumentKey key, string name) => Assert.Equal(WriteOutcome.Written, _share.CreateDocument(key, Content(name)).Outcome);

    private WriteResult Call(Write write) => write switch
    {
        Write.Create => _copy.Create(ThirdRecord, Content("new")),
        Write.Replace => _copy.Replace(Record, 1, Content("changed")),
        Write.Delete => _copy.Delete(Record, 1),
        Write.RestoreFromTrash => _copy.RestoreFromTrash(OtherRecord),
        Write.WriteBack => _copy.WriteBack(new OutsideChange(Record, InTrash: false, OutsideReason.Gone, LastGood: 1)),
        _ => throw new ArgumentOutOfRangeException(nameof(write)),
    };

    private void Call(Member member)
    {
        switch (member)
        {
            case Member.Documents:
                Assert.Equal(Record, Assert.Single(_copy.Documents()).Key);
                break;
            case Member.Trash:
                Assert.Empty(_copy.Trash());
                break;
            case Member.CopyOthers:
                _copy.CopyOthers(["demo.other"]);
                break;
            case Member.AcceptPrivateFolder:
                _copy.AcceptPrivateFolder(Me);
                break;
            case Member.ToRestore:
                Assert.Empty(_copy.ToRestore());
                break;
            case Member.Forget:
                Assert.Equal(0, _copy.Forget([new OutsideChange(Record, InTrash: false, OutsideReason.Gone, LastGood: 1)]));
                break;
            case Member.ForgetPrivate:
                _copy.ForgetPrivate("demo.other");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(member));
        }
    }

    /// <summary>
    /// Makes a write through the copy and gives how it ended; <see cref="DataCopy.CreatePrivate"/> and
    /// <see cref="DataCopy.ReadAudit"/>, which give no outcome, end <see cref="WriteOutcome.Written"/>, or
    /// <see cref="WriteOutcome.Offline"/> with an <see cref="OfflineException"/>.
    /// </summary>
    private WriteOutcome Attempt(Write write)
    {
        if (write == Write.AppendAudit)
        {
            return _copy.AppendAudit(new DateOnly(2026, 10, 31), Me, "{}"u8);
        }

        if (write is not (Write.CreatePrivate or Write.ReadAudit))
        {
            return Call(write).Outcome;
        }

        try
        {
            if (write == Write.CreatePrivate)
            {
                _copy.CreatePrivate();
            }
            else
            {
                _copy.ReadAudit(new DateOnly(2026, 10, 31), new DateOnly(2026, 10, 31));
            }

            return WriteOutcome.Written;
        }
        catch (OfflineException)
        {
            return WriteOutcome.Offline;
        }
    }

    /// <summary>The next event of the watch, which must be a <typeparamref name="T"/>.</summary>
    private T Next<T>()
    {
        Assert.True(_events.TryTake(out var next, Wait), "No event came.");
        return Assert.IsAssignableFrom<T>(next);
    }

    private void AssertNoEvent() => Assert.False(_events.TryTake(out var next, Moment), $"An event came: {next}");

    /// <summary>
    /// Moves the clock on until the watch raises an event, for a load that comes due after one that raised none: the timer of
    /// the next load is made after that load ends, with no event to tell the test (code review of M2.8a). An extra load is
    /// harmless where it is used.
    /// </summary>
    private void AdvanceUntilAnEvent()
    {
        var started = Stopwatch.GetTimestamp();
        while (_events.Count == 0)
        {
            Assert.True(Stopwatch.GetElapsedTime(started) < Wait, "No event came.");
            _clock.Advance(DataWatch.Interval);
            SpinWait.SpinUntil(() => _events.Count > 0, Moment);
        }
    }

    /// <summary>Puts the events of a watch into <see cref="_events"/>.</summary>
    private void Watch(DataWatch watch)
    {
        watch.Loaded += (_, load) => _events.Add(load);
        watch.Failed += (_, ex) => _events.Add(ex);
        watch.OfflineChanged += (_, change) => _events.Add(change);
    }

    private bool IsOnShare(string path) =>
        path.Equals(_share.Root, StringComparison.OrdinalIgnoreCase) || path.StartsWith(_share.Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    /// <summary>Takes the share away: every step of the file system on it fails, as when the network has gone. Gives the steps that were tried on it.</summary>
    private ConcurrentQueue<string> GoAway(bool unauthorized = false)
    {
        var tried = new ConcurrentQueue<string>();
        _files.Before = (step, path) =>
        {
            if (IsOnShare(path))
            {
                tried.Enqueue(step);
                throw unauthorized ? new UnauthorizedAccessException("The test took the share away.") : new IOException("The test took the share away.");
            }
        };

        return tried;
    }

    private void Back() => _files.Before = null;

    /// <summary>Makes every listing fail with a fault of the code, while the share is there.</summary>
    private void FailTheListings() => _files.Before = (step, _) =>
    {
        if (step == nameof(IFileSystem.List))
        {
            throw new InvalidOperationException("The test failed the load.");
        }
    };

    /// <summary>
    /// Lets the holds of folders on the share go through, so a load reaches the root and takes the lock of the copy, holds its
    /// first listing until <see cref="_release"/> is set, and fails every listing: gives what is set once the listing is held.
    /// </summary>
    private ManualResetEventSlim HoldTheNextListing()
    {
        var held = new ManualResetEventSlim();
        var once = 0;
        _files.Before = (step, path) =>
        {
            if (step == nameof(IFileSystem.List) && IsOnShare(path))
            {
                if (Interlocked.Exchange(ref once, 1) == 0)
                {
                    held.Set();
                    _release.Wait(Wait);
                }

                throw new IOException("The test took the share away.");
            }
        };

        return held;
    }

    /// <summary>
    /// Holds the next step on the share until <see cref="_release"/> is set, and takes the share away from that step on: gives
    /// what is set once the step is held.
    /// </summary>
    private ManualResetEventSlim HoldTheNextTry()
    {
        var held = new ManualResetEventSlim();
        var once = 0;
        _files.Before = (_, path) =>
        {
            if (IsOnShare(path))
            {
                if (Interlocked.Exchange(ref once, 1) == 0)
                {
                    held.Set();
                    _release.Wait(Wait);
                }

                throw new IOException("The test took the share away.");
            }
        };

        return held;
    }
}

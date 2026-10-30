using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Kiler.Core;
using Kiler.Crypto;
using Kiler.Storage;
using Microsoft.Extensions.Time.Testing;

namespace Kiler.App.Tests;

/// <summary>The data of a start (M2.10): the data folder and its local copy, opened once the server listens and watched until it stops.</summary>
public sealed class KilerDataTests : IDisposable
{
    private const string FirstLoad = "Loaded the data folder: ";

    /// <summary>The longest wait for a load of a few files on the local disk, on a busy CI machine.</summary>
    private static readonly TimeSpan Quick = TimeSpan.FromSeconds(30);

    private readonly TempFolder _folder = new();
    private readonly LineWriter _output = new();
    private readonly LineWriter _errors = new();
    private readonly DataPlace _place;

    public KilerDataTests()
    {
        _place = DataPlace.For(Path.Combine(_folder.Path, "data"), Path.Combine(_folder.Path, "Kiler"));
    }

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        _output.Dispose();
        _errors.Dispose();
        _folder.Dispose();
    }

    [Fact]
    public async Task Start_WithADataFolderOfMadeUpDocuments_FillsItsLocalCopy()
    {
        var made = MadeUp();
        string[] ids = ["a1", "b2", "c3"];
        foreach (var id in ids)
        {
            made.Create(id);
        }

        using var data = NewData(TimeProvider.System);
        await using (await StartServerAsync(data))
        {
            await data.Started.WaitAsync(Quick, Cancel);

            foreach (var id in ids)
            {
                Assert.Equal(File.ReadAllBytes(made.RecordFile(id)), File.ReadAllBytes(MadeUpData.CopyFile(_place.CopyFolder, id)));
            }

            Assert.True(File.Exists(Path.Combine(_place.CopyFolder, "index.json")), "The local copy has no index.");
            Assert.Equal(3, Assert.IsType<DataCopy>(data.Copy).Documents().Count);
            Assert.True(IsLocked(), "The local copy is not locked.");
            Assert.Equal(DataState.Open, data.State);
            Assert.Equal(_place.DataFolder, data.Folder);
        }

        // Each level is made private (PrivateFolder), so none takes what the profile grants.
        foreach (var folder in new[] { _place.LocalFolder, _place.CopiesFolder, _place.CopyFolder })
        {
            Assert.True(new DirectoryInfo(folder).GetAccessControl().AreAccessRulesProtected, $"Not private: {folder}");
        }

        Assert.Equal($"Kiler opens the data folder {_place.DataFolder}, with its local copy in {_place.CopyFolder}.", _output.Lines[0]);
        Assert.StartsWith($"{FirstLoad}3 new or changed, 0 gone, 0 changed outside Kiler, 0 skipped, in ", _output.Lines[1], StringComparison.Ordinal);
        Assert.Equal(2, _output.Lines.Count);
        Assert.Empty(_errors.Lines);
    }

    [Fact]
    public async Task SecondStart_ReadsOnlyWhatChanged()
    {
        var made = MadeUp();
        made.Create("a1");
        made.Create("b2");
        made.Create("c3");
        using (var first = NewData(TimeProvider.System))
        {
            first.Start();
            await first.Started.WaitAsync(Quick, Cancel);
        }

        made.Replace("b2", 1);
        made.Create("d4");
        using var output = new LineWriter();
        using var second = new KilerData(_place, FileOwner.Instance, TimeProvider.System, output, _errors);
        second.Start();
        await second.Started.WaitAsync(Quick, Cancel);

        Assert.StartsWith($"{FirstLoad}2 new or changed, 0 gone, 0 changed outside Kiler, 0 skipped, in ", output.Lines[^1], StringComparison.Ordinal);
        Assert.Equal(4, Assert.IsType<DataCopy>(second.Copy).Documents().Count);
        Assert.Empty(_errors.Lines);
    }

    [Fact]
    public async Task StopOfTheHost_StopsTheWatch_AndDisposeLetsTheCopyGo()
    {
        var made = MadeUp();
        made.Create("a1");
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        using var data = NewData(clock);
        await using (await StartServerAsync(data))
        {
            await data.Started.WaitAsync(Quick, Cancel);

            // A load that changed nothing says nothing. One that took longer than the wait would take the next record too, and
            // say it as the load after it should.
            clock.Advance(DataWatch.Interval);
            await Task.Delay(TimeSpan.FromSeconds(1), Cancel);
            made.Create("b2");
            clock.Advance(DataWatch.Interval);
            // The whole line: the first load says one record too, with its time.
            await _output.WaitForLineAsync($"{FirstLoad}1 new or changed, 0 gone, 0 changed outside Kiler, 0 skipped.", Quick);
        }

        // The server has stopped, so no load takes the new record: the clock alone would bring the next one.
        made.Create("c3");
        clock.Advance(DataWatch.Interval);
        await Task.Delay(TimeSpan.FromSeconds(1), Cancel);
        Assert.False(File.Exists(MadeUpData.CopyFile(_place.CopyFolder, "c3")), "A load ran after the stop.");
        Assert.Equal(3, _output.Lines.Count);

        // Requests can use the copy until the server has stopped, so it stays locked until the data is disposed.
        Assert.True(IsLocked(), "The local copy was let go before the data was disposed.");
        data.Dispose();
        Assert.False(IsLocked(), "The local copy is still locked.");
        Assert.Empty(_errors.Lines);
    }

    [Fact]
    public async Task Start_WhileAnotherKilerHasTheCopyOpen_RunsWithoutData_AndSaysSo()
    {
        MadeUp().Create("a1");
        using var first = NewData(TimeProvider.System);
        first.Start();
        await first.Started.WaitAsync(Quick, Cancel);

        using var output = new LineWriter();
        using var errors = new LineWriter();
        using var second = new KilerData(_place, FileOwner.Instance, TimeProvider.System, output, errors);
        second.Start();
        await second.Started.WaitAsync(Quick, Cancel);

        Assert.Null(second.Copy);
        Assert.Null(second.Team);
        Assert.Equal(DataState.InUse, second.State);
        Assert.Equal(DataState.Open, first.State);
        Assert.Equal(["Kiler runs without data: another Kiler of this user has the same data folder open, in another session, or one started with --data and one without."], errors.Lines);
        Assert.Empty(output.Lines);
        Assert.NotNull(first.Copy);
        Assert.True(IsLocked(), "The second start let the copy of the first go.");
    }

    [Fact]
    public async Task Start_WithoutTheDataFolder_RunsWithoutData_SaysSo_AndMakesNothing()
    {
        using var data = NewData(TimeProvider.System);
        data.Start();
        await data.Started.WaitAsync(Quick, Cancel);

        Assert.Null(data.Copy);
        Assert.Equal(DataState.Missing, data.State);
        Assert.Equal([$"Kiler runs without data: the data folder does not exist: {_place.DataFolder}"], _errors.Lines);
        Assert.Empty(_output.Lines);
        Assert.False(Directory.Exists(_place.DataFolder), "The data folder was made.");
        Assert.False(Directory.Exists(_place.LocalFolder), "A local copy was made.");
    }

    [Fact]
    public async Task Start_WhenTheIndexCannotBeReadNow_RunsWithoutData_KeepsTheCopy_AndLetsItGo()
    {
        MadeUp().Create("a1");
        using (var first = NewData(TimeProvider.System))
        {
            first.Start();
            await first.Started.WaitAsync(Quick, Cancel);
        }

        using (new FileStream(Path.Combine(_place.CopyFolder, "index.json"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            using var data = NewData(TimeProvider.System);
            data.Start();
            await data.Started.WaitAsync(Quick, Cancel);

            Assert.Null(data.Copy);
            Assert.Equal(DataState.IndexUnreadable, data.State);
            Assert.StartsWith("Kiler runs without data: the index of its local copy cannot be read now, so start Kiler again: ", Assert.Single(_errors.Lines), StringComparison.Ordinal);
            Assert.False(IsLocked(), "The copy stayed locked.");
        }

        Assert.True(File.Exists(MadeUpData.CopyFile(_place.CopyFolder, "a1")), "The copy lost a document.");
    }

    [Fact]
    public async Task StopAndDispose_WhileTheWatchHasNotEnded_CompleteStarted_AndKeepTheCopyLockedUntilItHas()
    {
        MadeUp().Create("a1");
        using var console = new HeldConsole();
        using var data = new KilerData(_place, FileOwner.Instance, TimeProvider.System, console, _errors);
        data.Start();
        await console.Held.WaitAsync(Quick, Cancel);

        // The copy is open, but its first load has not ended.
        Assert.NotNull(data.Copy);
        Assert.Equal(DataState.Opening, data.State);

        // The thread of the watch stays in the event of its first load, as in a load that outlives the stop of the host.
        data.Stop(TimeSpan.FromMilliseconds(100));
        Assert.True(data.Started.IsCompleted, "The start did not end with the stop.");
        data.Dispose();
        Assert.True(IsLocked(), "The copy was let go while the watch still ran.");

        console.Release();
        for (var waited = Stopwatch.StartNew(); IsLocked(); data.Dispose())
        {
            Assert.True(waited.Elapsed < Quick, "The copy stayed locked after the watch had ended.");
            await Task.Delay(TimeSpan.FromMilliseconds(50), Cancel);
        }
    }

    [Fact]
    public async Task ADataFolderThatGoesAndComesBack_IsSaidOfflineAndBack()
    {
        MadeUp().Create("a1");
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        using var data = NewData(clock);
        data.Start();
        await data.Started.WaitAsync(Quick, Cancel);

        var away = $"{_place.DataFolder}-away";
        Directory.Move(_place.DataFolder, away);
        clock.Advance(DataWatch.Interval);
        await _output.WaitForLineAsync("The data folder cannot be reached, so Kiler works from its local copy: ", Quick);
        await WaitForStateAsync(data, DataState.Offline);

        Directory.Move(away, _place.DataFolder);
        clock.Advance(DataWatch.Interval);
        await _output.WaitForLineAsync("The data folder can be reached again.", Quick);
        await WaitForStateAsync(data, DataState.Open);
        Assert.Empty(_errors.Lines);
    }

    [Fact]
    public async Task Start_WithAFileWhereTheDataFolderIs_RunsWithoutData_AndSaysSo()
    {
        File.WriteAllText(_place.DataFolder, "Not a data folder.");
        using var data = NewData(TimeProvider.System);
        data.Start();
        await data.Started.WaitAsync(Quick, Cancel);

        Assert.Null(data.Copy);
        Assert.Equal(DataState.Unusable, data.State);
        Assert.StartsWith("Kiler runs without data: the data folder cannot be used: ", Assert.Single(_errors.Lines), StringComparison.Ordinal);
        Assert.False(Directory.Exists(_place.LocalFolder), "A local copy was made.");
    }

    [Fact]
    public async Task Start_WhenTheFolderOfTheCopyCannotBeMade_RunsWithoutData_AndSaysSo()
    {
        MadeUp().Create("a1");
        Directory.CreateDirectory(_place.CopiesFolder);
        File.WriteAllText(_place.CopyFolder, "Not a folder.");
        using var data = NewData(TimeProvider.System);
        data.Start();
        await data.Started.WaitAsync(Quick, Cancel);

        Assert.Null(data.Copy);
        Assert.Equal(DataState.Failed, data.State);
        Assert.StartsWith("Kiler runs without data: its local copy cannot be made or locked: ", Assert.Single(_errors.Lines), StringComparison.Ordinal);
        Assert.Empty(_output.Lines);
    }

    [Fact]
    public async Task Start_WithoutADataFolder_RunsWithoutData_AndSaysNothing()
    {
        using var data = new KilerData(null, FileOwner.Instance, TimeProvider.System, _output, _errors);
        Assert.Equal(DataState.NoFolder, data.State);

        data.Start();
        await data.Started.WaitAsync(Quick, Cancel);

        Assert.Equal(DataState.NoFolder, data.State);
        Assert.Null(data.Folder);
        Assert.Null(data.Copy);
        Assert.Null(data.Team);
        Assert.Empty(_output.Lines);
        Assert.Empty(_errors.Lines);
    }

    [Fact]
    public async Task TheTeam_IsReadWhenTheCopyOpens_AndFollowsTheLoads()
    {
        var made = MadeUp();
        made.Create(UserFile.KeyOf("demo.user"), User("demo.user"));
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        using var data = NewData(clock);
        Assert.Equal(DataState.Opening, data.State);
        data.Start();
        await data.Started.WaitAsync(Quick, Cancel);
        var team = Assert.IsType<Team>(data.Team);
        Assert.Equal(["demo.user"], team.Files.Users.Keys);

        made.Create(UserFile.KeyOf("another.user"), User("another.user"));
        clock.Advance(DataWatch.Interval);
        await _output.WaitForLineAsync($"{FirstLoad}1 new or changed, 0 gone, 0 changed outside Kiler, 0 skipped.", Quick);

        Assert.Equal(["another.user", "demo.user"], team.Files.Users.Keys);
        Assert.Empty(_errors.Lines);
    }

    [Fact]
    public async Task TheRecords_FollowTheLoads_AndTheDispose_DropsThem()
    {
        var made = MadeUp();
        made.Create(StashFile.KeyOf("a1b2c3d4"), Stash("a1b2c3d4", "Payments"));
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        using var data = NewData(clock);
        data.Start();
        await data.Started.WaitAsync(Quick, Cancel);

        // A new local copy has nothing before its first load, so the records have what the loads gave them (M5.6).
        var records = Assert.IsType<Records>(data.Records);
        Assert.Equal(["a1b2c3d4"], records.Read(null).Stashes.Keys);

        made.Create(StashFile.KeyOf("e5f6g7h8"), Stash("e5f6g7h8", "Prod"));
        clock.Advance(DataWatch.Interval);
        await _output.WaitForLineAsync($"{FirstLoad}1 new or changed, 0 gone, 0 changed outside Kiler, 0 skipped.", Quick);
        Assert.Equal(["a1b2c3d4", "e5f6g7h8"], records.Read(null).Stashes.Keys);

        // After the server has stopped, as in Program.
        data.Dispose();
        Assert.Empty(records.Read(null).Stashes);
        Assert.Empty(_errors.Lines);
    }

    private static JsonObject Stash(string id, string name) =>
        new StashFile(id, name, new Authorship("demo.admin", new DateTime(2026, 10, 30, 8, 0, 0, DateTimeKind.Utc), "demo.admin", new DateTime(2026, 10, 30, 8, 0, 0, DateTimeKind.Utc))).ToContent();

    private static JsonObject User(string account) =>
        new UserFile(account, "Demo User", level: 1, canEdit: false, isAdmin: false, mustChangePassword: false, new LockedKeys("salt", new Dictionary<int, string> { [1] = "locked-1" })).ToContent();

    /// <summary>Waits for a state that a handler of the watch sets after its line.</summary>
    private static async Task WaitForStateAsync(KilerData data, DataState state)
    {
        for (var waited = Stopwatch.StartNew(); data.State != state; await Task.Delay(TimeSpan.FromMilliseconds(20), Cancel))
        {
            Assert.True(waited.Elapsed < Quick, $"The state stayed {data.State}, not {state}.");
        }
    }

    private MadeUpData MadeUp() => MadeUpData.Create(_place.DataFolder, Path.Combine(_folder.Path, "writer"));

    private KilerData NewData(TimeProvider time) => new(_place, FileOwner.Instance, time, _output, _errors);

    private Task<KilerServer> StartServerAsync(KilerData data) =>
        KilerServer.StartAsync(KilerHost.Build(new StartOptions { DataFolder = _place.DataFolder }, data: data), _place.DataFolder);

    /// <summary>Whether a Kiler holds the lock of the local copy: the file then cannot be opened without sharing.</summary>
    private bool IsLocked()
    {
        try
        {
            using var held = new FileStream(Path.Combine(_place.CopyFolder, KilerData.LockFile), FileMode.Open, FileAccess.Read, FileShare.None);
            return false;
        }
        catch (IOException ex) when ((ex.HResult & 0xFFFF) == 32)
        {
            return true;
        }
    }

    /// <summary>A console whose line of a load waits until the test lets it go, so the thread of the watch stays in the event.</summary>
    private sealed class HeldConsole : TextWriter
    {
        private readonly ManualResetEventSlim _go = new();
        private readonly TaskCompletionSource _held = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override Encoding Encoding => Encoding.UTF8;

        /// <summary>Completes when a line of a load waits.</summary>
        public Task Held => _held.Task;

        public override void WriteLine(string? value)
        {
            if (value is not null && value.StartsWith(FirstLoad, StringComparison.Ordinal))
            {
                _held.TrySetResult();
                _go.Wait();
            }
        }

        public override void Write(char value) => throw new NotSupportedException("Kiler writes whole lines.");

        public void Release() => _go.Set();

        // The event is not disposed: the thread of the watch can still be waking from it.
        protected override void Dispose(bool disposing)
        {
            _go.Set();
            base.Dispose(disposing);
        }
    }
}

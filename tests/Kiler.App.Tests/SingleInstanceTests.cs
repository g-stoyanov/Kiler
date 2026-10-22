using Microsoft.Extensions.Time.Testing;

namespace Kiler.App.Tests;

/// <summary>
/// The decision "is there a running Kiler" with a fake lock, a fake health check and a fake clock. The real second start
/// is tested in M1.9. With no retry delay the decision runs without waiting, and each fake call moves the clock on.
/// </summary>
public sealed class SingleInstanceTests : IDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private readonly TempFolder _folder = new();
    private readonly FakeTimeProvider _time = new();
    private readonly InstanceFile _file;

    public SingleInstanceTests() => _file = new InstanceFile(Path.Combine(_folder.Path, "instance.json"));

    public void Dispose() => _folder.Dispose();

    [Fact]
    public async Task Decide_NoOtherKilerAndNoFile_StartsTheServer()
    {
        var instanceLock = new FakeLock(_time, free: true);
        var health = new FakeHealth(_time);

        var decision = await DecideAsync(instanceLock, health);

        Assert.IsType<StartDecision.StartServer>(decision);
        Assert.Equal(1, instanceLock.Calls);
    }

    [Fact]
    public async Task Decide_FirstStartWithoutTheKilerFolder_StartsTheServer()
    {
        var file = new InstanceFile(Path.Combine(_folder.Path, "Kiler", "instance.json"));

        var decision = await new SingleInstance(new FakeLock(_time, free: true), file, new FakeHealth(_time), _time)
            .DecideAsync(Wait, TimeSpan.Zero, TestContext.Current.CancellationToken);

        Assert.IsType<StartDecision.StartServer>(decision);
    }

    [Fact]
    public async Task Decide_StaleFileFromAStoppedKiler_StartsTheServerAndDeletesTheFileWithoutAskingItsPort()
    {
        _file.Write(new RunningInstance(51234, new AccessToken()));
        var health = new FakeHealth(_time, answers: true);

        var decision = await DecideAsync(new FakeLock(_time, free: true), health);

        Assert.IsType<StartDecision.StartServer>(decision);
        Assert.Empty(health.Asked);
        Assert.False(File.Exists(Path.Combine(_folder.Path, "instance.json")));
    }

    [Fact]
    public async Task Decide_StaleFileThatCannotBeDeleted_DoesNotStartAndAsksNoPort()
    {
        var path = Path.Combine(_folder.Path, "instance.json");
        _file.Write(new RunningInstance(51234, new AccessToken()));
        var health = new FakeHealth(_time, answers: true);

        StartDecision decision;
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            decision = await DecideAsync(new FakeLock(_time, free: true), health);
        }

        Assert.IsType<StartDecision.StaleFileStays>(decision);
        Assert.Empty(health.Asked);
    }

    [Fact]
    public async Task Decide_StaleFileThatAReaderHolds_IsStillDeleted()
    {
        // TryRead opens the file this way, so a second start that reads it does not keep the holder of the lock from deleting it.
        var path = Path.Combine(_folder.Path, "instance.json");
        _file.Write(new RunningInstance(51234, new AccessToken()));

        StartDecision decision;
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            decision = await DecideAsync(new FakeLock(_time, free: true), new FakeHealth(_time));
        }

        Assert.IsType<StartDecision.StartServer>(decision);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task Decide_LiveKiler_OpensItWithThePortAndTheTokenFromTheFile()
    {
        var running = new RunningInstance(51234, new AccessToken());
        _file.Write(running);
        var health = new FakeHealth(_time, answers: true);

        var decision = await DecideAsync(new FakeLock(_time, free: false), health);

        var open = Assert.IsType<StartDecision.OpenRunning>(decision);
        Assert.Equal(51234, open.Instance.Port);
        Assert.Equal(running.Token.Value, open.Instance.Token.Value);
        var asked = Assert.Single(health.Asked);
        Assert.Equal(51234, asked.Port);
        Assert.Equal(running.Token.Value, asked.Token.Value);
        Assert.True(File.Exists(Path.Combine(_folder.Path, "instance.json")));
    }

    [Fact]
    public async Task Decide_KilerThatDoesNotAnswer_GivesNoAnswerAfterTheWait()
    {
        _file.Write(new RunningInstance(51234, new AccessToken()));
        var health = new FakeHealth(_time, answers: false, takes: TimeSpan.FromSeconds(1));

        var decision = await DecideAsync(new FakeLock(_time, free: false, takes: TimeSpan.Zero), health);

        Assert.IsType<StartDecision.NoAnswer>(decision);
        Assert.Equal(10, health.Asked.Count);
        Assert.True(File.Exists(Path.Combine(_folder.Path, "instance.json")));
    }

    [Fact]
    public async Task Decide_LockHeldButNoFile_GivesNoAnswerAfterTheWaitWithoutAskingAnyPort()
    {
        var instanceLock = new FakeLock(_time, free: false);
        var health = new FakeHealth(_time, answers: true);

        var decision = await DecideAsync(instanceLock, health);

        Assert.IsType<StartDecision.NoAnswer>(decision);
        Assert.Empty(health.Asked);
        Assert.Equal(40, instanceLock.Calls);
    }

    [Fact]
    public async Task Decide_LockHeldAndTheFileIsNotValid_GivesNoAnswerWithoutAskingAnyPort()
    {
        File.WriteAllText(Path.Combine(_folder.Path, "instance.json"), """{"port":51234,"token":"short"}""");
        var health = new FakeHealth(_time, answers: true);

        var decision = await DecideAsync(new FakeLock(_time, free: false), health);

        Assert.IsType<StartDecision.NoAnswer>(decision);
        Assert.Empty(health.Asked);
    }

    [Fact]
    public async Task Decide_KilerThatIsStarting_OpensItOnceItsFileIsWritten()
    {
        var running = new RunningInstance(51234, new AccessToken());
        var instanceLock = new FakeLock(_time, free: false)
        {
            OnCall = call =>
            {
                if (call == 3)
                {
                    _file.Write(running);
                }
            },
        };

        var decision = await DecideAsync(instanceLock, new FakeHealth(_time, answers: true));

        var open = Assert.IsType<StartDecision.OpenRunning>(decision);
        Assert.Equal(running.Token.Value, open.Instance.Token.Value);
        Assert.Equal(3, instanceLock.Calls);
    }

    [Fact]
    public async Task Decide_KilerThatIsStopping_StartsTheServerOnceTheLockIsFree()
    {
        _file.Write(new RunningInstance(51234, new AccessToken()));
        var instanceLock = new FakeLock(_time, free: false);
        instanceLock.OnCall = call => instanceLock.Free = call >= 3;

        var decision = await DecideAsync(instanceLock, new FakeHealth(_time, answers: false));

        Assert.IsType<StartDecision.StartServer>(decision);
        Assert.Equal(3, instanceLock.Calls);
        Assert.False(File.Exists(Path.Combine(_folder.Path, "instance.json")));
    }

    [Fact]
    public async Task Decide_BetweenTries_WaitsTheRetryDelayOnTheClock()
    {
        var instanceLock = new FakeLock(_time, free: false, takes: TimeSpan.Zero);
        var decide = new SingleInstance(instanceLock, _file, new FakeHealth(_time), _time)
            .DecideAsync(Wait, TimeSpan.FromMilliseconds(250), TestContext.Current.CancellationToken);

        Assert.False(decide.IsCompleted);
        Assert.Equal(1, instanceLock.Calls);
        _time.Advance(TimeSpan.FromMilliseconds(249));
        Assert.Equal(1, instanceLock.Calls);

        instanceLock.Free = true;
        _time.Advance(TimeSpan.FromMilliseconds(1));

        Assert.IsType<StartDecision.StartServer>(await decide.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Assert.Equal(2, instanceLock.Calls);
    }

    [Fact]
    public async Task Decide_CancelledWhileWaiting_Throws()
    {
        using var cancel = new CancellationTokenSource();
        var decide = new SingleInstance(new FakeLock(_time, free: false, takes: TimeSpan.Zero), _file, new FakeHealth(_time), _time)
            .DecideAsync(Wait, TimeSpan.FromMilliseconds(250), cancel.Token);

        cancel.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => decide.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
    }

    [Fact]
    public void DefaultTimes_WaitTenSecondsAndTryFourTimesASecond()
    {
        Assert.Equal(TimeSpan.FromSeconds(10), SingleInstance.Wait);
        Assert.Equal(TimeSpan.FromMilliseconds(250), SingleInstance.RetryDelay);
    }

    private Task<StartDecision> DecideAsync(FakeLock instanceLock, FakeHealth health) =>
        new SingleInstance(instanceLock, _file, health, _time)
            .DecideAsync(Wait, TimeSpan.Zero, TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

    /// <summary>A lock that is free or held as the test says. Each call moves the clock on, as a real retry delay would.</summary>
    private sealed class FakeLock(FakeTimeProvider time, bool free, TimeSpan? takes = null) : IInstanceLock
    {
        public bool Free { get; set; } = free;

        public int Calls { get; private set; }

        /// <summary>Runs on each call with its number, before the answer.</summary>
        public Action<int>? OnCall { get; set; }

        public Task<bool> TryAcquireAsync()
        {
            Calls++;
            OnCall?.Invoke(Calls);
            time.Advance(takes ?? TimeSpan.FromMilliseconds(250));
            return Task.FromResult(Free);
        }
    }

    /// <summary>A health check with a fixed answer that records what it was asked and moves the clock on.</summary>
    private sealed class FakeHealth(FakeTimeProvider time, bool answers = false, TimeSpan? takes = null) : IHealthCheck
    {
        public List<RunningInstance> Asked { get; } = [];

        public Task<bool> AnswersAsync(RunningInstance instance, CancellationToken cancellationToken)
        {
            Asked.Add(instance);
            time.Advance(takes ?? TimeSpan.Zero);
            return Task.FromResult(answers);
        }
    }
}

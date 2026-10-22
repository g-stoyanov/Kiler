namespace Kiler.App.Tests;

/// <summary>The real named mutex. Each test has a name of its own, so a Kiler that runs on this machine does not count.</summary>
public class InstanceMutexTests
{
    private readonly string _name = $"Kiler.Tests.{Guid.NewGuid():N}";

    [Fact]
    public async Task TryAcquire_WhenNoOneHoldsIt_IsTrue_AndStaysTrue()
    {
        using var instanceLock = new InstanceMutex(_name);

        Assert.True(await instanceLock.TryAcquireAsync());
        Assert.True(await instanceLock.TryAcquireAsync());
    }

    [Fact]
    public async Task TryAcquire_WhileAnotherHoldsIt_IsFalse_UntilTheOtherIsDisposed()
    {
        var first = new InstanceMutex(_name);
        using var second = new InstanceMutex(_name);
        try
        {
            Assert.True(await first.TryAcquireAsync());
            Assert.False(await second.TryAcquireAsync());
            Assert.False(await second.TryAcquireAsync());
        }
        finally
        {
            first.Dispose();
        }

        Assert.True(await second.TryAcquireAsync());
    }

    [Fact]
    public async Task TryAcquire_HoldsTheMutexOnAThreadOfItsOwn_NotOnTheCallingThread()
    {
        // The calling thread may be a thread pool thread, which ends when idle and would release the mutex. A mutex is
        // reentrant for its owner, so a hold by the calling thread shows as a successful wait on that thread. The test
        // pins the calling thread and blocks on it, because an await would move the check to another thread.
        using var instanceLock = new InstanceMutex(_name);

        var (acquired, reentered) = await Task.Factory.StartNew(
            () =>
            {
                var acquired = instanceLock.TryAcquireAsync().GetAwaiter().GetResult();
                using var mutex = new Mutex(false, _name, new NamedWaitHandleOptions { CurrentUserOnly = true, CurrentSessionOnly = true });
                var reentered = mutex.WaitOne(0);
                if (reentered)
                {
                    mutex.ReleaseMutex();
                }

                return (acquired, reentered);
            },
            TestContext.Current.CancellationToken,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

        Assert.True(acquired);
        Assert.False(reentered);
    }

    [Fact]
    public async Task TryAcquire_AfterTheOwnerEndedWithoutReleasingIt_IsTrue()
    {
        // A Kiler that crashed: its thread ends and leaves the mutex abandoned. The open handle keeps the mutex alive.
        using var mutex = new Mutex(false, _name, new NamedWaitHandleOptions { CurrentUserOnly = true, CurrentSessionOnly = true });
        var owner = new Thread(() => mutex.WaitOne());
        owner.Start();
        owner.Join();

        using var instanceLock = new InstanceMutex(_name);

        Assert.True(await instanceLock.TryAcquireAsync());
    }
}

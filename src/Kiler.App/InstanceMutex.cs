namespace Kiler.App;

/// <summary>The lock that the running Kiler holds while it runs.</summary>
internal interface IInstanceLock
{
    /// <summary>True when this process holds the lock, now or from before: no other Kiler of the user runs with the same data.</summary>
    Task<bool> TryAcquireAsync();
}

/// <summary>
/// A named mutex of the current user in the current session, the defaults of <see cref="NamedWaitHandleOptions"/>.
/// Another user cannot open it. A mutex of the same name that another user made first is refused, so Kiler does not
/// start, and it neither reads the file nor contacts a port. Windows releases the mutex when the process ends, also on
/// a crash, so a Kiler that stopped never blocks the next one.
/// </summary>
internal sealed class InstanceMutex(string name) : IInstanceLock, IDisposable
{
    private readonly ManualResetEventSlim _release = new();
    private Thread? _holder;
    private bool _disposed;

    /// <summary>One call at a time.</summary>
    public Task<bool> TryAcquireAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_holder is not null)
        {
            return Task.FromResult(true);
        }

        // A mutex belongs to the thread that took it, and Windows releases it when that thread ends. After an await the
        // code runs on thread pool threads, which end when they are idle, so the mutex is taken and held on a thread of its own.
        var acquired = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        new Thread(() => Hold(acquired)) { IsBackground = true, Name = "Kiler instance lock" }.Start();
        return acquired.Task;
    }

    /// <summary>
    /// Releases the mutex. A try that has not finished yet sees the event disposed, and its thread ends; Windows then
    /// releases the mutex it may have taken.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _release.Set();
        _holder?.Join();
        _release.Dispose();
    }

    private void Hold(TaskCompletionSource<bool> acquired)
    {
        try
        {
            using var mutex = new Mutex(initiallyOwned: false, name, new NamedWaitHandleOptions { CurrentUserOnly = true, CurrentSessionOnly = true });
            if (!TryTake(mutex))
            {
                acquired.SetResult(false);
                return;
            }

            _holder = Thread.CurrentThread;
            acquired.SetResult(true);
            _release.Wait();
            mutex.ReleaseMutex();
        }
        // After the result, only the release can fail, and the end of the process releases the mutex anyway.
        catch (Exception ex)
        {
            acquired.TrySetException(ex);
        }
    }

    private static bool TryTake(Mutex mutex)
    {
        try
        {
            return mutex.WaitOne(0);
        }
        // The last owner ended without releasing it: a Kiler that crashed or was stopped. The mutex now belongs to this thread.
        catch (AbandonedMutexException)
        {
            return true;
        }
    }
}

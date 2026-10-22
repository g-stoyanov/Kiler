namespace Kiler.App;

/// <summary>What a start of Kiler does.</summary>
internal abstract record StartDecision
{
    private StartDecision()
    {
    }

    /// <summary>No other Kiler runs: this process holds the lock and starts the server.</summary>
    public sealed record StartServer : StartDecision;

    /// <summary>Another process runs Kiler and answers: this start opens the browser to it and ends.</summary>
    public sealed record OpenRunning(RunningInstance Instance) : StartDecision;

    /// <summary>Another process holds the lock, but it did not answer in time.</summary>
    public sealed record NoAnswer : StartDecision;

    /// <summary>This process holds the lock, but the file of an earlier run cannot be deleted, so it does not start.</summary>
    public sealed record StaleFileStays : StartDecision;
}

/// <summary>
/// One Kiler per user, session and data folder. The lock says whether a Kiler runs, and <c>instance.json</c> says where.
/// The file is read only while another process holds the lock. Once the lock is free, the process that wrote the file
/// has stopped, its port is free for any program, and a request there would hand the token over.
/// </summary>
internal sealed class SingleInstance(IInstanceLock instanceLock, InstanceFile file, IHealthCheck health, TimeProvider time)
{
    /// <summary>How long a start waits for a Kiler that holds the lock but does not answer: it may be starting or stopping.</summary>
    public static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    public static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(250);

    /// <summary>How many times the holder of the lock tries to delete a file left over, with the retry delay between the tries.</summary>
    public const int DeleteTries = 5;

    public Task<StartDecision> DecideAsync(CancellationToken cancellationToken) => DecideAsync(Wait, RetryDelay, cancellationToken);

    public async Task<StartDecision> DecideAsync(TimeSpan wait, TimeSpan retryDelay, CancellationToken cancellationToken)
    {
        var started = time.GetTimestamp();
        while (true)
        {
            if (await instanceLock.TryAcquireAsync())
            {
                // A file left over from a Kiler that crashed. It must go before the server starts: while this process
                // holds the lock, the next start trusts the file. This process writes a new one once its server listens.
                for (var tries = 1; !file.TryDelete(); tries++)
                {
                    if (tries == DeleteTries)
                    {
                        return new StartDecision.StaleFileStays();
                    }

                    await Task.Delay(retryDelay, time, cancellationToken);
                }

                return new StartDecision.StartServer();
            }

            // The running Kiler writes the file once its server listens, so before that there is no file yet.
            if (file.TryRead() is { } running && await health.AnswersAsync(running, cancellationToken))
            {
                return new StartDecision.OpenRunning(running);
            }

            if (time.GetElapsedTime(started) >= wait)
            {
                return new StartDecision.NoAnswer();
            }

            await Task.Delay(retryDelay, time, cancellationToken);
        }
    }
}

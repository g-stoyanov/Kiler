using System.Text;

namespace Kiler.App.Tests;

/// <summary>A writer that keeps the lines written to it, safe across threads, for the tests of what Kiler says on its console.</summary>
internal sealed class LineWriter : TextWriter
{
    private readonly Lock _gate = new();
    private readonly List<string> _lines = [];
    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public override Encoding Encoding => Encoding.UTF8;

    public IReadOnlyList<string> Lines
    {
        get
        {
            lock (_gate)
            {
                return [.. _lines];
            }
        }
    }

    public override void WriteLine(string? value)
    {
        TaskCompletionSource changed;
        lock (_gate)
        {
            _lines.Add(value ?? string.Empty);
            changed = _changed;
            _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        changed.TrySetResult();
    }

    /// <summary>Kiler writes whole lines, so anything else is a fault of the test or of the code.</summary>
    public override void Write(char value) => throw new NotSupportedException("Kiler writes whole lines.");

    /// <summary>Waits for a line that starts with <paramref name="prefix"/>, and gives it; a <see cref="TimeoutException"/> after <paramref name="timeout"/>.</summary>
    public async Task<string> WaitForLineAsync(string prefix, TimeSpan timeout)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        limit.CancelAfter(timeout);
        while (true)
        {
            Task changed;
            lock (_gate)
            {
                if (_lines.FirstOrDefault(line => line.StartsWith(prefix, StringComparison.Ordinal)) is { } found)
                {
                    return found;
                }

                changed = _changed.Task;
            }

            try
            {
                await changed.WaitAsync(limit.Token);
            }
            catch (OperationCanceledException) when (!TestContext.Current.CancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"No line that starts with \"{prefix}\" in {timeout}. The lines: {string.Join(" | ", Lines)}");
            }
        }
    }
}

using System.Runtime.ExceptionServices;

namespace Kiler.Storage;

/// <summary>
/// Runs one piece of work for each item, some of them at once, on threads of their own and the calling thread (decided in
/// M2.7). The reads of a load block on the network, and the thread pool adds threads for work that blocks only slowly, about
/// one every 0.5 s above the number of cores, while Kestrel shares it. The threads end with the work.
/// </summary>
internal static class Workers
{
    /// <summary>
    /// The results of <paramref name="work"/> for the items, in their order, with at most <paramref name="atOnce"/> of them
    /// at a time. An exception of the work stops the items that have not started and is thrown once every thread has ended;
    /// work that expects an error for one item gives it as its result.
    /// </summary>
    public static TResult[] Run<T, TResult>(IReadOnlyList<T> items, int atOnce, Func<T, TResult> work)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(work);
        ArgumentOutOfRangeException.ThrowIfLessThan(atOnce, 1);
        var batch = new Batch<T, TResult>(items, work);
        var threads = new List<Thread>();
        try
        {
            // The calling thread is one of them.
            for (var started = 1; started < Math.Min(atOnce, items.Count); started++)
            {
                var thread = new Thread(batch.Work) { IsBackground = true, Name = "Kiler load" };
                thread.Start();
                threads.Add(thread);
            }

            batch.Work();
        }
        finally
        {
            foreach (var thread in threads)
            {
                thread.Join();
            }
        }

        return batch.Results();
    }

    /// <summary>The items of one run, which each thread takes the next of, and their results.</summary>
    private sealed class Batch<T, TResult>(IReadOnlyList<T> items, Func<T, TResult> work)
    {
        private readonly TResult[] _results = new TResult[items.Count];
        private int _next = -1;
        private ExceptionDispatchInfo? _failure;

        public void Work()
        {
            try
            {
                int index;
                while (Volatile.Read(ref _failure) is null && (index = Interlocked.Increment(ref _next)) < items.Count)
                {
                    _results[index] = work(items[index]);
                }
            }
            catch (Exception ex)
            {
                // Thrown on the calling thread by Results, once every thread has ended.
                Interlocked.CompareExchange(ref _failure, ExceptionDispatchInfo.Capture(ex), null);
            }
        }

        public TResult[] Results()
        {
            _failure?.Throw();
            return _results;
        }
    }
}

namespace CouchbaseInsight.Services;

/// <summary>
/// Cuts a pass over a collection into key ranges of a fixed number of documents and works on
/// several at once.
///
/// Range k is (after, end]: <c>after</c> is the end of range k-1 ("" for the first), and
/// <c>end</c> is the batch-size-th key after it, found by a keys-only query on the primary index.
/// Ranges meet without overlapping, so every key is in exactly one.
///
/// Without a cap, the last range has no end and runs to the end of the collection, so documents
/// added after the count are still covered. With a cap (the scan limit), the ranges cover exactly
/// the first <c>cap</c> keys, the last one shortened to land on it. A count that is out of date
/// only changes how many ranges there are, never which keys are covered.
///
/// Finding the next cut point and working on the ranges already found overlap: up to
/// <c>parallel</c> ranges are worked on while the next end key is being looked up.
/// </summary>
public static class RangePlanner
{
    /// <param name="count">The collection's document count, taken before the first range.</param>
    /// <param name="cap">Cover only the first this-many keys, or null for all of them.</param>
    /// <param name="findEndKey">(after, n) → the n-th key after <c>after</c>, or null if fewer than n remain.</param>
    /// <param name="work">Handles one range (end null: to the end of the collection).</param>
    /// <param name="rangeDone">Called after each range with (ranges done, ranges expected).</param>
    /// <param name="pause">Wait this long after each range before starting another in its place.</param>
    public static async Task RunAsync(
        long count, long? cap, int batchSize, int parallel,
        Func<string, int, Task<string?>> findEndKey,
        Func<string, string?, Task> work,
        Action<int, int> rangeDone,
        CancellationToken ct,
        TimeSpan pause = default)
    {
        if (batchSize <= 0) throw new ArgumentOutOfRangeException(nameof(batchSize));

        // A cap at or above the collection's size reads all of it: treat it as no cap, so the
        // last range still runs to the end instead of stopping at a count that may be stale.
        if (cap is { } c && c >= count) cap = null;
        var total = cap ?? count;

        var expected = Math.Max(1, (int)Math.Ceiling(total / (double)batchSize));
        using var gate = new SemaphoreSlim(Math.Max(1, parallel));
        var running = new List<Task>();
        var done = 0;
        var doneLock = new object();

        var after = "";
        long covered = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();

            // Stop handing out ranges once one has failed; WhenAll below rethrows its error.
            if (running.Any(t => t.IsFaulted || t.IsCanceled)) break;

            var size = (int)Math.Min(batchSize, Math.Max(0, total - covered));
            var last = covered + size >= total;

            string? end = null;
            if (cap is not null)
            {
                if (size > 0) end = await findEndKey(after, size);   // exact end, the last one on the cap
            }
            else if (!last)
            {
                end = await findEndKey(after, size);                 // the last range runs open-ended
            }

            await gate.WaitAsync(ct);
            var (a, e) = (after, end);
            running.Add(Task.Run(async () =>
            {
                try
                {
                    await work(a, e);
                    int n;
                    lock (doneLock) n = ++done;
                    rangeDone(n, Math.Max(expected, n));

                    // Gentle mode: give the cluster a breather before this slot takes the next range.
                    if (pause > TimeSpan.Zero) await Task.Delay(pause, ct);
                }
                finally
                {
                    gate.Release();
                }
            }, ct));

            covered += size;
            if (end is null || (cap is not null && last)) break;
            after = end;
        }

        await Task.WhenAll(running);
    }
}

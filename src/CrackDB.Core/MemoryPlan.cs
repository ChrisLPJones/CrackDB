namespace CrackDB;

/// <summary>
/// How much memory one open <see cref="WordlistDatabase"/> may use, derived from a single budget:
/// RocksDB memtables (60%), the import pipeline's in-flight chunks (15%), the rest is headroom
/// (flush sort copy, GC, the web app, read caches). Pure maths so it is unit-testable without a DB.
/// On a big machine this reproduces the original hardcoded tuning (512 MB x 8 buffers, 8 workers, queue 24).
/// </summary>
public readonly record struct MemoryPlan(
    ulong WriteBuffer, int MaxBuffers, ulong DbWriteBufferCap,
    int Flushes, int Workers, int QueueCapacity, int ChunkChars)
{
    private const long MiB = 1024 * 1024;
    private const long MinBudget = 256 * MiB;
    private const long ChunkEstimateBytes = 8 * MiB;   // 4M chars of strings + write batch + slop, per in-flight chunk
    public const int DefaultChunkChars = 4_000_000;

    /// <summary>
    /// 50% of the memory actually available to this process right now (honors cgroup/LXC limits),
    /// at least 256 MB. Uses <see cref="SystemMemory.AvailableBytesReader"/> - the same cross-checked
    /// (GC info vs. <c>/proc/meminfo</c>) primitive <c>CrackDB.Web.Jobs.HeadroomGuard</c> uses - so the
    /// auto-detected budget and the headroom safety check never disagree because they read from
    /// different sources. An ambiguous/unreadable reading falls back to the 256 MB floor rather than
    /// guessing a bigger budget.
    /// </summary>
    public static long DefaultBudgetBytes()
    {
        long? avail = SystemMemory.AvailableBytesReader();
        if (avail is null) return MinBudget;
        return Math.Max(MinBudget, avail.Value / 2);
    }

    public static MemoryPlan Compute(long budgetBytes, int procs, int requestedThreads = 0)
    {
        double b = Math.Max(1, budgetBytes);
        procs = Math.Max(1, procs);

        long memtable = (long)(b * 0.6);
        long writeBuffer = Math.Clamp(memtable / 4, 16 * MiB, 512 * MiB);
        int maxBuffers = (int)Math.Clamp(memtable / writeBuffer, 2, 8);
        int flushes = Math.Min(Math.Max(2, procs / 4), Math.Max(1, maxBuffers / 2));

        // Pipeline: workers + queued chunks + the one being filled all hold a chunk.
        int inflightMax = (int)Math.Max(4, (long)(b * 0.15) / ChunkEstimateBytes);
        int workers = requestedThreads > 0 ? requestedThreads : Math.Clamp(procs - 1, 2, 8);
        workers = Math.Min(workers, Math.Min(Math.Max(1, procs - 1), 8));
        workers = Math.Max(1, Math.Min(workers, Math.Max(1, inflightMax / 2)));
        int queue = Math.Max(2, Math.Min(workers * 3, inflightMax - workers - 1));

        return new MemoryPlan((ulong)writeBuffer, maxBuffers, (ulong)memtable, flushes, workers, queue, DefaultChunkChars);
    }
}

namespace CrackDB.Web.Jobs;

/// <summary>
/// Cheap safety net checked before each queued job starts: refuses to start a job if free memory
/// would leave less than a safety margin above the configured import budget, instead of letting the
/// OOM killer pick a victim. This is a second, independent backstop - it supplements
/// <c>Storage:ImportMemoryMb</c>/<see cref="CrackDB.MemoryPlan"/>, it does not replace it, and it is never
/// driven by anything client-supplied (the budget comes from operator config; the margin is fixed).
///
/// Reads available memory via <see cref="CrackDB.SystemMemory.AvailableBytesReader"/> - the same
/// cross-checked (GC info vs. <c>/proc/meminfo</c>) primitive <see cref="CrackDB.MemoryPlan.DefaultBudgetBytes"/>
/// also uses - so the auto-detected import budget and this headroom check always agree on the same
/// number instead of being computed from different sources. Fails closed: any ambiguous or
/// unreadable reading refuses the job rather than assuming there is room.
/// </summary>
public static class HeadroomGuard
{
    /// <summary>Required free memory above the configured import budget before a job may start.</summary>
    public const long DefaultMarginBytes = 1024L * 1024 * 1024;   // ~1 GB

    /// <summary>Test seam: override how "bytes currently available" is read. Resets are the test's job.</summary>
    public static Func<long?> AvailableBytesReader { get; set; } = () => CrackDB.SystemMemory.AvailableBytesReader();

    /// <summary>
    /// True if there is enough headroom to start a job needing <paramref name="budgetBytes"/> (plus
    /// <paramref name="marginBytes"/>). False (with a human-readable <paramref name="reason"/>,
    /// no paths or data) when there isn't, or when the memory reading itself is unavailable/ambiguous.
    /// </summary>
    public static bool HasHeadroom(long budgetBytes, out string? reason, long marginBytes = DefaultMarginBytes)
    {
        long? available = AvailableBytesReader();
        if (available is null)
        {
            reason = "Could not read available memory; refusing to start the job to avoid an out-of-memory crash.";
            return false;
        }

        long required = budgetBytes + marginBytes;
        if (available.Value < required)
        {
            reason = $"Not enough free memory to start safely (need at least ~{required / (1024 * 1024)} MB free, " +
                      $"have ~{available.Value / (1024 * 1024)} MB). Try again after the next compaction or once other jobs free memory.";
            return false;
        }

        reason = null;
        return true;
    }
}

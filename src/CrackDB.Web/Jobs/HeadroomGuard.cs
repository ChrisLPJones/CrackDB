using System.Globalization;

namespace CrackDB.Web.Jobs;

/// <summary>
/// Cheap safety net checked before each queued job starts: refuses to start a job if free memory
/// would leave less than a safety margin above the configured import budget, instead of letting the
/// OOM killer pick a victim. This is a second, independent backstop - it supplements
/// <c>Storage:ImportMemoryMb</c>/<see cref="CrackDB.MemoryPlan"/>, it does not replace it, and it is never
/// driven by anything client-supplied (the budget comes from operator config; the margin is fixed).
///
/// Reads available memory via the same cgroup/LXC-aware primitive <see cref="CrackDB.MemoryPlan.DefaultBudgetBytes"/>
/// already uses (<see cref="GC.GetGCMemoryInfo()"/>), so it behaves the same inside a Proxmox LXC as
/// on bare metal. On Linux this is cross-checked against <c>/proc/meminfo</c>'s <c>MemAvailable</c> and the
/// *lower* of the two readings is used: the GC's own total-memory detection can be fooled on hosts where
/// the cgroup memory limit is unconstrained (sysconf then falls back to the outer host's physical page
/// count rather than what's actually available to this process), and taking the more conservative figure
/// is a direct hardening of "fail closed on an ambiguous reading", not a weakening of it. If /proc/meminfo
/// can't be read or parsed, it falls back to the GC-only reading (still failing closed per the rule below).
/// Fails closed: any ambiguous or unreadable reading refuses the job rather than assuming there is room.
/// </summary>
public static class HeadroomGuard
{
    /// <summary>Required free memory above the configured import budget before a job may start.</summary>
    public const long DefaultMarginBytes = 1024L * 1024 * 1024;   // ~1 GB

    /// <summary>Test seam: override how "bytes currently available" is read. Resets are the test's job.</summary>
    public static Func<long?> AvailableBytesReader { get; set; } = ReadAvailableBytes;

    private static long? ReadAvailableBytes()
    {
        long? gc = ReadFromGc();
        if (gc is null) return null;   // ambiguous GC reading: fail closed regardless of /proc/meminfo

        if (!OperatingSystem.IsLinux()) return gc;

        long? proc = ReadFromProcMeminfo();
        if (proc is null) return gc;   // /proc/meminfo unreadable/unparseable: fall back to the GC-only reading

        return Math.Min(gc.Value, proc.Value);   // the more conservative of the two never over-promises room
    }

    private static long? ReadFromGc()
    {
        var info = GC.GetGCMemoryInfo();
        long total = info.TotalAvailableMemoryBytes;
        long load = info.MemoryLoadBytes;
        // Ambiguous/implausible reading (e.g. not cgroup/memory-limit aware on this runtime):
        // refuse rather than assume there's room.
        if (total <= 0 || load < 0 || load > total) return null;
        return total - load;
    }

    /// <summary>
    /// Linux-only cross-check: <c>MemAvailable</c> from <c>/proc/meminfo</c> (kB), which the kernel already
    /// computes as "memory that can be made available without swapping" - a figure the GC's own host-level
    /// detection can miss when a cgroup's memory limit is unconstrained. Any read/parse failure returns
    /// null so the caller falls back to the GC-only reading rather than treating this as a second
    /// independent ambiguous-reading failure.
    /// </summary>
    private static long? ReadFromProcMeminfo()
    {
        try
        {
            foreach (var line in File.ReadLines("/proc/meminfo"))
            {
                if (!line.StartsWith("MemAvailable:", StringComparison.Ordinal)) continue;
                var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                // Expected shape: "MemAvailable:   1234567 kB"
                if (parts.Length >= 2 && long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var kb) && kb >= 0)
                    return kb * 1024;
                return null;
            }
            return null;   // no MemAvailable line: ambiguous, let the caller fall back to GC-only
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

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

namespace CrackDB;

using System.Globalization;

/// <summary>
/// Cross-checked "how much memory is actually available right now" primitive, shared by
/// <see cref="MemoryPlan.DefaultBudgetBytes"/> (sizes the auto-detected import budget) and
/// <c>CrackDB.Web.Jobs.HeadroomGuard</c> (the pre-job safety check), so the two always agree on the
/// same number instead of drifting apart on a host where one of the readings is unreliable.
///
/// Reads via <see cref="GC.GetGCMemoryInfo()"/>, which is cgroup/LXC-aware in the common case. On
/// Linux this is cross-checked against <c>/proc/meminfo</c>'s <c>MemAvailable</c> and the *lower* of
/// the two readings is used: the GC's own total-memory detection can be fooled on hosts where the
/// cgroup memory limit is unconstrained (sysconf then falls back to the outer host's physical page
/// count rather than what's actually available to this process), so taking the more conservative
/// figure is a direct hardening of "fail closed on an ambiguous reading", not a weakening of it. If
/// /proc/meminfo can't be read or parsed, it falls back to the GC-only reading (still failing closed
/// per the rule below).
/// Fails closed: any ambiguous or unreadable reading returns null rather than assuming there is room.
/// </summary>
public static class SystemMemory
{
    /// <summary>Test seam: override how "bytes currently available" is read as a whole. Resets are the test's job.</summary>
    public static Func<long?> AvailableBytesReader { get; set; } = ReadAvailableBytes;

    /// <summary>Test seam: override just the GC-derived reading, to simulate it diverging from /proc/meminfo.</summary>
    public static Func<long?> GcReader { get; set; } = ReadFromGc;

    /// <summary>Test seam: override just the /proc/meminfo-derived reading, to simulate it diverging from the GC reading.</summary>
    public static Func<long?> ProcMeminfoReader { get; set; } = ReadFromProcMeminfo;

    private static long? ReadAvailableBytes()
    {
        long? gc = GcReader();
        if (gc is null) return null;   // ambiguous GC reading: fail closed regardless of /proc/meminfo

        if (!OperatingSystem.IsLinux()) return gc;

        long? proc = ProcMeminfoReader();
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
}

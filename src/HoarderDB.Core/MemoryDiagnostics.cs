using System.Globalization;

namespace HoarderDB;

/// <summary>Reads a small text file; returns null on any failure. Injectable so diagnostics are testable.</summary>
public interface ITextFileReader
{
    string? TryReadAllText(string path);
}

/// <summary>Real file reader: never throws (absent file, no permission, non-Linux host all give null).</summary>
public sealed class FileTextReader : ITextFileReader
{
    public string? TryReadAllText(string path)
    {
        try { return File.ReadAllText(path); }
        catch (Exception) { return null; }
    }
}

/// <summary>
/// Pure parsers and fixed-path readers for operator memory diagnostics. Numbers only: nothing here reads
/// a database path, word or hash. Every method tolerates missing or malformed input (returns null) and never throws.
/// </summary>
public static class MemoryDiagnostics
{
    public const string ProcStatusPath = "/proc/self/status";
    public const string CgroupV2MemoryMax = "/sys/fs/cgroup/memory.max";
    public const string CgroupV2MemoryHigh = "/sys/fs/cgroup/memory.high";
    public const string CgroupV1MemoryLimit = "/sys/fs/cgroup/memory/memory.limit_in_bytes";

    /// <summary>Above this many L0 files a "compaction recommended" warning is logged.</summary>
    public const long L0WarnFiles = 64;

    /// <summary>Anything at or above this is treated as "no limit" (cgroup v1 reports a huge page-aligned number).</summary>
    private const long NoLimitThreshold = 1L << 60;

    /// <summary>Parses a "Key:   123 kB" line of /proc/self/status into bytes.</summary>
    public static long? ParseProcStatusKb(string? text, string key)
    {
        if (string.IsNullOrEmpty(text)) return null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith(key + ":", StringComparison.Ordinal)) continue;
            var parts = line.Substring(key.Length + 1).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 1 && long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var n)
                && n >= 0 && n <= long.MaxValue / 1024)
                return n * 1024;
            return null;
        }
        return null;
    }

    /// <summary>Parses a cgroup limit file: a number of bytes, or "max" / absurdly large = no limit (null).</summary>
    public static long? ParseCgroupLimit(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = text.Trim();
        if (!long.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out var n)) return null;   // "max", garbage
        return n <= 0 || n >= NoLimitThreshold ? null : n;
    }

    /// <summary>
    /// Index block bytes from <c>rocksdb.aggregated-table-properties</c>. The text is a flat list of
    /// "name=value" pairs; the index entry is named "index block size" (older/other builds: "index_size"),
    /// possibly with a parenthesised qualifier before the '='. Returns null if absent or unparseable.
    /// </summary>
    public static long? ParseIndexBytes(string? props)
    {
        if (string.IsNullOrEmpty(props)) return null;
        foreach (var name in new[] { "index block size", "index_size" })
        {
            int at = props.IndexOf(name, StringComparison.Ordinal);
            if (at < 0) continue;
            int eq = props.IndexOf('=', at + name.Length);
            if (eq < 0) continue;
            int i = eq + 1;
            while (i < props.Length && props[i] == ' ') i++;
            int start = i;
            while (i < props.Length && props[i] >= '0' && props[i] <= '9') i++;
            if (i > start && long.TryParse(props.AsSpan(start, i - start), NumberStyles.None, CultureInfo.InvariantCulture, out var n))
                return n;
        }
        return null;
    }

    public sealed record ProcessMemory(long? RssAnonBytes, long? VmHwmBytes, long GcCommittedBytes);

    public static ProcessMemory ReadProcessMemory(ITextFileReader reader)
    {
        string? status = null;
        try { status = reader.TryReadAllText(ProcStatusPath); } catch (Exception) { }
        long committed = 0;
        try { committed = GC.GetGCMemoryInfo().TotalCommittedBytes; } catch (Exception) { }
        return new ProcessMemory(ParseProcStatusKb(status, "RssAnon"), ParseProcStatusKb(status, "VmHWM"), committed);
    }

    /// <summary>cgroup memory.max (v2, then v1 fallback), or null when absent / unlimited.</summary>
    public static long? ReadCgroupMemoryMax(ITextFileReader reader)
    {
        try { return ParseCgroupLimit(reader.TryReadAllText(CgroupV2MemoryMax)) ?? ParseCgroupLimit(reader.TryReadAllText(CgroupV1MemoryLimit)); }
        catch (Exception) { return null; }
    }

    public static long? ReadCgroupMemoryHigh(ITextFileReader reader)
    {
        try { return ParseCgroupLimit(reader.TryReadAllText(CgroupV2MemoryHigh)); }
        catch (Exception) { return null; }
    }

    /// <summary>True when index bytes x (1 + scanThreads) exceeds 50% of the cgroup limit. False if a value is unknown.</summary>
    public static bool IndexExceedsHalfLimit(long? indexBytes, int scanThreads, long? cgroupMax)
    {
        if (indexBytes is null || cgroupMax is null || cgroupMax <= 0) return false;
        return (decimal)indexBytes.Value * (1 + Math.Max(0, scanThreads)) > (decimal)cgroupMax.Value / 2m;
    }
}

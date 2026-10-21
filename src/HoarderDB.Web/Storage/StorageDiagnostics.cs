using HoarderDB;

namespace HoarderDB.Web.Storage;

/// <summary>
/// Operator memory visibility: numbers only (never paths, db names, words or hashes). Every method swallows its own
/// failures so diagnostics can never break a request or a job.
/// </summary>
public interface IStorageDiagnostics
{
    void LogStartup(int scanThreads, int maxSubcompactions);
    /// <param name="phase">Fixed label: "open", "job-start" or "job-finish".</param>
    void LogHandle(string phase, WordlistDatabase db, int scanThreads);
}

public sealed class StorageDiagnostics : IStorageDiagnostics
{
    private readonly ILogger<StorageDiagnostics>? _logger;
    private readonly ITextFileReader _reader;

    public StorageDiagnostics(ILogger<StorageDiagnostics>? logger = null, ITextFileReader? reader = null)
    {
        _logger = logger;
        _reader = reader ?? new FileTextReader();
    }

    private static string Mb(long? bytes) => bytes is null ? "n/a" : (bytes.Value / (1024 * 1024)).ToString(System.Globalization.CultureInfo.InvariantCulture);

    // Hex/decimal number from the operator's DOTNET_GCHeapHardLimit or the runtimeconfig knob; "default" = the runtime's derived 75% of the container limit.
    private static string HardLimitText()
    {
        var v = Environment.GetEnvironmentVariable("DOTNET_GCHeapHardLimit") ?? AppContext.GetData("GCHeapHardLimit")?.ToString();
        return v is { Length: > 0 and <= 20 } && v.All(c => char.IsAsciiHexDigit(c) || c == 'x' || c == 'X') ? v : "default";
    }

    public void LogStartup(int scanThreads, int maxSubcompactions)
    {
        try
        {
            var gc = GC.GetGCMemoryInfo();
            _logger?.LogInformation(
                "Memory diagnostics: GC {GcMode} concurrent {Concurrent}, GC heap hard limit {HardLimit}, total available {TotalMb} MB, processors {Procs}, cgroup memory.max {MaxMb} MB, memory.high {HighMb} MB, block cache {CacheMb} MB, max subcompactions {Sub}, scan threads {ScanThreads}; read opens: auto-compaction off",
                System.Runtime.GCSettings.IsServerGC ? "server" : "workstation",
                AppContext.TryGetSwitch("System.GC.Concurrent", out var c) ? c : (bool?)null,
                HardLimitText(),
                Mb(gc.TotalAvailableMemoryBytes), Environment.ProcessorCount,
                Mb(MemoryDiagnostics.ReadCgroupMemoryMax(_reader)), Mb(MemoryDiagnostics.ReadCgroupMemoryHigh(_reader)),
                WordlistDatabase.BlockCacheBytes / (1024 * 1024), maxSubcompactions, scanThreads);
        }
        catch (Exception) { /* diagnostics must never throw */ }
    }

    public void LogHandle(string phase, WordlistDatabase db, int scanThreads)
    {
        try
        {
            var mem = MemoryDiagnostics.ReadProcessMemory(_reader);
            long l0 = db.NumFilesAtLevel0();
            long sst = db.TotalSstFilesBytes();
            long? index = MemoryDiagnostics.ParseIndexBytes(db.AggregatedTableProperties());
            _logger?.LogInformation(
                "Storage {Phase}: RssAnon {RssAnonMb} MB, VmHWM {HwmMb} MB, GC committed {GcMb} MB, L0 files {L0}, SST size {SstMb} MB, index {IndexMb} MB",
                phase, Mb(mem.RssAnonBytes), Mb(mem.VmHwmBytes), Mb(mem.GcCommittedBytes), l0, Mb(sst < 0 ? null : sst), Mb(index));
            if (l0 > MemoryDiagnostics.L0WarnFiles)
                _logger?.LogWarning("Storage {Phase}: {L0} L0 files (over {Limit}); compaction recommended (reads slow down and scans use more memory as L0 files pile up)",
                    phase, l0, MemoryDiagnostics.L0WarnFiles);
            if (MemoryDiagnostics.IndexExceedsHalfLimit(index, scanThreads, MemoryDiagnostics.ReadCgroupMemoryMax(_reader)))
                _logger?.LogWarning("Storage {Phase}: index {IndexMb} MB x (1 + {ScanThreads} scan threads) exceeds 50% of the container memory limit; a search, count or compaction may run out of memory - compact first",
                    phase, Mb(index), scanThreads);
        }
        catch (Exception) { /* diagnostics must never throw */ }
    }
}

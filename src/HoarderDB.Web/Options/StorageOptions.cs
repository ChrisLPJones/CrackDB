using Microsoft.Extensions.Options;

namespace HoarderDB.Web.Options;

/// <summary>
/// Where databases and the Data Protection key ring live. Deliberately has no defaults: a
/// forgotten setting must stop startup rather than fall back to a real data path.
/// Env: CRACKDB_Storage__DataRoot/HOARDERDB_Storage__DataRoot, CRACKDB_Storage__KeyPath/HOARDERDB_Storage__KeyPath, CRACKDB_Storage__IncomingRoot/HOARDERDB_Storage__IncomingRoot, CRACKDB_Storage__ImportMemoryMb/HOARDERDB_Storage__ImportMemoryMb, CRACKDB_Storage__ScanThreads/HOARDERDB_Storage__ScanThreads, CRACKDB_Storage__MaxSubcompactions/HOARDERDB_Storage__MaxSubcompactions (HOARDERDB_ wins if both are set).
/// </summary>
public sealed class StorageOptions
{
    public const string Section = "Storage";

    public string DataRoot { get; set; } = "";
    public string KeyPath { get; set; } = "";

    /// <summary>
    /// Directory server-side imports are read from (and where browser uploads are staged under
    /// <c>.uploads</c>). Optional: when unset, file imports and uploads are disabled (compact still works).
    /// </summary>
    public string IncomingRoot { get; set; } = "";

    /// <summary>
    /// Memory (MB) one open database may use for write buffers and the import pipeline.
    /// 0 or unset = auto (half of the memory available to the process); otherwise 256..1048576.
    /// </summary>
    public long ImportMemoryMb { get; set; }

    /// <summary>
    /// Most threads all parallel database scans (length breakdown, exact count, stats) may use together. 0 or unset = auto (processors - 1, at least 1, so one core is left
    /// for the web server); otherwise 1..<see cref="HoarderDB.ScanLimiter.MaxCap"/>. 1 = single-threaded scans.
    /// Operator config only.
    /// </summary>
    public int ScanThreads { get; set; }

    /// <summary>
    /// RocksDB <c>max_subcompactions</c> for compaction. 0 or unset = 1 (each extra subcompaction holds more input
    /// index blocks in memory); otherwise 1..<see cref="HoarderDB.ScanLimiter.MaxCap"/>. Independent of
    /// <see cref="ScanThreads"/>. Operator config only.
    /// </summary>
    public int MaxSubcompactions { get; set; }
}

/// <summary>Browser-upload limits. Env: CRACKDB_Upload__MaxBytes/HOARDERDB_Upload__MaxBytes (0 or unset = unlimited; HOARDERDB_ wins if both are set).</summary>
public sealed class UploadOptions
{
    public const string Section = "Upload";
    public long MaxBytes { get; set; }
}

public sealed class UploadOptionsValidator : IValidateOptions<UploadOptions>
{
    public ValidateOptionsResult Validate(string? name, UploadOptions o) =>
        o.MaxBytes >= 0 ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail("Upload:MaxBytes must be zero (unlimited) or greater.");
}

public sealed class StorageOptionsValidator : IValidateOptions<StorageOptions>
{
    /// <summary>Sanity ceiling (1 TiB in MB): a typo must not overflow the MB-to-bytes conversion.</summary>
    public const long MaxImportMemoryMb = 1_048_576;

    public ValidateOptionsResult Validate(string? name, StorageOptions o)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(o.DataRoot))
            errors.Add("Storage:DataRoot is required (env CRACKDB_Storage__DataRoot or HOARDERDB_Storage__DataRoot).");
        else if (!Directory.Exists(o.DataRoot))
            errors.Add("Storage:DataRoot does not exist or is not a directory.");
        if (string.IsNullOrWhiteSpace(o.KeyPath))
            errors.Add("Storage:KeyPath is required (env CRACKDB_Storage__KeyPath or HOARDERDB_Storage__KeyPath).");
        if (!string.IsNullOrWhiteSpace(o.IncomingRoot) && !Directory.Exists(o.IncomingRoot))
            errors.Add("Storage:IncomingRoot does not exist or is not a directory.");
        if (o.ImportMemoryMb < 0 || (o.ImportMemoryMb > 0 && o.ImportMemoryMb < 256) || o.ImportMemoryMb > MaxImportMemoryMb)
            errors.Add($"Storage:ImportMemoryMb must be 0 (auto) or 256..{MaxImportMemoryMb}.");
        if (o.ScanThreads < 0 || o.ScanThreads > HoarderDB.ScanLimiter.MaxCap)
            errors.Add($"Storage:ScanThreads must be 0 (auto) or 1..{HoarderDB.ScanLimiter.MaxCap}.");
        if (o.MaxSubcompactions < 0 || o.MaxSubcompactions > HoarderDB.ScanLimiter.MaxCap)
            errors.Add($"Storage:MaxSubcompactions must be 0 (default 1) or 1..{HoarderDB.ScanLimiter.MaxCap}.");
        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}

using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using HoarderDB.Web.Endpoints;

namespace HoarderDB.Web.Storage;

/// <summary>One finished import of a database. Advisory data: never trusted for security decisions.</summary>
public sealed record ImportRecord(
    string Id, string JobId,
    string Source,                 // display label only: "upload:<safe name>" or an incoming-relative path; never absolute
    string Sha256,                 // lower hex of the raw bytes (equals sha256sum)
    long SizeBytes, DateTimeOffset ImportedAt, DateTimeOffset? StartedAt,
    string Status,                 // completed | cancelled | failed | backfilled
    int? Min, int? Max,            // null = unbounded
    long LinesRead, int Errors, long EstNewEntries, bool Forced);

/// <summary>One validated row of a backfill batch: a file already imported outside the app, not re-read here.</summary>
public sealed record BackfillEntry(string Name, string Sha256, long SizeBytes, DateTimeOffset ImportedAtUtc);

public sealed record BackfillSkip(string Sha256, string Reason);

public sealed record BackfillResult(int Written, IReadOnlyList<BackfillSkip> Skipped, int Total);

/// <summary>What a client is told about an earlier import (no hash, no ids beyond the job id).</summary>
public sealed record ImportMatch(DateTimeOffset ImportedAt, string Source, string JobId, long LinesRead, string Status);

public sealed record ImportPage(int Total, IReadOnlyList<ImportRecord> Entries);

/// <summary>Result of comparing a candidate file with a database's completed imports.</summary>
public sealed record ImportCheck(ImportRecord? Duplicate, ImportRecord? Changed, ImportRecord? Narrower)
{
    public static readonly ImportCheck None = new(null, null, null);

    public string? Note =>
        Changed is { } c ? $"Changed file: a different version of this file was imported on {c.ImportedAt:yyyy-MM-dd}."
        : Narrower is { } n ? $"Identical content was imported on {n.ImportedAt:yyyy-MM-dd} with a narrower length filter; importing the wider range."
        : null;
}

public interface IImportHistory
{
    ImportPage List(string db, int limit, int offset);
    /// <param name="source">Display label to compare for the "changed file" rule (null = skip).</param>
    ImportCheck Check(string db, string sha256, string? source, int? min, int? max);
    /// <summary>Advisory match by label and size when the hash is not known yet.</summary>
    ImportRecord? FindByNameAndSize(string db, IReadOnlyCollection<string> labels, long size, int? min, int? max);
    ImportRecord? FindChangedByName(string db, IReadOnlyCollection<string> labels);
    void Append(string db, ImportRecord record);
    /// <summary>
    /// Records files already imported outside the app (not re-read here) as status "backfilled", unfiltered
    /// (null min/max) so a later real import with a narrower filter is still seen as already-done. An entry
    /// whose sha256 already has a completed OR backfilled record is skipped, not duplicated. Rejects the whole
    /// batch (throws <see cref="ImportHistoryCapExceededException"/>, 413) rather than truncating when it would
    /// push the database past <see cref="MaxEntries"/>.
    /// </summary>
    BackfillResult Backfill(string db, IReadOnlyList<BackfillEntry> entries);
}

/// <summary>
/// Per-database import history kept in a JSON sidecar inside the db directory. Not a RocksDB key or column
/// family, so it never touches the engine or its scans, dies with the directory on delete-db, and moves with
/// the db. The path is the resolver's db directory plus a constant name: no client text reaches a path.
/// A history write never creates directories (it must not resurrect a deleted db) and refuses a symlinked
/// sidecar. Read-modify-write is serialised per db; the file is replaced atomically (0600 tmp + rename).
/// Status is one of completed | cancelled | failed | backfilled (a backfilled record stands for a file
/// imported outside the app; see <see cref="Backfill"/>).
/// </summary>
public sealed partial class ImportHistory(DataRootResolver resolver, ILogger<ImportHistory> log) : IImportHistory
{
    public const string FileName = "hoarderdb-import-history.json";
    /// <summary>The pre-rebrand sidecar filename, still present inside every db directory created before the
    /// rebrand. <see cref="MigrateLegacySidecar"/> renames it to <see cref="FileName"/> on open.</summary>
    public const string LegacyFileName = "crackdb-import-history.json";
    public const int MaxEntries = 5000;
    private const string TmpPrefix = ".hoarderdb-import-history.";
    private const string TmpSuffix = ".tmp";

    /// <summary>
    /// Idempotent, advisory, non-destructive rename of the legacy sidecar filename to the current one.
    /// Run on every db open (not only on the next write) so every existing database - including ones that
    /// are never written to again - picks up the new filename: if <see cref="LegacyFileName"/> exists and
    /// <see cref="FileName"/> does not, atomically renames it (same semantics as the rest of this class's
    /// atomic tmp+rename writes - here a plain <see cref="File.Move(string, string)"/> since there is no tmp
    /// staging needed for a rename). If the new name already exists, or there is no old file, or a symlink
    /// is involved, or anything goes wrong, this is a no-op: never fatal to opening or reading a database
    /// (same "advisory, never fails an import" philosophy as the rest of <see cref="ImportHistory"/>). Safe
    /// to call on every open, including repeatedly on the same directory.
    /// </summary>
    public static void MigrateLegacySidecar(string dir, ILogger? log = null)
    {
        try
        {
            var oldPath = Path.Combine(dir, LegacyFileName);
            var newPath = Path.Combine(dir, FileName);
            if (File.Exists(newPath)) return;                         // already migrated (or pre-existing) - leave alone
            if (!File.Exists(oldPath)) return;                        // nothing to migrate
            if (new FileInfo(oldPath).LinkTarget != null) return;      // never follow/move a symlink
            File.Move(oldPath, newPath);
        }
        catch (Exception ex)
        {
            // Advisory data: a failed migration must never block opening or reading a database.
            log?.LogWarning("Could not migrate the legacy import-history sidecar in {Dir} ({Kind})", dir, ex.GetType().Name);
        }
    }

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex HexRegex();
    [GeneratedRegex("^[0-9a-fA-F]{64}$")]
    private static partial Regex HexAnyCaseRegex();

    public static bool IsValidSha256(string? s) => s != null && HexAnyCaseRegex().IsMatch(s);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private sealed record FileModel(int V, List<ImportRecord> Entries);

    private readonly ConcurrentDictionary<string, object> _locks = new(StringComparer.Ordinal);

    public ImportPage List(string db, int limit, int offset)
    {
        var all = Load(db);                                   // oldest first on disk
        var page = all.AsEnumerable().Reverse().Skip(Math.Max(0, offset)).Take(Math.Clamp(limit, 0, 200)).ToList();
        return new ImportPage(all.Count, page);
    }

    public ImportCheck Check(string db, string sha256, string? source, int? min, int? max)
    {
        var hash = sha256.ToLowerInvariant();
        // Backfilled entries count exactly like completed ones here: that is the point of backfill.
        var done = Load(db).Where(r => r.Status is "completed" or "backfilled").Reverse().ToList();   // newest first
        var same = done.Where(r => r.Sha256 == hash).ToList();
        var dup = same.FirstOrDefault(r => Covers(r, min, max));
        if (dup != null) return new ImportCheck(dup, null, null);
        var narrower = same.FirstOrDefault();
        var changed = source == null ? null
            : done.FirstOrDefault(r => r.Sha256 != hash && string.Equals(r.Source, source, StringComparison.Ordinal));
        return new ImportCheck(null, narrower == null ? changed : null, narrower);
    }

    public ImportRecord? FindByNameAndSize(string db, IReadOnlyCollection<string> labels, long size, int? min, int? max) =>
        Load(db).Where(r => r.Status is "completed" or "backfilled" && r.SizeBytes == size && labels.Contains(r.Source) && Covers(r, min, max))
            .LastOrDefault();

    public ImportRecord? FindChangedByName(string db, IReadOnlyCollection<string> labels) =>
        Load(db).Where(r => r.Status is "completed" or "backfilled" && labels.Contains(r.Source)).LastOrDefault();

    /// <summary>Prior range covers the new one; null min means 0, null max means unbounded.</summary>
    public static bool Covers(ImportRecord prior, int? min, int? max) =>
        (prior.Min ?? 0) <= (min ?? 0) && (prior.Max is null || (max is not null && prior.Max >= max));

    public void Append(string db, ImportRecord record)
    {
        string dir;
        try { dir = resolver.Resolve(db); }
        catch (InvalidDbNameException) { return; }
        lock (LockFor(db))
        {
            try
            {
                // Never CreateDirectory here: if the db is gone (deleted mid-job) the record is dropped.
                if (!DataRootResolver.IsDatabaseDir(dir)) return;
                var path = Path.Combine(dir, FileName);
                if (IsLink(path)) { log.LogWarning("Import history file for {Db} is a symlink; not writing", db); return; }
                var entries = LoadFile(dir, db);
                entries.Add(record);
                if (entries.Count > MaxEntries) entries.RemoveRange(0, entries.Count - MaxEntries);
                WriteAtomic(dir, path, entries);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Advisory data must never fail an import; DirectoryNotFound (delete racing) lands here too.
                log.LogWarning("Could not write the import history for {Db} ({Kind})", db, ex.GetType().Name);
            }
        }
    }

    public BackfillResult Backfill(string db, IReadOnlyList<BackfillEntry> entries)
    {
        var dir = resolver.ResolveExisting(db);                // 400 bad name / 404 missing
        lock (LockFor(db))
        {
            var path = Path.Combine(dir, FileName);
            if (IsLink(path)) throw new IOException("Import history file is a symlink; refusing to write.");
            var existing = LoadFile(dir, db);
            var hashes = new HashSet<string>(
                existing.Where(r => r.Status is "completed" or "backfilled").Select(r => r.Sha256), StringComparer.Ordinal);

            var skipped = new List<BackfillSkip>();
            var toAdd = new List<ImportRecord>();
            foreach (var e in entries)
            {
                var hash = e.Sha256.ToLowerInvariant();
                if (!hashes.Add(hash)) { skipped.Add(new BackfillSkip(hash, "already imported")); continue; }
                toAdd.Add(new ImportRecord(
                    Guid.NewGuid().ToString("N"), "backfill-" + Guid.NewGuid().ToString("N"),
                    "backfill:" + JobEndpoints.SafeLabel(e.Name), hash, e.SizeBytes, e.ImportedAtUtc, null,
                    "backfilled", null, null, 0, 0, 0, false));
            }

            // Reject the whole batch rather than silently dropping the oldest entries (unlike the normal
            // job-queue Append, which must never fail an import): a backfill is a deliberate admin action.
            if (!DataRootResolver.IsDatabaseDir(dir)) throw new DbNotFoundException("Database not found.");
            if (existing.Count + toAdd.Count > MaxEntries)
                throw new ImportHistoryCapExceededException(
                    $"Backfilling {toAdd.Count} entries would exceed the {MaxEntries}-entry history cap for this database.");

            if (toAdd.Count > 0) WriteAtomic(dir, path, existing.Concat(toAdd).ToList());
            return new BackfillResult(toAdd.Count, skipped, entries.Count);
        }
    }

    private object LockFor(string db) => _locks.GetOrAdd(db, _ => new object());

    private List<ImportRecord> Load(string db)
    {
        var dir = resolver.Resolve(db);                        // 400 bad name; a missing db simply has no history
        if (!DataRootResolver.IsDatabaseDir(dir)) return [];
        lock (LockFor(db)) return LoadFile(dir, db);
    }

    private static bool IsLink(string path)
    {
        try { return new FileInfo(path).LinkTarget != null; }
        catch (IOException) { return false; }
    }

    private List<ImportRecord> LoadFile(string dir, string db)
    {
        var path = Path.Combine(dir, FileName);
        if (!File.Exists(path) || IsLink(path)) return [];
        try
        {
            var bytes = File.ReadAllBytes(path);
            var model = JsonSerializer.Deserialize<FileModel>(bytes, Json);
            if (model?.Entries == null || model.V != 1) throw new JsonException("shape");
            return model.Entries.Where(Valid).ToList();
        }
        catch (JsonException)
        {
            // Quarantine and continue empty; never log the contents.
            log.LogWarning("Import history for {Db} is corrupt; moved aside and starting empty", db);
            try { File.Move(path, path + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss"), overwrite: true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            return [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
    }

    private static bool Valid(ImportRecord r) =>
        r != null && r.Sha256 != null && HexRegex().IsMatch(r.Sha256) && r.Status is "completed" or "cancelled" or "failed" or "backfilled"
        && r.Source != null && r.Id != null && r.JobId != null;

    private static void WriteAtomic(string dir, string path, List<ImportRecord> entries)
    {
        foreach (var stale in Directory.EnumerateFiles(dir, TmpPrefix + "*" + TmpSuffix))
            try { File.Delete(stale); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }

        var tmp = Path.Combine(dir, TmpPrefix + Guid.NewGuid().ToString("N") + TmpSuffix);
        var opts = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows())
            opts.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        try
        {
            using (var fs = new FileStream(tmp, opts))
            {
                JsonSerializer.Serialize(fs, new FileModel(1, entries), Json);
                fs.Flush(flushToDisk: true);
            }
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmp); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            throw;
        }
    }
}

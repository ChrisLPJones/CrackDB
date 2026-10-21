using System.Collections.Concurrent;
using System.Text.Json;

namespace HoarderDB.Web.Storage;

public sealed record LengthCount(int Length, long Count);

/// <summary>A stored exact length breakdown and when its scan finished (UTC).</summary>
public sealed record LengthBreakdown(DateTimeOffset AsOf, IReadOnlyList<LengthCount> Lengths);

public interface ILengthBreakdownStore
{
    /// <summary>The stored breakdown, or null if none (or the file was unsafe/corrupt). Never creates anything; throws only for a bad db name.</summary>
    LengthBreakdown? Load(string db);
    /// <summary>Atomically replaces the stored breakdown. Throws <see cref="DbNotFoundException"/> if the db is gone, <see cref="BreakdownSaveException"/> on I/O failure.</summary>
    void Save(string db, DateTimeOffset asOf, IReadOnlyList<LengthCount> lengths);
}

/// <summary>
/// Per-database cached length breakdown in a JSON sidecar inside the db directory (same posture as
/// <see cref="ImportHistory"/>): the path is the resolver's db dir plus a constant name (no client text in
/// a path), never CreateDirectory (a deleted db must not be resurrected), a symlink or directory at the
/// target is neither read nor written, writes are 0600 tmp+rename under a per-db lock, and a file that
/// fails strict validation is moved aside and treated as "no result". Counts and a timestamp only.
/// </summary>
public sealed class LengthBreakdownStore(DataRootResolver resolver, ILogger<LengthBreakdownStore> log, TimeProvider? time = null)
    : ILengthBreakdownStore
{
    public const string FileName = "hoarderdb-length-breakdown.json";
    public const long MaxFileBytes = 1024 * 1024;
    public const int MaxEntries = 10_000;
    private static readonly TimeSpan FutureSlack = TimeSpan.FromDays(1);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private sealed record Row(int Length, long Count);
    private sealed record FileModel(int V, DateTimeOffset AsOf, List<Row>? Lengths);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, object> _locks = new(StringComparer.Ordinal);
    private object LockFor(string db) => _locks.GetOrAdd(db, _ => new object());

    public LengthBreakdown? Load(string db)
    {
        var dir = resolver.Resolve(db);                          // 400 on a bad name
        if (!DataRootResolver.IsDatabaseDir(dir)) return null;
        var path = Path.Combine(dir, FileName);
        lock (LockFor(db))
        {
            try
            {
                if (AtomicJsonFile.IsUnsafe(path)) { log.LogWarning("Length breakdown target for {Db} is a link or directory; ignored", db); return null; }
                var info = new FileInfo(path);
                if (!info.Exists) return null;
                if (info.Length > MaxFileBytes) return Corrupt(db, path);
                var model = JsonSerializer.Deserialize<FileModel>(File.ReadAllBytes(path), Json);
                return Validate(model) is { } ok ? ok : Corrupt(db, path);
            }
            catch (JsonException) { return Corrupt(db, path); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        }
    }

    private LengthBreakdown? Validate(FileModel? m)
    {
        if (m is null || m.V != 1 || m.Lengths is null || m.Lengths.Count > MaxEntries) return null;
        if (m.AsOf > _time.GetUtcNow() + FutureSlack) return null;
        int prev = -1;
        foreach (var r in m.Lengths)
        {
            if (r is null || r.Length < 0 || r.Length <= prev || r.Count < 1) return null;
            prev = r.Length;
        }
        return new LengthBreakdown(m.AsOf.ToUniversalTime(), m.Lengths.Select(r => new LengthCount(r.Length, r.Count)).ToList());
    }

    private LengthBreakdown? Corrupt(string db, string path)
    {
        // Contents are never logged.
        log.LogWarning("Length breakdown for {Db} is invalid; moved aside", db);
        AtomicJsonFile.MoveAside(path);
        return null;
    }

    public void Save(string db, DateTimeOffset asOf, IReadOnlyList<LengthCount> lengths)
    {
        var dir = resolver.Resolve(db);                          // 400 on a bad name
        if (lengths.Count > MaxEntries) throw new BreakdownSaveException("Too many distinct lengths to store.");
        lock (LockFor(db))
        {
            // Never CreateDirectory: a db deleted mid-scan must not be resurrected by its result.
            if (!DataRootResolver.IsDatabaseDir(dir)) throw new DbNotFoundException("Database not found.");
            var path = Path.Combine(dir, FileName);
            try
            {
                foreach (var stale in Directory.EnumerateFiles(dir, FileName + ".tmp-*"))
                    try { File.Delete(stale); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                var model = new FileModel(1, asOf.ToUniversalTime(), lengths.Select(l => new Row(l.Length, l.Count)).ToList());
                AtomicJsonFile.Write(path, JsonSerializer.SerializeToUtf8Bytes(model, Json));
            }
            catch (DirectoryNotFoundException) { throw new DbNotFoundException("Database not found."); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                log.LogWarning("Could not write the length breakdown for {Db} ({Kind})", db, e.GetType().Name);
                throw new BreakdownSaveException("Could not save the length breakdown.", e);
            }
        }
    }
}

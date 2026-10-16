using System.Collections.Concurrent;
using System.Text;
using CrackDB.Web.Options;
using Microsoft.Extensions.Options;

namespace CrackDB.Web.Storage;

public enum DbOpenMode { Read, Bulk }   // Read = normal open; Bulk = bulkLoad:true (P3 imports only)

public interface IDbManager
{
    IReadOnlyList<string> ListDatabases();
    DbLease AcquireRead(string name);
    /// <summary>
    /// Exclusive write window. Readers are drained first; while the window runs, reads of this db
    /// fail fast with <see cref="DbBusyException"/> (409) instead of hanging for the whole import.
    /// <paramref name="allowCreate"/> lets an import materialise a brand-new db; compact leaves it off.
    /// </summary>
    Task<T> WithWriteAsync<T>(string name, DbOpenMode mode,
        Func<WordlistDatabase, CancellationToken, Task<T>> work, CancellationToken ct,
        bool allowCreate = false, bool failIfExists = false);
    /// <summary>
    /// Creates a new, empty database (left open for reads). <see cref="DbAlreadyExistsException"/> (409)
    /// if it exists, <see cref="DbLimitReachedException"/> (409) at the database-count cap.
    /// </summary>
    Task CreateAsync(string name, CancellationToken ct);
    /// <summary>
    /// Permanently deletes one database directory. <see cref="DbNotFoundException"/> (404) if it does not
    /// exist (including a second delete), <see cref="InvalidDbNameException"/> (400) for a bad name,
    /// <see cref="DbBusyException"/> (409) if a write window (import/compact/create) holds the db (fail-fast).
    /// </summary>
    Task DeleteAsync(string name, CancellationToken ct);
}

/// <summary>
/// Keeps a handle alive for the duration of a read (ref-count). A held lease, never a lock,
/// so it can span an async streamed response. Dispose releases it.
/// </summary>
public sealed class DbLease : IDisposable
{
    private DbManager.DbEntry? _entry;
    public WordlistDatabase Db { get; }

    internal DbLease(DbManager.DbEntry entry, WordlistDatabase db) { _entry = entry; Db = db; }

    public void Dispose()
    {
        var e = Interlocked.Exchange(ref _entry, null);   // idempotent
        if (e == null) return;
        lock (e.Gate)
        {
            e.Readers--;
            Monitor.PulseAll(e.Gate);
        }
    }
}

/// <summary>
/// Sole in-process owner of every opened WordlistDatabase (RocksDB allows one process per dir).
/// One handle per name, opened lazily, kept open until shutdown.
/// </summary>
public sealed class DbManager : IDbManager, IHostedService, IAsyncDisposable
{
    internal sealed class DbEntry
    {
        public readonly object Gate = new();          // guards Handle/Mode/Readers/Swapping
        public readonly SemaphoreSlim WriteGate = new(1, 1);
        public WordlistDatabase? Handle;
        public DbOpenMode Mode;
        public int Readers;
        public bool Swapping;                          // a write window is claiming/releasing the handle; readers park briefly
        public bool Writing;                           // the write work is running; readers fail fast (409)
        public bool Gone;                              // the db was deleted; this entry is orphaned (readers 404, writers re-fetch)
    }

    private static readonly Encoding Utf8 = new UTF8Encoding(false);
    private readonly DataRootResolver _resolver;
    private readonly ConcurrentDictionary<string, DbEntry> _entries = new(StringComparer.Ordinal);
    private readonly int _maxDatabases;
    private readonly long? _memoryBudgetBytes;
    private readonly ILogger? _logger;
    private bool _closed;

    /// <summary>Soft cap on databases under the data root; each created db keeps an open handle (and its threads) until shutdown.</summary>
    public const int DefaultMaxDatabases = 512;

    public DbManager(DataRootResolver resolver, int maxDatabases = DefaultMaxDatabases,
        IOptions<StorageOptions>? storage = null, ILogger<DbManager>? logger = null)
    {
        _resolver = resolver;
        _maxDatabases = maxDatabases;
        _logger = logger;
        long mb = storage?.Value.ImportMemoryMb ?? 0;
        _memoryBudgetBytes = mb > 0 ? mb * 1024 * 1024 : null;   // null = engine auto default
    }

    public Task CreateAsync(string name, CancellationToken ct) =>
        WithWriteAsync(name, DbOpenMode.Read, (_, _) => Task.FromResult(true), ct, allowCreate: true, failIfExists: true);

    public IReadOnlyList<string> ListDatabases() => _resolver.List();

    public DbLease AcquireRead(string name)
    {
        var path = _resolver.ResolveExisting(name);   // validates; R1: never opens an unseen path
        var entry = _entries.GetOrAdd(name, _ => new DbEntry());
        lock (entry.Gate)
        {
            if (entry.Gone) throw new DbNotFoundException("Database not found.");
            if (entry.Writing && !_closed) throw new DbBusyException("Database is busy (import or compact in progress).");
            while (entry.Swapping && !entry.Writing && !_closed) Monitor.Wait(entry.Gate);   // brief, sync park (drain / reopen); wakes when Writing starts
            if (entry.Gone) throw new DbNotFoundException("Database not found.");   // deleted while we were parked: never reopen (engine ctor would recreate the dir)
            if (entry.Writing && !_closed) throw new DbBusyException("Database is busy (import or compact in progress).");
            if (_closed) throw new ObjectDisposedException(nameof(DbManager));
            // The dir may have been deleted between ResolveExisting and here (fresh entry after a delete): never let the always-create ctor resurrect it.
            if (entry.Handle == null && !DataRootResolver.IsDatabaseDir(path)) throw new DbNotFoundException("Database not found.");
            entry.Handle ??= Open(path, DbOpenMode.Read, entry);
            entry.Readers++;
            return new DbLease(entry, entry.Handle);
        }
    }

    public async Task<T> WithWriteAsync<T>(string name, DbOpenMode mode,
        Func<WordlistDatabase, CancellationToken, Task<T>> work, CancellationToken ct,
        bool allowCreate = false, bool failIfExists = false)
    {
        // Create-on-import: Resolve (validate + contain) instead of ResolveExisting, so the
        // always-create engine constructor materialises the directory; still never an unvalidated path.
        var path = allowCreate ? _resolver.Resolve(name) : _resolver.ResolveExisting(name);
        if (failIfExists && !DataRootResolver.IsDatabaseDir(path)) EnsureRoom();   // cheap pre-check so rejected creates don't add entries
        var entry = await AcquireWriteGateAsync(name, ct);
        try
        {
            // Race-free against a concurrent create/import of the same name: checked inside the gate.
            if (failIfExists)
            {
                if (DataRootResolver.IsDatabaseDir(path)) throw new DbAlreadyExistsException("Database already exists.");
                EnsureRoom();
            }
            // Claim the handle, then wait (without holding the monitor across an await) for readers to drain.
            lock (entry.Gate) entry.Swapping = true;
            try
            {
                while (true)
                {
                    lock (entry.Gate)
                    {
                        if (entry.Readers == 0) { entry.Handle?.Dispose(); entry.Handle = null; break; }
                    }
                    await Task.Delay(25, ct);
                }
                WordlistDatabase db;
                lock (entry.Gate) { db = entry.Handle = Open(path, mode, entry); entry.Writing = true; Monitor.PulseAll(entry.Gate); }
                try { return await work(db, ct); }
                finally
                {
                    // Always reopen Read (Dispose flushes; WAL is off), even when work failed or was cancelled.
                    lock (entry.Gate)
                    {
                        entry.Writing = false;
                        entry.Handle?.Dispose();
                        entry.Handle = null;
                        entry.Handle = Open(path, DbOpenMode.Read, entry);
                    }
                }
            }
            finally
            {
                lock (entry.Gate) { entry.Swapping = false; Monitor.PulseAll(entry.Gate); }
            }
        }
        finally { entry.WriteGate.Release(); }
    }

    /// <summary>Takes the name's WriteGate. If the entry was orphaned by a delete while we waited, retries on the fresh entry.</summary>
    private async Task<DbEntry> AcquireWriteGateAsync(string name, CancellationToken ct)
    {
        while (true)
        {
            var entry = _entries.GetOrAdd(name, _ => new DbEntry());
            await entry.WriteGate.WaitAsync(ct);
            if (!entry.Gone) return entry;
            entry.WriteGate.Release();
        }
    }

    public async Task DeleteAsync(string name, CancellationToken ct)
    {
        _resolver.ResolveExisting(name);   // validate + 404 if missing, before any lock
        var entry = _entries.GetOrAdd(name, _ => new DbEntry());
        if (!await entry.WriteGate.WaitAsync(0, ct)) throw new DbBusyException("Database is busy.");   // fail-fast, never block behind an import
        try
        {
            if (entry.Gone) throw new DbNotFoundException("Database not found.");   // lost a race with another delete
            lock (entry.Gate) entry.Swapping = true;
            try
            {
                // Drain readers (no monitor held across an await), then close the handle: RocksDB holds the dir open.
                while (true)
                {
                    lock (entry.Gate)
                    {
                        if (entry.Readers == 0) { entry.Handle?.Dispose(); entry.Handle = null; break; }
                    }
                    await Task.Delay(25, ct);
                }

                // Re-resolve and re-assert immediately before the destructive call; never trust the earlier path.
                var full = _resolver.Resolve(name);
                if (!full.StartsWith(_resolver.Root + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                    || !DataRootResolver.IsDatabaseDir(full)
                    || new DirectoryInfo(full).LinkTarget is not null)
                    throw new DbNotFoundException("Database not found.");
                Directory.Delete(full, recursive: true);

                lock (entry.Gate) { entry.Gone = true; _entries.TryRemove(new KeyValuePair<string, DbEntry>(name, entry)); }
            }
            finally
            {
                lock (entry.Gate) { entry.Swapping = false; Monitor.PulseAll(entry.Gate); }   // parked readers see Gone -> 404
            }
        }
        finally { entry.WriteGate.Release(); }
    }

    private void EnsureRoom()
    {
        if (_resolver.List().Count >= _maxDatabases) throw new DbLimitReachedException("Database limit reached.");
    }

    private WordlistDatabase Open(string path, DbOpenMode mode, DbEntry entry)
    {
        var db = new WordlistDatabase(path, Utf8, bulkLoad: mode == DbOpenMode.Bulk, memoryBudgetBytes: _memoryBudgetBytes);
        entry.Mode = mode;
        return db;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Numbers only; logged once so an operator can see what the memory budget resolved to.
        var budget = _memoryBudgetBytes ?? MemoryPlan.DefaultBudgetBytes();
        var p = MemoryPlan.Compute(budget, Environment.ProcessorCount);
        _logger?.LogInformation(
            "Import memory budget {BudgetMb} MB ({Source}): {Buffers} x {BufferMb} MB write buffers, {Workers} workers, queue {Queue}",
            budget / (1024 * 1024), _memoryBudgetBytes is null ? "auto" : "configured",
            p.MaxBuffers, p.WriteBuffer / (1024 * 1024), p.Workers, p.QueueCapacity);
        return Task.CompletedTask;
    }
    public Task StopAsync(CancellationToken cancellationToken) { CloseAll(); return Task.CompletedTask; }
    public ValueTask DisposeAsync() { CloseAll(); return ValueTask.CompletedTask; }

    // Dispose flushes (WAL is off), so every handle must be closed cleanly on shutdown.
    private void CloseAll()
    {
        _closed = true;
        var deadline = DateTime.UtcNow.AddSeconds(10);   // let in-flight reads/streams finish first
        foreach (var entry in _entries.Values)
            lock (entry.Gate)
            {
                while (entry.Readers > 0 && DateTime.UtcNow < deadline) Monitor.Wait(entry.Gate, 100);
                entry.Handle?.Dispose();
                entry.Handle = null;
                Monitor.PulseAll(entry.Gate);
            }
    }
}

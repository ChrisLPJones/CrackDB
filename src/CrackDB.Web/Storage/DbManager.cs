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
        Func<WordlistDatabase, CancellationToken, Task<T>> work, CancellationToken ct, bool allowCreate = false);
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
    }

    private static readonly Encoding Utf8 = new UTF8Encoding(false);
    private readonly DataRootResolver _resolver;
    private readonly ConcurrentDictionary<string, DbEntry> _entries = new(StringComparer.Ordinal);
    private bool _closed;

    public DbManager(DataRootResolver resolver) => _resolver = resolver;

    public IReadOnlyList<string> ListDatabases() => _resolver.List();

    public DbLease AcquireRead(string name)
    {
        var path = _resolver.ResolveExisting(name);   // validates; R1: never opens an unseen path
        var entry = _entries.GetOrAdd(name, _ => new DbEntry());
        lock (entry.Gate)
        {
            if (entry.Writing && !_closed) throw new DbBusyException("Database is busy (import or compact in progress).");
            while (entry.Swapping && !entry.Writing && !_closed) Monitor.Wait(entry.Gate);   // brief, sync park (drain / reopen); wakes when Writing starts
            if (entry.Writing && !_closed) throw new DbBusyException("Database is busy (import or compact in progress).");
            if (_closed) throw new ObjectDisposedException(nameof(DbManager));
            entry.Handle ??= Open(path, DbOpenMode.Read, entry);
            entry.Readers++;
            return new DbLease(entry, entry.Handle);
        }
    }

    public async Task<T> WithWriteAsync<T>(string name, DbOpenMode mode,
        Func<WordlistDatabase, CancellationToken, Task<T>> work, CancellationToken ct, bool allowCreate = false)
    {
        // Create-on-import: Resolve (validate + contain) instead of ResolveExisting, so the
        // always-create engine constructor materialises the directory; still never an unvalidated path.
        var path = allowCreate ? _resolver.Resolve(name) : _resolver.ResolveExisting(name);
        var entry = _entries.GetOrAdd(name, _ => new DbEntry());
        await entry.WriteGate.WaitAsync(ct);
        try
        {
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

    private static WordlistDatabase Open(string path, DbOpenMode mode, DbEntry entry)
    {
        var db = new WordlistDatabase(path, Utf8, bulkLoad: mode == DbOpenMode.Bulk);
        entry.Mode = mode;
        return db;
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
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

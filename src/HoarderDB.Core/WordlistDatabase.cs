using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text;
using RocksDbSharp;

namespace HoarderDB;

/// <summary>
/// Stores wordlist entries in an embedded RocksDB (LSM) key-value store, tuned for
/// very large, frequent, write-heavy imports with automatic deduplication.
///
/// Key layout:  [4-byte big-endian length][password bytes]   Value: single marker byte.
///
/// Because the key begins with the length, all keys are physically sorted by length
/// (then by password), so:
///   * exact-string lookup  -> a single point Get
///   * one specific length  -> a contiguous range scan
///   * length X..Y          -> one contiguous range scan
/// Deduplication is free: identical passwords produce identical keys and collapse.
/// </summary>
public sealed partial class WordlistDatabase : IDisposable
{
    private readonly RocksDb _db;
    private readonly Encoding _enc;
    private readonly MemoryPlan _plan;
    private readonly ScanLimiter _scanLimiter;

    /// <summary>The memory plan this handle was opened with (write buffers, import pipeline sizes).</summary>
    public MemoryPlan Plan => _plan;

    /// <summary>The <c>max_subcompactions</c> this handle was opened with (bulk opens do not set it).</summary>
    public int MaxSubcompactions { get; }

    // Fixed, operator-level bounds on RocksDB's baseline memory, independent of ImportMemoryMb and
    // never client/request-configurable. Without these, index/filter block memory and open
    // file-handle/table-reader overhead are unbounded by default and grow with the number of SST
    // files on disk, i.e. with total db size, regardless of whether an import is running (observed
    // as idle RSS climbing with db size on a production host).
    public const long BlockCacheBytes = 256L * 1024 * 1024;    // 256 MB cap shared by index/filter/data blocks
    public const int MaxOpenFilesCap = 4096;                   // caps file-handle/table-reader overhead per open

    /// <param name="bulkLoad">
    /// Open tuned for a large import: a vector memtable (O(1) append inserts,
    /// sorted once at flush) instead of the default skiplist, which roughly
    /// halves import time. Read-only commands leave this false.
    /// </param>
    /// <param name="memoryBudgetBytes">
    /// Memory this handle may use for memtables and the import pipeline; null = half of the
    /// memory available to the process (see <see cref="MemoryPlan.DefaultBudgetBytes"/>).
    /// </param>
    /// <param name="enableAutoCompaction">
    /// Only meaningful for a non-bulk open (a bulk-load open already disables auto-compactions
    /// unconditionally, for import speed). True (the library default, used by the CLI and tests) lets
    /// RocksDB auto-compact once enough L0 files accumulate. The web app's DbManager always passes false
    /// (read opens and job opens alike): an auto-compaction is invisible, uncancellable and holds the index
    /// of every L0 file in memory, so compaction there only happens via an explicit <see cref="Compact"/>
    /// call (manual CompactRange does not need the auto trigger). This never affects read correctness: data
    /// is still flushed and visible; only the auto-compaction *trigger* is suppressed.
    /// </param>
    /// <param name="scanLimiter">
    /// Process-wide scan-thread budget (<c>Storage:ScanThreads</c>): caps the worker threads of parallel
    /// scans (<see cref="LengthDistribution"/>, <see cref="CountRange"/>). It does not affect compaction
    /// (see <paramref name="maxSubcompactions"/>). Null = <see cref="ScanLimiter.Default"/> (cores - 1).
    /// </param>
    /// <param name="maxSubcompactions">
    /// RocksDB <c>max_subcompactions</c> on non-bulk opens (<c>Storage:MaxSubcompactions</c>). Default 1: each extra
    /// subcompaction holds more input index blocks in memory (measured 1.2-1.7x at 3). Clamped to 1..<see cref="ScanLimiter.MaxCap"/>.
    /// </param>
    public WordlistDatabase(string path, Encoding encoding, bool bulkLoad = false, long? memoryBudgetBytes = null,
        bool enableAutoCompaction = true, ScanLimiter? scanLimiter = null, int maxSubcompactions = 1)
    {
        MaxSubcompactions = Math.Clamp(maxSubcompactions, 1, ScanLimiter.MaxCap);
        _enc = encoding;
        _scanLimiter = scanLimiter ?? ScanLimiter.Default;

        // Tuned for write throughput on huge imports. Disk size is not a concern,
        // so buffers are large, compression is off (saves CPU), and compaction
        // triggers are relaxed so bulk writes rarely stall.
        // Universal compaction rewrites data far less than the default leveled
        // style (much lower write amplification), which keeps import speed from
        // collapsing once compaction engages. It trades disk space for speed,
        // which is the right call here. Measured ~1.7x faster than leveled on a
        // compaction-heavy import. The style is fixed at DB creation, so it is
        // applied on every open.
        int procs = Environment.ProcessorCount;
        _plan = MemoryPlan.Compute(memoryBudgetBytes ?? MemoryPlan.DefaultBudgetBytes(), procs);

        // Caps RocksDB's baseline memory (index/filter blocks + open file handles) to a fixed size
        // regardless of how large the db grows on disk. Applied on every open, bulk or read.
        var tableOptions = new BlockBasedTableOptions()
            .SetBlockCache(Cache.CreateLru((ulong)BlockCacheBytes))
            .SetCacheIndexAndFilterBlocks(true);

        var options = new DbOptions()
            .SetCreateIfMissing(true)
            .IncreaseParallelism(procs)
            .SetMaxBackgroundCompactions(procs)
            .SetMaxBackgroundFlushes(_plan.Flushes)
            .SetCompactionStyle(Compaction.Universal)
            .SetWriteBufferSize(_plan.WriteBuffer)     // sized from the memory budget
            .SetMaxWriteBufferNumber(_plan.MaxBuffers)
            .SetDbWriteBufferSize(_plan.DbWriteBufferCap)   // global memtable cap: forces a flush instead of growing
            .SetMinWriteBufferNumberToMerge(1)
            .SetTargetFileSizeBase(512ul * 1024 * 1024)
            .SetBytesPerSync(16ul * 1024 * 1024)
            .SetCompression(Compression.No)            // no compression: trade disk for CPU
            .SetBlockBasedTableFactory(tableOptions)
            .SetMaxOpenFiles(MaxOpenFilesCap);

        if (bulkLoad)
        {
            // Import at pure flush-only speed: never compact mid-import (that is
            // what makes a big import slow down). Files pile up harmlessly; run
            // `compact` afterwards to consolidate them for fast reads.
            options.SetMemtableVectorRep()             // fast bulk-insert memtable
                   .SetDisableAutoCompactions(1)
                   .SetLevel0FileNumCompactionTrigger(1 << 30)
                   .SetLevel0SlowdownWritesTrigger(1 << 30)
                   .SetLevel0StopWritesTrigger(1 << 30);
        }
        else
        {
            // Read/compact opens: let compaction run so the DB stays healthy, unless the caller
            // only wants this handle to serve reads between jobs (enableAutoCompaction: false).
            options.SetAllowConcurrentMemtableWrite(true)
                   .SetLevel0FileNumCompactionTrigger(4);
            if (!enableAutoCompaction) options.SetDisableAutoCompactions(1);
            // Lets one manual/auto compaction split its key range across several threads (the default
            // is 1 = one core). Not for bulk opens: an import never compacts. RocksDbSharp has no managed setter.
            Native.Instance.rocksdb_options_set_max_subcompactions(options.Handle, (uint)MaxSubcompactions);
        }

        _db = RocksDb.Open(options, path);
    }

    private static readonly byte[] Marker = { 1 };

    private byte[] MakeKey(string word)
    {
        var wordBytes = _enc.GetBytes(word);
        var key = new byte[4 + wordBytes.Length];
        BinaryPrimitives.WriteInt32BigEndian(key, word.Length);
        wordBytes.CopyTo(key, 4);
        return key;
    }

    private static byte[] LengthPrefix(int length)
    {
        var p = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(p, length);
        return p;
    }

    /// <summary>Approximate number of stored passwords (instant; from RocksDB estimate).</summary>
    public long EstimatedCount()
    {
        var s = _db.GetProperty("rocksdb.estimate-num-keys");
        return long.TryParse(s, out var n) ? n : 0;
    }

    /// <summary>
    /// Number of SST files currently sitting at level 0 (instant; from RocksDB's internal stats).
    /// Mainly a test/diagnostic hook: with auto-compaction suppressed this should stay flat across
    /// repeated imports with no explicit <see cref="Compact"/>; with it enabled it should collapse.
    /// </summary>
    public long NumFilesAtLevel0()
    {
        var s = _db.GetProperty("rocksdb.num-files-at-level0");
        return long.TryParse(s, out var n) ? n : 0;
    }

    /// <summary>Total size in bytes of all live SST files (instant; test/diagnostic hook), or -1 if unavailable.</summary>
    public long TotalSstFilesBytes()
    {
        var s = _db.GetProperty("rocksdb.total-sst-files-size");
        return long.TryParse(s, out var n) ? n : -1;
    }

    /// <summary>Raw <c>rocksdb.aggregated-table-properties</c> text (diagnostics; parse with <see cref="MemoryDiagnostics"/>), or null.</summary>
    public string? AggregatedTableProperties() => _db.GetProperty("rocksdb.aggregated-table-properties");

    /// <summary>L0 count for the parallel-scan gate: long.MaxValue (unknown, so single-thread) if the property is unreadable.</summary>
    internal long NumFilesAtLevel0ForGate() => ParseL0ForGate(L0PropertyHook != null ? L0PropertyHook() : _db.GetProperty("rocksdb.num-files-at-level0"));

    internal static long ParseL0ForGate(string? s) => long.TryParse(s, out var n) && n >= 0 ? n : long.MaxValue;

    /// <summary>Test seam: replaces the raw L0 property text read by the gate.</summary>
    internal Func<string?>? L0PropertyHook;

    /// <summary>The block cache's configured capacity in bytes, straight from the engine (test/diagnostic hook).</summary>
    public long BlockCacheCapacityBytes()
    {
        var s = _db.GetProperty("rocksdb.block-cache-capacity");
        return long.TryParse(s, out var n) ? n : -1;
    }

    /// <summary>
    /// Bulk-import passwords from a sequence of lines. Blank lines are skipped.
    /// Optional min/max restrict which character lengths get stored.
    /// WAL is disabled for speed: if the process is killed mid-import, re-run it
    /// (already-stored passwords simply dedup again). Returns lines read.
    /// </summary>
    public long Import(IEnumerable<string> lines, int? min, int? max,
                       Action<long>? onProgress = null, int reportEvery = 50_000, int threads = 0)
    {
        // A single writer thread caps at the memtable insert rate of one core.
        // We pipeline: this thread reads lines into chunks; several worker threads
        // build keys and write concurrently (concurrent memtable writes are on).
        // Worker/queue counts and the per-chunk size cap come from the memory plan; an explicit
        // thread count is honored only up to what the budget allows.
        int workers = threads > 0 ? Math.Min(threads, _plan.Workers) : _plan.Workers;
        const int chunkSize = 50_000;
        int chunkChars = _plan.ChunkChars;
        var queue = new BlockingCollection<string[]>(boundedCapacity: _plan.QueueCapacity);

        var tasks = new Task[workers];
        for (int w = 0; w < workers; w++)
            tasks[w] = Task.Run(() => Worker(queue, min, max));

        long read = 0;
        long sinceReport = 0;
        var chunk = new string[chunkSize];
        int ci = 0;
        long chars = 0;
        foreach (var raw in lines)
        {
            read++;
            if (++sinceReport >= reportEvery) { sinceReport = 0; onProgress?.Invoke(read); }
            chunk[ci++] = raw;
            chars += raw.Length;
            // Flush by count OR size: lines can be thousands of chars, so a count-only chunk can reach hundreds of MB.
            if (ci == chunkSize || chars >= chunkChars)
            {
                if (ci == chunkSize) { queue.Add(chunk); chunk = new string[chunkSize]; }
                else queue.Add(chunk[..ci]);   // size-capped: copy out, reuse the buffer
                ci = 0;
                chars = 0;
            }
        }
        if (ci > 0) queue.Add(chunk[..ci]);
        queue.CompleteAdding();
        Task.WaitAll(tasks);

        // Make the import durable and queryable now that WAL was off.
        _db.Flush(new FlushOptions().SetWaitForFlush(true));
        return read;
    }

    private void Worker(BlockingCollection<string[]> queue, int? min, int? max)
    {
        var writeOpts = new WriteOptions().DisableWal(1);
        byte[] keyBuf = new byte[4 + 256];   // reused; grows for long words
        foreach (var chunk in queue.GetConsumingEnumerable())
        {
            ReadOnlySpan<byte> marker = Marker;
            using var batch = new WriteBatch();
            foreach (var raw in chunk)
            {
                ReadOnlySpan<char> word = raw.EndsWith('\r') ? raw.AsSpan(0, raw.Length - 1) : raw;
                if (word.Length == 0) continue;
                int len = word.Length;
                if (min.HasValue && len < min.Value) continue;
                if (max.HasValue && len > max.Value) continue;

                int maxBytes = 4 + _enc.GetMaxByteCount(word.Length);
                if (keyBuf.Length < maxBytes) keyBuf = new byte[maxBytes];
                BinaryPrimitives.WriteInt32BigEndian(keyBuf, len);
                int n = _enc.GetBytes(word, keyBuf.AsSpan(4));
                batch.Put(keyBuf.AsSpan(0, 4 + n), marker, null);
            }
            _db.Write(batch, writeOpts);
        }
    }

    /// <summary>Exact-string membership test (single point lookup).</summary>
    public bool Contains(string word) => _db.Get(MakeKey(word)) != null;

    /// <summary>
    /// Fully compact the database into as few sorted files as possible. Import
    /// defers compaction for speed, which leaves many files that slow reads;
    /// running this afterwards restores fast lookups and range scans. It rewrites
    /// the whole database, so it takes a while on a large one.
    /// </summary>
    public void Compact() => _db.CompactRange((byte[]?)null, (byte[]?)null, (ColumnFamilyHandle?)null);

    /// <summary>
    /// Read options for one scan: fill_cache off (a full scan must not evict the shared 256 MB block cache
    /// that also holds index/filter blocks), optional readahead, optional exclusive upper key bound and an
    /// optional snapshot. The bound lives in unmanaged memory because the C API keeps a pointer to the
    /// caller's bytes (not a copy): it must outlive the iterator, so dispose this AFTER the iterator
    /// (declare it first in a using chain).
    /// </summary>
    internal sealed class ScanReadOptions : IDisposable
    {
        public ReadOptions Options { get; } = new ReadOptions();
        private IntPtr _bound;

        public ScanReadOptions(ulong readahead, byte[]? upperBound = null, Snapshot? snapshot = null)
        {
            Options.SetFillCache(false);
            if (readahead > 0) Options.SetReadaheadSize(readahead);
            if (snapshot != null) Options.SetSnapshot(snapshot);
            if (upperBound != null)
            {
                _bound = System.Runtime.InteropServices.Marshal.AllocHGlobal(Math.Max(1, upperBound.Length));
                System.Runtime.InteropServices.Marshal.Copy(upperBound, 0, _bound, upperBound.Length);
                Native.Instance.rocksdb_readoptions_set_iterate_upper_bound(Options.Handle, _bound, (UIntPtr)upperBound.Length);
            }
        }

        public void Dispose()
        {
            if (_bound != IntPtr.Zero) { System.Runtime.InteropServices.Marshal.FreeHGlobal(_bound); _bound = IntPtr.Zero; }
        }
    }

    /// <summary>Readahead for bulk scans (counts, breakdown); measured in agent-reports/multicore-scans/benchmark.md.</summary>
    internal static ulong BulkReadaheadBytes = 0;

    // Key prefix just past every key of length <= max, or null when no such bound exists (max is open or int.MaxValue).
    private static byte[]? UpperBoundFor(int? max) =>
        max is >= 0 and < int.MaxValue ? LengthPrefix(max.Value + 1) : null;

    // Length / word of the iterator's current key straight from the native buffer (no per-key byte[]).
    private static int CurrentLength(Iterator it) => BinaryPrimitives.ReadInt32BigEndian(it.GetKeySpan());
    private string CurrentWord(Iterator it) => _enc.GetString(it.GetKeySpan().Slice(4));

    /// <summary>
    /// Stream every stored password whose length is in [min, max]. Either bound may
    /// be null (open-ended). Results come out ordered by length, then by password.
    /// An optional limit caps how many are returned.
    /// </summary>
    public IEnumerable<string> Range(int? min, int? max, long? limit)
    {
        int lo = min ?? 0;
        long produced = 0;

        using var ro = new ScanReadOptions(0, UpperBoundFor(max));
        using var it = _db.NewIterator(readOptions: ro.Options);
        for (it.Seek(LengthPrefix(lo)); it.Valid(); it.Next())
        {
            int len = CurrentLength(it);
            if (max.HasValue && len > max.Value) yield break;   // sorted: nothing longer follows in-range

            yield return CurrentWord(it);
            if (limit.HasValue && ++produced >= limit.Value) yield break;
        }
    }

    /// <summary>
    /// Resumable, length-bounded scan: streams every stored password whose length is in [min, max],
    /// in the same length-then-password order as <see cref="Range"/>. Pass <paramref name="resumeAfterWord"/>
    /// (the last word returned by a previous call) to continue right after it instead of restarting
    /// from the beginning - lets a caller release the read lease between chunks and re-seek without
    /// re-scanning what it already consumed. There is no delete path in this app, so a resume word
    /// that no longer exists is not a real case; the seek still lands at the next key >= that key,
    /// which is correct either way. Either bound may be null (open-ended).
    /// </summary>
    public IEnumerable<string> Scan(int? min, int? max, string? resumeAfterWord)
    {
        int lo = min ?? 0;
        using var ro = new ScanReadOptions(0, UpperBoundFor(max));
        using var it = _db.NewIterator(readOptions: ro.Options);
        if (resumeAfterWord != null)
        {
            // Resume strictly after the given word: seek to its key, then step once past it (or past
            // whatever sorts just after it, if the exact key is gone - no deletes in this app, so that
            // never actually happens, but Next() is still the correct, safe thing to do either way).
            it.Seek(MakeKey(resumeAfterWord));
            if (it.Valid()) it.Next();
        }
        else
        {
            it.Seek(LengthPrefix(lo));
        }
        for (; it.Valid(); it.Next())
        {
            int len = CurrentLength(it);
            if (len < lo) continue;                              // only reachable via a resume seek landing short
            if (max.HasValue && len > max.Value) yield break;    // sorted: nothing longer follows in-range
            yield return CurrentWord(it);
        }
    }

    /// <summary>Keys between cancellation checks / progress callbacks in <see cref="LengthDistribution"/>.</summary>
    public const int ScanCheckInterval = 65_536;

    public void Dispose() => _db.Dispose();
}

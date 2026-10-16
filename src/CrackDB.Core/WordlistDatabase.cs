using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text;
using RocksDbSharp;

namespace CrackDB;

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
public sealed class WordlistDatabase : IDisposable
{
    private readonly RocksDb _db;
    private readonly Encoding _enc;
    private readonly MemoryPlan _plan;

    /// <summary>The memory plan this handle was opened with (write buffers, import pipeline sizes).</summary>
    public MemoryPlan Plan => _plan;

    /// <param name="bulkLoad">
    /// Open tuned for a large import: a vector memtable (O(1) append inserts,
    /// sorted once at flush) instead of the default skiplist, which roughly
    /// halves import time. Read-only commands leave this false.
    /// </param>
    /// <param name="memoryBudgetBytes">
    /// Memory this handle may use for memtables and the import pipeline; null = half of the
    /// memory available to the process (see <see cref="MemoryPlan.DefaultBudgetBytes"/>).
    /// </param>
    public WordlistDatabase(string path, Encoding encoding, bool bulkLoad = false, long? memoryBudgetBytes = null)
    {
        _enc = encoding;

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
            .SetCompression(Compression.No);           // no compression: trade disk for CPU

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
            // Read/compact opens: let compaction run so the DB stays healthy.
            options.SetAllowConcurrentMemtableWrite(true)
                   .SetLevel0FileNumCompactionTrigger(4);
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
    /// Stream every stored password whose length is in [min, max]. Either bound may
    /// be null (open-ended). Results come out ordered by length, then by password.
    /// An optional limit caps how many are returned.
    /// </summary>
    public IEnumerable<string> Range(int? min, int? max, long? limit)
    {
        int lo = min ?? 0;
        long produced = 0;

        var readOpts = new ReadOptions();
        using var it = _db.NewIterator(readOptions: readOpts);
        for (it.Seek(LengthPrefix(lo)); it.Valid(); it.Next())
        {
            var key = it.Key();
            int len = BinaryPrimitives.ReadInt32BigEndian(key);
            if (max.HasValue && len > max.Value) yield break;   // sorted: nothing longer follows in-range

            yield return _enc.GetString(key, 4, key.Length - 4);
            if (limit.HasValue && ++produced >= limit.Value) yield break;
        }
    }

    /// <summary>Exact count of passwords with length in [min, max] (full range scan).</summary>
    public long CountRange(int? min, int? max)
    {
        int lo = min ?? 0;
        long count = 0;
        using var it = _db.NewIterator();
        for (it.Seek(LengthPrefix(lo)); it.Valid(); it.Next())
        {
            int len = BinaryPrimitives.ReadInt32BigEndian(it.Key());
            if (max.HasValue && len > max.Value) break;
            count++;
        }
        return count;
    }

    /// <summary>Exact (length, count) pairs, ordered by length (full scan).</summary>
    public IEnumerable<(int length, long count)> LengthDistribution()
    {
        using var it = _db.NewIterator();
        it.SeekToFirst();
        int current = -1;
        long count = 0;
        while (it.Valid())
        {
            int len = BinaryPrimitives.ReadInt32BigEndian(it.Key());
            if (len != current)
            {
                if (current >= 0) yield return (current, count);
                current = len;
                count = 0;
            }
            count++;
            it.Next();
        }
        if (current >= 0) yield return (current, count);
    }

    public void Dispose() => _db.Dispose();
}

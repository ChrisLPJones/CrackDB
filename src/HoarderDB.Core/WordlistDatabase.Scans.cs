using System.Buffers.Binary;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using RocksDbSharp;

namespace HoarderDB;

/// <summary>
/// Full-database counting scans (<see cref="CountRange"/>, <see cref="LengthDistribution"/>): a single-thread
/// path (also the path for small databases and ScanThreads = 1) and a tiled parallel path.
///
/// Tiling: ordered cut keys c1 &lt; c2 &lt; ... split the key space into [start,c1) [c1,c2) ... [cn,end).
/// Tiles cannot overlap or leave a gap for ANY data (a key either sorts below a cut or not), so correctness
/// never depends on the data; data only affects how balanced the tiles are. Every tile is scanned with its own
/// iterator over ONE shared snapshot, so the union equals a single point-in-time scan.
/// </summary>
public sealed partial class WordlistDatabase
{
    /// <summary>Below this many (estimated) keys a scan stays single-threaded: thread setup would cost more than it saves.</summary>
    internal static long ParallelMinKeys = 1_000_000;   // tests lower it to exercise the parallel path on small fixtures

    /// <summary>Test seam: called by a scan worker at the start of every tile (null in production).</summary>
    internal Action<Tile>? TileHook;

    /// <summary>
    /// Parallel scans open one iterator per tile and each holds the index block of every L0 file (fill_cache off),
    /// so index memory grows with tiles x L0 files (measured 3x for 3 tiles). Above this many L0 files scans stay
    /// single-threaded; results are identical, a compaction restores the speed-up.
    /// </summary>
    internal const int ParallelMaxL0Files = 16;

    private bool ParallelAllowedByL0() => NumFilesAtLevel0() <= ParallelMaxL0Files;

    /// <summary>Scan threads this handle's process-wide limiter allows (diagnostic).</summary>
    public int ScanThreads => _scanLimiter.Cap;

    /// <summary>How often the calling thread publishes the workers' shared counter to the progress callback.</summary>
    internal static int ProgressPollMs = 200;

    internal readonly record struct Tile(byte[]? Lower, byte[]? Upper);

    private static int Compare(byte[] a, byte[] b) => a.AsSpan().SequenceCompareTo(b);

    // The next length prefix after `length` in key order (unsigned), or null for the very last one.
    private static byte[]? NextLengthPrefix(int length)
    {
        uint u = unchecked((uint)length);
        if (u == uint.MaxValue) return null;
        var p = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(p, u + 1);
        return p;
    }

    /// <summary>
    /// Turns arbitrary cut keys into tiles that cover [start, end) exactly once. Cuts outside (start, end),
    /// duplicates and unsorted input are all tolerated; this is the single place the no-gap/no-overlap
    /// property rests on. start == null means "from the first key", end == null "to the last".
    /// </summary>
    internal static List<Tile> MakeTiles(IEnumerable<byte[]> cuts, byte[]? start, byte[]? end)
    {
        var tiles = new List<Tile>();
        if (start != null && end != null && Compare(start, end) >= 0) return tiles;   // empty range
        var sorted = cuts.Where(c => c.Length > 0
                                     && (start == null || Compare(c, start) > 0)
                                     && (end == null || Compare(c, end) < 0))
                         .ToList();
        sorted.Sort(Compare);
        byte[]? lower = start;
        byte[]? prev = null;
        foreach (var c in sorted)
        {
            if (prev != null && Compare(prev, c) == 0) continue;
            tiles.Add(new Tile(lower, c));
            lower = c;
            prev = c;
        }
        tiles.Add(new Tile(lower, end));
        return tiles;
    }

    // ---- Cut planning -----------------------------------------------------------------------------

    private ulong ApproximateSize(byte[] start, byte[]? end)
    {
        // End null = past the last possible length prefix; 0xFF * 8 sorts after any realistic key.
        end ??= new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF };
        IntPtr sk = Marshal.AllocHGlobal(start.Length), ek = Marshal.AllocHGlobal(end.Length);
        IntPtr arr = Marshal.AllocHGlobal(IntPtr.Size * 2), lens = Marshal.AllocHGlobal(IntPtr.Size * 2), sizes = Marshal.AllocHGlobal(8);
        try
        {
            Marshal.Copy(start, 0, sk, start.Length);
            Marshal.Copy(end, 0, ek, end.Length);
            Marshal.WriteIntPtr(arr, 0, sk);        // start_keys[0]
            Marshal.WriteIntPtr(arr, IntPtr.Size, ek);   // limit_keys[0]
            Marshal.WriteIntPtr(lens, 0, (IntPtr)start.Length);
            Marshal.WriteIntPtr(lens, IntPtr.Size, (IntPtr)end.Length);
            Marshal.WriteInt64(sizes, 0);
            Native.Instance.rocksdb_approximate_sizes(_db.Handle, 1,
                arr, lens, arr + IntPtr.Size, lens + IntPtr.Size, sizes);
            return (ulong)Marshal.ReadInt64(sizes);
        }
        finally
        {
            Marshal.FreeHGlobal(sk); Marshal.FreeHGlobal(ek); Marshal.FreeHGlobal(arr); Marshal.FreeHGlobal(lens); Marshal.FreeHGlobal(sizes);
        }
    }

    // Distinct values of the byte following `parent` among the keys that start with it (seek-hopping, so the
    // cost is one seek per distinct byte, not per key). Only used on big prefixes.
    private List<byte> HopNextBytes(byte[] parent, Snapshot snapshot, CancellationToken ct)
    {
        var found = new List<byte>();
        using var ro = new ScanReadOptions(0, null, snapshot);
        using var it = _db.NewIterator(readOptions: ro.Options);
        it.Seek(parent);
        while (it.Valid())
        {
            ct.ThrowIfCancellationRequested();
            var key = it.GetKeySpan();
            if (key.Length <= parent.Length)
            {
                if (key.Length == parent.Length && key.SequenceEqual(parent)) { it.Next(); continue; }   // the bare prefix itself
                break;
            }
            if (!key.StartsWith(parent)) break;
            byte b = key[parent.Length];
            found.Add(b);
            if (b == 0xFF) break;
            var next = new byte[parent.Length + 1];
            parent.CopyTo(next, 0);
            next[parent.Length] = (byte)(b + 1);
            it.Seek(next);
        }
        return found;
    }

    /// <summary>
    /// Picks cut keys for [start, end): one at every present length (found by seek-hopping), and for a length
    /// that holds more than `splitBytes` of data, one per present first password byte, and once more per second
    /// byte when a first-byte slice is still that big. Sizes are RocksDB's approximate on-disk sizes: wrong
    /// guesses only unbalance the tiles, never the result.
    /// </summary>
    internal List<byte[]> PlanCuts(byte[]? start, byte[]? end, long splitBytes, Snapshot snapshot, CancellationToken ct)
    {
        var cuts = new List<byte[]>();
        var lengths = new List<(byte[] prefix, byte[]? next)>();
        using (var ro = new ScanReadOptions(0, null, snapshot))
        using (var it = _db.NewIterator(readOptions: ro.Options))
        {
            if (start == null) it.SeekToFirst(); else it.Seek(start);
            while (it.Valid())
            {
                ct.ThrowIfCancellationRequested();
                var key = it.GetKeySpan();
                if (key.Length < 4) break;                                   // not a key of this layout: leave it to the tiles
                if (end != null && key.SequenceCompareTo(end) >= 0) break;
                var prefix = key[..4].ToArray();
                var next = NextLengthPrefix(BinaryPrimitives.ReadInt32BigEndian(prefix));
                lengths.Add((prefix, next));
                if (next == null) break;
                it.Seek(next);
            }
        }

        foreach (var (prefix, next) in lengths)
        {
            cuts.Add(prefix);
            if (splitBytes <= 0) continue;
            if (ApproximateSize(prefix, next) <= (ulong)splitBytes) continue;
            foreach (byte b1 in HopNextBytes(prefix, snapshot, ct))
            {
                var p1 = new byte[5]; prefix.CopyTo(p1, 0); p1[4] = b1;
                cuts.Add(p1);
                byte[]? p1End = b1 == 0xFF ? next : new[] { prefix[0], prefix[1], prefix[2], prefix[3], (byte)(b1 + 1) };
                if (ApproximateSize(p1, p1End) <= (ulong)splitBytes) continue;
                foreach (byte b2 in HopNextBytes(p1, snapshot, ct))
                {
                    var p2 = new byte[6]; p1.CopyTo(p2, 0); p2[5] = b2;
                    cuts.Add(p2);
                }
            }
        }
        return cuts;
    }

    // ---- Parallel engine --------------------------------------------------------------------------

    private sealed class ScanContext
    {
        public Snapshot Snapshot = null!;
        public CancellationToken Ct;
        public long Shared;                       // keys scanned by all workers (Interlocked)
        public void Add(long n) => Interlocked.Add(ref Shared, n);
    }

    /// <summary>
    /// Runs `work` for every tile on up to `threads` dedicated threads (permits from the process-wide limiter).
    /// The calling thread scans nothing: it waits, polls the shared counter and calls `progress` (so the callback
    /// is single-threaded and non-decreasing), and ALWAYS joins every worker before returning or throwing, so a
    /// caller's db lease is never released under a live iterator. Cancellation or the first worker fault stops
    /// the rest; a fault is rethrown after the join. Returns each worker's state.
    /// </summary>
    private List<TState> RunTiles<TState>(IReadOnlyList<Tile> tiles, int threads, CancellationToken ct, Action<long>? progress,
        Snapshot snapshot, Func<TState> newState, Action<TState, Tile, ScanContext> work)
    {
        int want = Math.Min(threads, tiles.Count);
        int permits = _scanLimiter.AcquireUpTo(want, ct);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var ctx = new ScanContext { Snapshot = snapshot, Ct = linked.Token };
        var states = new List<TState>();
        var started = new List<Thread>();
        Exception? fault = null;
        int next = -1;
        var done = new CountdownEvent(1);
        try
        {
            for (int i = 0; i < permits; i++)
            {
                var state = newState();
                states.Add(state);
                done.AddCount();
                var t = new Thread(() =>
                {
                    try
                    {
                        while (true)
                        {
                            linked.Token.ThrowIfCancellationRequested();
                            int idx = Interlocked.Increment(ref next);
                            if (idx >= tiles.Count) break;
                            TileHook?.Invoke(tiles[idx]);
                            work(state, tiles[idx], ctx);
                        }
                    }
                    catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
                    catch (Exception e)
                    {
                        Interlocked.CompareExchange(ref fault, e, null);
                        linked.Cancel();
                    }
                    finally { done.Signal(); }
                })
                { IsBackground = true, Name = "hoarderdb-scan" };
                try { t.Start(); started.Add(t); }
                catch { done.Signal(); throw; }
            }
            done.Signal();   // the initial count: workers still hold the rest

            long reported = 0;
            while (!done.Wait(ProgressPollMs, ct))
            {
                long cur = Volatile.Read(ref ctx.Shared);
                if (cur > reported) { reported = cur; progress?.Invoke(cur); }
            }
        }
        finally
        {
            linked.Cancel();                       // no-op after a normal finish; stops workers on cancel/fault/throwing callback
            foreach (var t in started) t.Join();
            _scanLimiter.Release(permits);
            done.Dispose();
        }
        if (fault != null) ExceptionDispatchInfo.Capture(fault).Throw();
        ct.ThrowIfCancellationRequested();
        progress?.Invoke(Volatile.Read(ref ctx.Shared));
        return states;
    }

    // Reference threshold: a length bigger than total / (threads * 8) is split on password bytes.
    private long SplitThreshold(int threads)
    {
        ulong total = ApproximateSize(new byte[4], null);
        return total == 0 ? 0 : (long)(total / (ulong)(threads * 8L));
    }

    private int EffectiveThreads(int? requested) => Math.Max(1, Math.Min(requested ?? _scanLimiter.Cap, _scanLimiter.Cap));

    // ---- CountRange -------------------------------------------------------------------------------

    /// <summary>Exact count of passwords with length in [min, max] (full range scan).</summary>
    public long CountRange(int? min, int? max) =>
        CountRangeCore(min, max, threads: null, minKeys: null, splitBytesOverride: null, default);

    internal long CountRangeCore(int? min, int? max, int? threads, long? minKeys, long? splitBytesOverride, CancellationToken ct)
    {
        int lo = min ?? 0;
        int t = EffectiveThreads(threads);
        var upper = UpperBoundFor(max);
        bool bounded = lo >= 0 && (max == null || upper != null || max == int.MaxValue);
        if (t > 1 && bounded && EstimatedCount() >= (minKeys ?? ParallelMinKeys) && ParallelAllowedByL0())
            return CountRangeParallel(LengthPrefix(lo), upper, t, splitBytesOverride, ct);
        return CountRangeSingle(min, max);
    }

    /// <summary>Single-thread count: with a usable upper bound it counts without reading a single key.</summary>
    internal long CountRangeSingle(int? min, int? max)
    {
        int lo = min ?? 0;
        var upper = UpperBoundFor(max);
        bool keyless = max == null || upper != null || max == int.MaxValue;
        if (!(lo >= 0 && keyless)) return CountRangeReference(min, max);   // odd (negative) bounds: keep the key check
        long count = 0;
        using var ro = new ScanReadOptions(BulkReadaheadBytes, upper);
        using var it = _db.NewIterator(readOptions: ro.Options);
        for (it.Seek(LengthPrefix(lo)); it.Valid(); it.Next()) count++;
        return count;
    }

    /// <summary>The original implementation, kept verbatim as the test oracle and the "before" benchmark.</summary>
    internal long CountRangeReference(int? min, int? max)
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

    private long CountRangeParallel(byte[] start, byte[]? end, int threads, long? splitBytesOverride, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var snapshot = _db.CreateSnapshot();
        var cuts = PlanCuts(start, end, splitBytesOverride ?? SplitThreshold(threads), snapshot, ct);
        var tiles = MakeTiles(cuts, start, end);
        var states = RunTiles(tiles, threads, ct, null, snapshot, () => new long[1], (acc, tile, ctx) =>
        {
            acc[0] += CountTile(tile, ctx);
        });
        long total = 0;
        foreach (var a in states) total += a[0];
        return total;
    }

    // Counts a tile's keys without reading any key bytes (the tile bounds already fix what it holds).
    private long CountTile(Tile tile, ScanContext ctx)
    {
        long n = 0, since = 0;
        using var ro = new ScanReadOptions(BulkReadaheadBytes, tile.Upper, ctx.Snapshot);
        using var it = _db.NewIterator(readOptions: ro.Options);
        if (tile.Lower == null) it.SeekToFirst(); else it.Seek(tile.Lower);
        for (; it.Valid(); it.Next())
        {
            n++;
            if (++since == ScanCheckInterval) { since = 0; ctx.Add(ScanCheckInterval); ctx.Ct.ThrowIfCancellationRequested(); }
        }
        ctx.Add(since);
        return n;
    }

    // ---- LengthDistribution -----------------------------------------------------------------------

    /// <summary>
    /// Exact (length, count) pairs, ordered by length (full scan). The scan checks <paramref name="ct"/> and
    /// reports the number of keys read to <paramref name="progress"/> (a cancelled token throws
    /// <see cref="OperationCanceledException"/>), so a multi-hour scan can be stopped. Large databases scan on
    /// several threads (see <see cref="ScanLimiter"/>); <paramref name="progress"/> is then called from the
    /// calling thread only, about every 200 ms, never decreasing, and the result is computed eagerly. Small
    /// databases or ScanThreads = 1 use the lazy single-thread scan (progress every <see cref="ScanCheckInterval"/> keys).
    /// </summary>
    public IEnumerable<(int length, long count)> LengthDistribution(CancellationToken ct = default, Action<long>? progress = null) =>
        LengthDistributionCore(threads: null, minKeys: null, splitBytesOverride: null, ct, progress);

    internal IEnumerable<(int length, long count)> LengthDistributionCore(int? threads, long? minKeys, long? splitBytesOverride,
        CancellationToken ct, Action<long>? progress)
    {
        int t = EffectiveThreads(threads);
        if (t > 1 && EstimatedCount() >= (minKeys ?? ParallelMinKeys) && ParallelAllowedByL0())
        {
            ct.ThrowIfCancellationRequested();
            return LengthDistributionParallel(t, splitBytesOverride, ct, progress);
        }
        return LengthDistributionSingle(ct, progress);
    }

    private (int length, long count)[] LengthDistributionParallel(int threads, long? splitBytesOverride, CancellationToken ct, Action<long>? progress)
    {
        using var snapshot = _db.CreateSnapshot();
        var cuts = PlanCuts(null, null, splitBytesOverride ?? SplitThreshold(threads), snapshot, ct);
        var tiles = MakeTiles(cuts, null, null);
        var states = RunTiles(tiles, threads, ct, progress, snapshot, () => new Dictionary<int, long>(), (acc, tile, ctx) =>
        {
            using var ro = new ScanReadOptions(BulkReadaheadBytes, tile.Upper, ctx.Snapshot);
            using var it = _db.NewIterator(readOptions: ro.Options);
            if (tile.Lower == null) it.SeekToFirst(); else it.Seek(tile.Lower);
            long since = 0;

            if (IsSingleLength(tile, out int fixedLen))
            {
                // Both bounds lie inside one length, so every key here has that length: just count.
                long n = 0;
                for (; it.Valid(); it.Next())
                {
                    n++;
                    if (++since == ScanCheckInterval) { since = 0; ctx.Add(ScanCheckInterval); ctx.Ct.ThrowIfCancellationRequested(); }
                }
                if (n > 0) acc[fixedLen] = acc.GetValueOrDefault(fixedLen) + n;
            }
            else
            {
                int cur = 0; long cnt = 0; bool any = false;
                for (; it.Valid(); it.Next())
                {
                    int len = CurrentLength(it);
                    if (!any || len != cur)
                    {
                        if (any) acc[cur] = acc.GetValueOrDefault(cur) + cnt;
                        cur = len; cnt = 0; any = true;
                    }
                    cnt++;
                    if (++since == ScanCheckInterval) { since = 0; ctx.Add(ScanCheckInterval); ctx.Ct.ThrowIfCancellationRequested(); }
                }
                if (any) acc[cur] = acc.GetValueOrDefault(cur) + cnt;
            }
            ctx.Add(since);
        });

        var merged = new Dictionary<int, long>();
        foreach (var d in states)
            foreach (var (len, n) in d) merged[len] = merged.GetValueOrDefault(len) + n;
        // Key order is the unsigned order of the big-endian prefix.
        return merged.OrderBy(kv => unchecked((uint)kv.Key)).Select(kv => (kv.Key, kv.Value)).ToArray();
    }

    // True when [Lower, Upper) can only contain keys of one length (which is returned).
    private static bool IsSingleLength(Tile tile, out int length)
    {
        length = 0;
        if (tile.Lower is not { Length: >= 4 } lower || tile.Upper is not { Length: >= 4 } upper) return false;
        length = BinaryPrimitives.ReadInt32BigEndian(lower);
        if (upper.AsSpan(0, 4).SequenceEqual(lower.AsSpan(0, 4))) return true;
        // Upper is exactly the next length's bare prefix.
        var nextPrefix = NextLengthPrefix(length);
        return nextPrefix != null && upper.Length == 4 && upper.AsSpan().SequenceEqual(nextPrefix);
    }

    /// <summary>Single-thread scan (zero-allocation key reads, fill_cache off). Lazy.</summary>
    internal IEnumerable<(int length, long count)> LengthDistributionSingle(CancellationToken ct, Action<long>? progress)
    {
        ct.ThrowIfCancellationRequested();
        using var ro = new ScanReadOptions(BulkReadaheadBytes);
        using var it = _db.NewIterator(readOptions: ro.Options);
        it.SeekToFirst();
        int current = -1;
        long count = 0, scanned = 0;
        while (it.Valid())
        {
            int len = CurrentLength(it);
            if (len != current)
            {
                if (current >= 0) yield return (current, count);
                current = len;
                count = 0;
            }
            count++;
            if (++scanned % ScanCheckInterval == 0)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Invoke(scanned);
            }
            it.Next();
        }
        progress?.Invoke(scanned);
        if (current >= 0) yield return (current, count);
    }

    /// <summary>The original implementation, kept verbatim as the test oracle and the "before" benchmark.</summary>
    internal IEnumerable<(int length, long count)> LengthDistributionReference(CancellationToken ct = default, Action<long>? progress = null)
    {
        ct.ThrowIfCancellationRequested();
        using var it = _db.NewIterator();
        it.SeekToFirst();
        int current = -1;
        long count = 0, scanned = 0;
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
            if (++scanned % ScanCheckInterval == 0)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Invoke(scanned);
            }
            it.Next();
        }
        progress?.Invoke(scanned);
        if (current >= 0) yield return (current, count);
    }
}

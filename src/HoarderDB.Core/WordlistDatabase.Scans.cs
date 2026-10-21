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

    private bool ParallelAllowedByL0() => NumFilesAtLevel0ForGate() <= ParallelMaxL0Files;

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

    /// <summary>
    /// Planning never walks the keys: a seek or approximate-size call that touches a huge SST file re-reads that
    /// file's whole index block (hundreds of MB for a 70 GB file, never cacheable in the 256 MB block cache), so
    /// the old one-seek-per-length plan took over an hour on a 440 GB db. Cuts now come from the live-file
    /// metadata (key bounds and size per file, no I/O), and at most MaxPlanProbeRanges approximate-size ranges
    /// are spent splitting files that are still far bigger than a tile.
    /// </summary>
    internal static int MaxPlanTiles = 512;
    internal static int MaxPlanProbeRanges = 192;

    /// <summary>Approximate-size ranges the last PlanCuts spent (test/diagnostic).</summary>
    internal int LastPlanProbeRanges;

    internal readonly record struct LiveFile(byte[] Smallest, byte[] Largest, long Size);

    // Binary-safe key bounds straight from the native live-file list (the managed GetLiveFilesMetadata wrapper turns
    // keys into strings, which would mangle the NUL bytes of the length prefix).
    private List<LiveFile> LiveFiles()
    {
        var files = new List<LiveFile>();
        var native = Native.Instance;
        IntPtr lf = native.rocksdb_livefiles(_db.Handle);
        if (lf == IntPtr.Zero) return files;
        try
        {
            int n = native.rocksdb_livefiles_count(lf);
            for (int i = 0; i < n; i++)
            {
                var lo = ReadKey((IntPtr l, int ix, out UIntPtr sz) => native.rocksdb_livefiles_smallestkey(l, ix, out sz), lf, i);
                var hi = ReadKey((IntPtr l, int ix, out UIntPtr sz) => native.rocksdb_livefiles_largestkey(l, ix, out sz), lf, i);
                if (lo.Length > 0 && hi.Length > 0) files.Add(new LiveFile(lo, hi, (long)(ulong)native.rocksdb_livefiles_size(lf, i)));
            }
        }
        finally { native.rocksdb_livefiles_destroy(lf); }
        return files;
    }

    private delegate IntPtr KeyGetter(IntPtr lf, int index, out UIntPtr size);

    private static byte[] ReadKey(KeyGetter get, IntPtr lf, int index)
    {
        IntPtr p = get(lf, index, out var len);
        if (p == IntPtr.Zero || len == UIntPtr.Zero) return Array.Empty<byte>();
        var b = new byte[(int)len];
        Marshal.Copy(p, b, 0, b.Length);
        return b;
    }

    // Prefix of a key's length field (4 bytes) as a cut key, or null for a key too short to carry one.
    private static byte[]? PrefixOf(byte[] key) => key.Length < 4 ? null : key[..4];

    /// <summary>
    /// Approximate on-disk sizes of many [start, limit) ranges in as few native calls as possible (chunks of 16 so
    /// cancellation is checked between them).
    /// </summary>
    private ulong[] ApproximateSizes(IReadOnlyList<(byte[] start, byte[] limit)> ranges, CancellationToken ct)
    {
        var result = new ulong[ranges.Count];
        for (int off = 0; off < ranges.Count; off += 16)
        {
            ct.ThrowIfCancellationRequested();
            int n = Math.Min(16, ranges.Count - off);
            var ptrs = new List<IntPtr>();
            IntPtr starts = Marshal.AllocHGlobal(IntPtr.Size * n), limits = Marshal.AllocHGlobal(IntPtr.Size * n);
            IntPtr sl = Marshal.AllocHGlobal(IntPtr.Size * n), ll = Marshal.AllocHGlobal(IntPtr.Size * n), sizes = Marshal.AllocHGlobal(8 * n);
            try
            {
                IntPtr Copy(byte[] b) { var p = Marshal.AllocHGlobal(Math.Max(1, b.Length)); Marshal.Copy(b, 0, p, b.Length); ptrs.Add(p); return p; }
                for (int i = 0; i < n; i++)
                {
                    var (s, l) = ranges[off + i];
                    Marshal.WriteIntPtr(starts, i * IntPtr.Size, Copy(s));
                    Marshal.WriteIntPtr(limits, i * IntPtr.Size, Copy(l));
                    Marshal.WriteIntPtr(sl, i * IntPtr.Size, (IntPtr)s.Length);
                    Marshal.WriteIntPtr(ll, i * IntPtr.Size, (IntPtr)l.Length);
                    Marshal.WriteInt64(sizes, i * 8, 0);
                }
                Native.Instance.rocksdb_approximate_sizes(_db.Handle, n, starts, sl, limits, ll, sizes);
                for (int i = 0; i < n; i++) result[off + i] = (ulong)Marshal.ReadInt64(sizes, i * 8);
            }
            finally
            {
                foreach (var p in ptrs) Marshal.FreeHGlobal(p);
                Marshal.FreeHGlobal(starts); Marshal.FreeHGlobal(limits); Marshal.FreeHGlobal(sl); Marshal.FreeHGlobal(ll); Marshal.FreeHGlobal(sizes);
            }
        }
        return result;
    }

    // Picks, from candidates (key, cumulative bytes from the anchor) in key order, a cut each time the cumulative size
    // has grown by `target` since the previous cut.
    private static void PickBySize(List<(byte[] key, ulong cum)> candidates, long target, List<byte[]> cuts)
    {
        ulong last = 0, run = 0;
        foreach (var (key, c) in candidates)
        {
            run = Math.Max(run, c);                       // approximate sizes need not be monotonic
            if (run - last >= (ulong)Math.Max(1, target)) { cuts.Add(key); last = run; }
        }
    }

    /// <summary>
    /// Picks cut keys for [start, end) from the live-file metadata: a cut at a file's smallest key whenever
    /// `target` bytes of files have gone by (target = total / (threads * 8) unless overridden), plus the length
    /// prefix of every file's smallest and largest key (free, and it lets a tile that sits inside one length be
    /// counted without reading keys). A file much bigger than a tile is split at length boundaries (and, inside a
    /// bigger length, at first password bytes) by approximate-size probes, at most MaxPlanProbeRanges in all.
    /// At most MaxPlanTiles - 1 cuts come back. Wrong guesses only unbalance the tiles, never the result.
    /// </summary>
    internal List<byte[]> PlanCuts(byte[]? start, byte[]? end, int threads, long? targetOverride, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        LastPlanProbeRanges = 0;
        var files = LiveFiles()
            .Where(f => (start == null || Compare(f.Largest, start) >= 0) && (end == null || Compare(f.Smallest, end) < 0))
            .OrderBy(f => f.Smallest, Comparer<byte[]>.Create(Compare)).ToList();
        var cuts = new List<byte[]>();
        if (files.Count == 0) return cuts;

        long total = files.Sum(f => f.Size);
        long target = targetOverride is > 0 ? targetOverride.Value : Math.Max(1, total / Math.Max(1, threads * 8L));

        long acc = 0;
        foreach (var f in files)
        {
            if (acc >= target) { cuts.Add(f.Smallest); acc = 0; }
            acc += f.Size;
        }
        foreach (var f in files)
        {
            if (PrefixOf(f.Smallest) is { } a) cuts.Add(a);
            if (PrefixOf(f.Largest) is { } b) cuts.Add(b);
        }

        // Split the oversized files (biggest first) while the probe budget lasts.
        int budget = MaxPlanProbeRanges;
        foreach (var f in files.Where(f => f.Size > 2 * target).OrderByDescending(f => f.Size))
        {
            if (budget <= 0) break;
            ProbeFile(f, target, cuts, ref budget, ct);
        }
        LastPlanProbeRanges = MaxPlanProbeRanges - budget;

        // Dedup, and thin evenly if over the cap (the thinned-out cuts only make tiles bigger).
        var sorted = cuts.Where(c => (start == null || Compare(c, start) > 0) && (end == null || Compare(c, end) < 0)).ToList();
        sorted.Sort(Compare);
        var distinct = new List<byte[]>();
        foreach (var c in sorted) if (distinct.Count == 0 || Compare(distinct[^1], c) != 0) distinct.Add(c);
        int cap = Math.Max(0, MaxPlanTiles - 1);
        if (distinct.Count > cap)
        {
            var thin = new List<byte[]>(cap);
            for (int i = 0; i < cap; i++) thin.Add(distinct[(int)((long)i * distinct.Count / cap)]);
            distinct = thin;
        }
        return distinct;
    }

    internal void ProbeFile(LiveFile f, long target, List<byte[]> cuts, ref int budget, CancellationToken ct)
    {
        if (f.Smallest.Length < 4 || f.Largest.Length < 4) return;
        uint lo = BinaryPrimitives.ReadUInt32BigEndian(f.Smallest), hi = BinaryPrimitives.ReadUInt32BigEndian(f.Largest);
        if (hi <= lo || hi - lo > (uint)Math.Min(budget - 1, 128)) return;     // one length, or too many to probe cheaply

        // Level 1: sizes of [smallest, prefix(L)) for each length boundary L in (lo, hi], plus the whole file.
        var ranges = new List<(byte[] start, byte[] limit)>();
        for (ulong l = (ulong)lo + 1; l <= hi; l++) { var p = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(p, (uint)l); ranges.Add((f.Smallest, p)); }
        var whole = new byte[f.Largest.Length + 1]; f.Largest.CopyTo(whole, 0);
        ranges.Add((f.Smallest, whole));
        budget -= ranges.Count;
        var cum = ApproximateSizes(ranges, ct);

        var cand = new List<(byte[] key, ulong cum)>();
        for (int i = 0; i < ranges.Count - 1; i++) cand.Add((ranges[i].limit, cum[i]));
        PickBySize(cand, target, cuts);

        // Level 2: a length that alone holds more than two tiles is split on its first password byte (printable range).
        for (int i = 0; i < ranges.Count - 2; i++)
        {
            ulong gap = Math.Max(cum[i + 1], cum[i]) - cum[i];
            if (gap <= (ulong)(2 * target) || budget < 97) continue;
            var anchor = ranges[i].limit;                                   // prefix(L): the length starts here
            var r2 = new List<(byte[] start, byte[] limit)>();
            for (int b = 0x20; b <= 0x7F; b++) { var p = new byte[5]; anchor.CopyTo(p, 0); p[4] = (byte)b; r2.Add((anchor, p)); }
            budget -= r2.Count;
            var c2 = ApproximateSizes(r2, ct);
            var cand2 = new List<(byte[] key, ulong cum)>();
            for (int k = 0; k < r2.Count; k++) cand2.Add((r2[k].limit, c2[k]));
            PickBySize(cand2, target, cuts);
        }
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
        var cuts = PlanCuts(start, end, threads, splitBytesOverride, ct);
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
        // Visible life sign before planning: the first report (0 keys) lets a caller show a "planning" phase.
        progress?.Invoke(0);
        ct.ThrowIfCancellationRequested();
        using var snapshot = _db.CreateSnapshot();
        var cuts = PlanCuts(null, null, threads, splitBytesOverride, ct);
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

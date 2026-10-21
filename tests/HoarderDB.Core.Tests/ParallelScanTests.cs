using System.Text;
using HoarderDB;
using Xunit;

// These tests block on threads on purpose (they assert thread joins), so the async-test analyzer does not apply.
#pragma warning disable xUnit1031

namespace HoarderDB.Core.Tests;

/// <summary>
/// Parallel scans must equal the single-thread reference (the original implementation) for any data and any
/// thread count. All databases are Test_&lt;guid&gt; throwaway dirs (TempDb).
/// </summary>
public sealed class ParallelScanTests : IDisposable
{
    private readonly int _savedPoll = WordlistDatabase.ProgressPollMs;
    public void Dispose() => WordlistDatabase.ProgressPollMs = _savedPoll;

    private static readonly int[] ThreadCounts = { 1, 2, 3, 8, 64 };

    // ~200k keys: lengths 1..40 with gaps, ~85% in 8..10, length 25 sharing one first byte, one length with a
    // single key, non-ASCII / non-BMP words, NUL characters inside words.
    internal static List<string> SkewedWords()
    {
        var rnd = new Random(1234);
        const string alpha = "abcdefghijklmnopqrstuvwxyz0123456789";
        string Rand(int len) => string.Create(len, rnd, (span, r) => { for (int i = 0; i < span.Length; i++) span[i] = alpha[r.Next(alpha.Length)]; });
        var words = new List<string>();
        for (int i = 0; i < 170_000; i++) words.Add(Rand(8 + rnd.Next(3)));
        for (int i = 0; i < 25_000; i++) { int l = 1 + rnd.Next(40); if (l is >= 31 and <= 34 or 35 or 8 or 9 or 10) continue; if (l == 25) continue; words.Add(Rand(l)); }
        for (int i = 0; i < 4_000; i++) words.Add("x" + Rand(24));                       // length 25, one shared first byte
        words.Add(new string('q', 35));                                                    // the only length-35 key
        for (int i = 0; i < 300; i++) words.Add("é" + Rand(5) + "日本");                    // non-ASCII (2- and 3-byte chars)
        for (int i = 0; i < 300; i++) words.Add("😀" + Rand(6));                            // non-BMP: UTF-16 length 8
        for (int i = 0; i < 300; i++) words.Add("\0" + Rand(6));                           // 0x00 right after the prefix
        words.Add("\0"); words.Add("\0\0");
        return words;
    }

    private static void ImportInChunks(WordlistDatabase db, List<string> words, int parts)
    {
        // Several imports = several flushed L0 files.
        int n = words.Count / parts + 1;
        for (int i = 0; i < words.Count; i += n)
            db.Import(words.Skip(i).Take(n), null, null, threads: 2);
    }

    public static IEnumerable<object[]> Variants() { yield return new object[] { false }; yield return new object[] { true }; }

    [Theory, MemberData(nameof(Variants))]
    public void LengthDistribution_EqualsReference_AtAllThreadCounts(bool compacted)
    {
        using var t = new TempDb();
        using var db = t.Open(new ScanLimiter(64));
        ImportInChunks(db, SkewedWords(), 5);
        if (compacted) db.Compact();
        var expected = db.LengthDistributionReference().ToArray();
        Assert.True(expected.Length > 20);
        Assert.Contains(expected, e => e.length == 35 && e.count == 1);

        foreach (var threads in ThreadCounts)
            foreach (long? split in new long?[] { null, 1 })   // real size estimates, and "split everything" (2-level bytes)
            {
                var got = db.LengthDistributionCore(threads, minKeys: 0, split, default, null).ToArray();
                Assert.Equal(expected, got);
            }
        Assert.Equal(expected, db.LengthDistributionSingle(default, null).ToArray());
    }

    [Theory, MemberData(nameof(Variants))]
    public void CountRange_EqualsReference_AtAllThreadCounts(bool compacted)
    {
        using var t = new TempDb();
        using var db = t.Open(new ScanLimiter(64));
        ImportInChunks(db, SkewedWords(), 5);
        if (compacted) db.Compact();

        var ranges = new (int?, int?)[]
        {
            (null, null), (8, 8), (9, 10), (8, null), (null, 7), (1, 1), (35, 35), (31, 34), (36, 40), (50, 60),
            (10, 8), (0, 0), (null, int.MaxValue), (int.MaxValue, null), (25, 25), (-1, 5), (5, -1), (null, 0),
        };
        foreach (var (min, max) in ranges)
        {
            long expected = db.CountRangeReference(min, max);
            Assert.Equal(expected, db.CountRangeSingle(min, max));
            foreach (var threads in ThreadCounts)
                foreach (long? split in new long?[] { null, 1 })
                    Assert.Equal(expected, db.CountRangeCore(min, max, threads, 0, split, default));
        }
    }

    [Fact]
    public void BinaryKeys_Latin1_WithNulAndFF_AfterPrefix()
    {
        using var t = new TempDb();
        using var db = t.Open(new ScanLimiter(8), Encoding.Latin1);
        var words = new List<string>();
        var rnd = new Random(7);
        for (int i = 0; i < 20_000; i++)
        {
            int len = 3 + rnd.Next(3);
            words.Add(string.Create(len, rnd, (s, r) => { for (int k = 0; k < s.Length; k++) s[k] = (char)(r.Next(4) switch { 0 => 0, 1 => 0xFF, 2 => 0x80, _ => r.Next(256) }); }));
        }
        db.Import(words, null, null, threads: 2);
        var expected = db.LengthDistributionReference().ToArray();
        foreach (var threads in ThreadCounts)
            foreach (long? split in new long?[] { null, 1 })
                Assert.Equal(expected, db.LengthDistributionCore(threads, 0, split, default, null).ToArray());
        Assert.Equal(db.CountRangeReference(null, null), db.CountRangeCore(null, null, 8, 0, 1, default));
    }

    [Fact]
    public void Empty_And_SingleLength_Databases()
    {
        using var t = new TempDb();
        using var db = t.Open(new ScanLimiter(8));
        var seen = new List<long>();
        Assert.Empty(db.LengthDistributionCore(8, 0, null, default, seen.Add));
        Assert.Equal(0, db.CountRangeCore(null, null, 8, 0, null, default));
        Assert.Equal(new long[] { 0, 0 }, seen);   // the planning report, then the final one

        db.Import(Enumerable.Range(0, 3000).Select(i => "k" + i.ToString("D5")), null, null, threads: 2);
        var got = db.LengthDistributionCore(8, 0, 1, default, null).ToArray();
        Assert.Equal(new[] { (6, 3000L) }, got);
        Assert.Equal(3000, db.CountRangeCore(6, 6, 8, 0, 1, default));
        Assert.Equal(0, db.CountRangeCore(7, 9, 8, 0, 1, default));
    }

    // ---- tiling: random cut sets always tile exactly --------------------------------------------

    [Fact]
    public void MakeTiles_RandomCutSets_TileExactly()
    {
        var rnd = new Random(99);
        byte[] RandKey(int maxLen) { var k = new byte[rnd.Next(0, maxLen + 1)]; for (int i = 0; i < k.Length; i++) k[i] = (byte)(rnd.Next(3) switch { 0 => 0, 1 => 255, _ => rnd.Next(256) }); return k; }
        for (int round = 0; round < 300; round++)
        {
            var keys = Enumerable.Range(0, 400).Select(_ => RandKey(7)).ToList();
            var cuts = Enumerable.Range(0, rnd.Next(0, 40)).Select(_ => RandKey(6)).ToList();   // may be empty, duplicate, out of order
            byte[]? start = rnd.Next(3) == 0 ? null : RandKey(5);
            byte[]? end = rnd.Next(3) == 0 ? null : RandKey(5);
            var tiles = WordlistDatabase.MakeTiles(cuts, start, end);

            // Contiguous, no gaps or overlaps between neighbours.
            for (int i = 0; i + 1 < tiles.Count; i++) Assert.True(tiles[i].Upper!.AsSpan().SequenceEqual(tiles[i + 1].Lower));
            if (tiles.Count > 0)
            {
                Assert.Equal(start, tiles[0].Lower);
                Assert.Equal(end, tiles[^1].Upper);
            }
            foreach (var key in keys)
            {
                bool inRange = (start == null || key.AsSpan().SequenceCompareTo(start) >= 0) && (end == null || key.AsSpan().SequenceCompareTo(end) < 0);
                int hits = tiles.Count(tl => (tl.Lower == null || key.AsSpan().SequenceCompareTo(tl.Lower) >= 0)
                                              && (tl.Upper == null || key.AsSpan().SequenceCompareTo(tl.Upper) < 0));
                Assert.Equal(inRange ? 1 : 0, hits);
            }
        }
    }

    // ---- cancellation, progress, thread cap -----------------------------------------------------

    private static WordlistDatabase Seeded(TempDb t, ScanLimiter limiter)
    {
        var db = t.Open(limiter);
        ImportInChunks(db, SkewedWords(), 3);
        return db;
    }

    [Fact]
    public void Cancel_MidScan_JoinsWorkers_ReleasesPermits_AndDbStaysUsable()
    {
        using var t = new TempDb();
        var limiter = new ScanLimiter(4);
        using var db = Seeded(t, limiter);
        using var cts = new CancellationTokenSource();
        var threads = new System.Collections.Concurrent.ConcurrentBag<Thread>();
        var entered = new CountdownEvent(1);
        db.TileHook = _ =>
        {
            threads.Add(Thread.CurrentThread);
            entered.Signal();
            Assert.True(cts.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(30)));   // hold the worker until the test cancels
        };
        var scan = Task.Factory.StartNew(() => db.LengthDistributionCore(4, 0, 1, cts.Token, null).ToArray(),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(30)));
        cts.Cancel();
        var ex = Assert.Throws<AggregateException>(() => scan.Wait(TimeSpan.FromSeconds(60)));
        Assert.IsAssignableFrom<OperationCanceledException>(ex.InnerException);

        Assert.NotEmpty(threads);
        Assert.All(threads, th => Assert.False(th.IsAlive));       // joined before the exception surfaced
        Assert.Equal(4, limiter.Free);                              // every permit given back
        db.TileHook = null;
        Assert.Equal(db.LengthDistributionReference().ToArray(), db.LengthDistributionCore(4, 0, null, default, null).ToArray());
    }

    [Fact]
    public void WorkerFault_CancelsTheRest_AndRethrowsAfterJoin()
    {
        using var t = new TempDb();
        var limiter = new ScanLimiter(3);
        using var db = Seeded(t, limiter);
        var threads = new System.Collections.Concurrent.ConcurrentBag<Thread>();
        int calls = 0;
        db.TileHook = _ =>
        {
            threads.Add(Thread.CurrentThread);
            if (Interlocked.Increment(ref calls) == 2) throw new InvalidOperationException("boom");
        };
        Assert.Throws<InvalidOperationException>(() => db.LengthDistributionCore(3, 0, 1, default, null).ToArray());
        Assert.All(threads, th => Assert.False(th.IsAlive));
        Assert.Equal(3, limiter.Free);
    }

    [Fact]
    public void PreCancelledToken_Throws_BeforeAnyWorker()
    {
        using var t = new TempDb();
        var limiter = new ScanLimiter(3);
        using var db = Seeded(t, limiter);
        using var cts = new CancellationTokenSource(); cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => db.LengthDistributionCore(3, 0, null, cts.Token, null).ToArray());
        Assert.Throws<OperationCanceledException>(() => db.CountRangeCore(null, null, 3, 0, null, cts.Token));
        Assert.Equal(3, limiter.Free);
    }

    [Fact]
    public void Progress_IsMonotonic_OnCallingThread_AndEndsAtTotal()
    {
        using var t = new TempDb();
        using var db = Seeded(t, new ScanLimiter(3));
        WordlistDatabase.ProgressPollMs = 5;
        db.TileHook = _ => Thread.Sleep(40);            // keeps the scan alive across several polls
        var caller = Environment.CurrentManagedThreadId;
        var seen = new List<long>();
        var dist = db.LengthDistributionCore(3, 0, 100_000, default, n =>
        {
            Assert.Equal(caller, Environment.CurrentManagedThreadId);
            seen.Add(n);
        }).ToArray();
        long total = dist.Sum(d => d.count);
        Assert.True(seen.Count >= 2, "expected at least one mid-scan tick plus the final one");
        for (int i = 1; i < seen.Count; i++) Assert.True(seen[i] >= seen[i - 1]);
        Assert.Equal(total, seen[^1]);
    }

    // ---- planning from file metadata (no key walk) ------------------------------------------------

    [Fact]
    public void Planning_Reports_BeforeFirstTile_AndCancelsDuringPlanning()
    {
        using var t = new TempDb();
        var limiter = new ScanLimiter(3);
        using var db = Seeded(t, limiter);
        var order = new List<string>();
        db.TileHook = _ => { lock (order) order.Add("tile"); };
        var dist = db.LengthDistributionCore(3, 0, null, default, n => { lock (order) order.Add(order.Count == 0 ? "p" + n : "p"); }).ToArray();
        Assert.Equal("p0", order[0]);                       // the 0-key report precedes every tile
        Assert.Contains("tile", order);
        Assert.Equal(db.LengthDistributionReference().ToArray(), dist);

        // Cancelling from that first report (i.e. before/while planning) stops the scan before any tile runs.
        using var cts = new CancellationTokenSource();
        int tiles = 0;
        db.TileHook = _ => Interlocked.Increment(ref tiles);
        Assert.Throws<OperationCanceledException>(() =>
            db.LengthDistributionCore(3, 0, null, cts.Token, n => { if (n == 0) cts.Cancel(); }).ToArray());
        Assert.Equal(0, tiles);
        Assert.Equal(3, limiter.Free);
        Assert.Throws<OperationCanceledException>(() => db.PlanCuts(null, null, 3, null, cts.Token));
    }

    [Fact]
    public void MetadataCuts_MultiFile_AndSingleHugeFile_EqualReference()
    {
        foreach (bool compacted in new[] { false, true })
        {
            using var t = new TempDb();
            using var db = t.Open(new ScanLimiter(64));
            ImportInChunks(db, SkewedWords(), 6);
            if (compacted) db.Compact();
            var expected = db.LengthDistributionReference().ToArray();

            var cuts = db.PlanCuts(null, null, 3, 1, default);
            Assert.NotEmpty(cuts);                                        // the 1-byte target cuts at file / length boundaries
            if (compacted)
            {
                // One big file: only the probes can split it, and only within the budget.
                Assert.InRange(db.LastPlanProbeRanges, 1, WordlistDatabase.MaxPlanProbeRanges);
                var probed = db.PlanCuts(null, null, 3, 20_000, default);
                Assert.True(probed.Count > 3, $"expected the probes to split the single file, got {probed.Count} cuts");
            }
            int tiles = 0;
            db.TileHook = _ => Interlocked.Increment(ref tiles);
            foreach (long? target in new long?[] { null, 1, 20_000 })
                Assert.Equal(expected, db.LengthDistributionCore(3, 0, target, default, null).ToArray());
            Assert.True(tiles > 0);
        }
    }

    [Fact]
    public void Planning_RespectsTileCap_AndProbeBudget()
    {
        int savedTiles = WordlistDatabase.MaxPlanTiles, savedProbe = WordlistDatabase.MaxPlanProbeRanges;
        try
        {
            using var t = new TempDb();
            using var db = t.Open(new ScanLimiter(8));
            ImportInChunks(db, SkewedWords(), 6);
            db.Compact();
            var expected = db.LengthDistributionReference().ToArray();

            WordlistDatabase.MaxPlanTiles = 4;
            Assert.True(db.PlanCuts(null, null, 8, 1, default).Count <= 3);
            int tiles = 0;
            db.TileHook = _ => Interlocked.Increment(ref tiles);
            Assert.Equal(expected, db.LengthDistributionCore(8, 0, 1, default, null).ToArray());
            Assert.True(tiles <= 4, $"{tiles} tiles with a cap of 4");

            WordlistDatabase.MaxPlanTiles = 512;
            WordlistDatabase.MaxPlanProbeRanges = 20;
            db.PlanCuts(null, null, 8, 1, default);
            Assert.True(db.LastPlanProbeRanges <= 20, $"{db.LastPlanProbeRanges} probe ranges with a budget of 20");
            Assert.Equal(expected, db.LengthDistributionCore(8, 0, 1, default, null).ToArray());
        }
        finally { WordlistDatabase.MaxPlanTiles = savedTiles; WordlistDatabase.MaxPlanProbeRanges = savedProbe; }
    }

    [Fact]
    public void ThreadCap_HoldsAcrossConcurrentScans()
    {
        using var t = new TempDb();
        var limiter = new ScanLimiter(2);
        using var db = Seeded(t, limiter);
        int running = 0, max = 0;
        db.TileHook = _ =>
        {
            int now = Interlocked.Increment(ref running);
            int seenMax;
            while ((seenMax = Volatile.Read(ref max)) < now && Interlocked.CompareExchange(ref max, now, seenMax) != seenMax) { }
            Thread.Sleep(15);
            Interlocked.Decrement(ref running);
        };
        var expected = db.LengthDistributionReference().ToArray();
        var tasks = Enumerable.Range(0, 3).Select(_ => Task.Factory.StartNew(
            () => db.LengthDistributionCore(8, 0, 100_000, default, null).ToArray(),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
        Task.WaitAll(tasks);
        Assert.All(tasks, x => Assert.Equal(expected, x.Result));
        Assert.True(max <= 2, $"saw {max} concurrent workers with a cap of 2");
        Assert.Equal(2, limiter.Free);
    }

    [Fact]
    public void CapOne_And_SmallDb_TakeTheSinglePath()
    {
        using var t = new TempDb();
        var one = new ScanLimiter(1);
        using var db = Seeded(t, one);
        bool hookRan = false;
        db.TileHook = _ => hookRan = true;
        var expected = db.LengthDistributionReference().ToArray();
        Assert.Equal(expected, db.LengthDistributionCore(64, 0, null, default, null).ToArray());   // asking for 64 is clamped to the cap of 1
        Assert.Equal(expected, db.LengthDistribution().ToArray());                                   // default threshold: 200k keys < 1M
        Assert.Equal(db.CountRangeReference(null, null), db.CountRange(null, null));
        Assert.False(hookRan);
    }

    [Fact]
    public void ScanLimiter_Validation_AndAcquire()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ScanLimiter(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ScanLimiter(ScanLimiter.MaxCap + 1));
        Assert.Equal(1, ScanLimiter.AutoThreads(1));
        Assert.Equal(3, ScanLimiter.AutoThreads(4));
        var l = new ScanLimiter(3);
        Assert.Equal(3, l.AcquireUpTo(10, default));
        using var cts = new CancellationTokenSource(50);
        Assert.Throws<OperationCanceledException>(() => l.AcquireUpTo(1, cts.Token));   // none free: waits, cancellable
        l.Release(3);
        Assert.Equal(3, l.Free);
    }

    // ---- scan efficiency (PR A): outputs unchanged -----------------------------------------------

    [Fact]
    public void Range_And_Scan_MatchExpectedOrder_WithBounds()
    {
        using var t = new TempDb();
        using var db = Seeded(t, new ScanLimiter(2));
        var all = SkewedWords().Distinct().ToList();
        var cmp = Comparer<string>.Create((a, b) =>
        {
            int c = a.Length.CompareTo(b.Length);
            return c != 0 ? c : Encoding.UTF8.GetBytes(a).AsSpan().SequenceCompareTo(Encoding.UTF8.GetBytes(b));
        });
        all.Sort(cmp);
        foreach (var (min, max) in new (int?, int?)[] { (null, null), (8, 10), (35, 35), (31, 34), (null, 3), (30, null), (int.MaxValue, null), (null, int.MaxValue) })
        {
            var expected = all.Where(w => w.Length >= (min ?? 0) && w.Length <= (max ?? int.MaxValue)).ToList();
            Assert.Equal(expected, db.Range(min, max, null).ToList());
            Assert.Equal(expected.Take(7).ToList(), db.Range(min, max, 7).ToList());

            var chunked = new List<string>();
            string? last = null;
            while (true)
            {
                var chunk = db.Scan(min, max, last).Take(5000).ToList();
                if (chunk.Count == 0) break;
                chunked.AddRange(chunk);
                last = chunk[^1];
            }
            Assert.Equal(expected, chunked);
        }
    }
}

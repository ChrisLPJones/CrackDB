using System.Diagnostics;
using System.Text;
using HoarderDB.Web.Jobs;
using HoarderDB.Web.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

// These tests join worker threads on purpose.
#pragma warning disable xUnit1031

namespace HoarderDB.Web.Tests;

/// <summary>
/// Lease-segment behaviour of <see cref="SearchRunner"/>: one iterator per segment, yield on demand (writer waiting,
/// MaxLeaseAge, shutdown, cancel), exact resume. Drives the runner directly against a real <see cref="DbManager"/>
/// over a throwaway Test_&lt;guid&gt; data root. Every static knob changed here is restored in Dispose.
/// </summary>
public sealed class SearchSegmentTests : IDisposable
{
    private readonly TimeSpan _age = SearchRunner.MaxLeaseAge;
    private readonly TimeSpan _retry = SearchRunner.BusyRetry;
    private readonly Action<long>? _stride = SearchRunner.StrideHook;
    private readonly int _bufM = SearchRunner.BufferMatches;
    private readonly int _cap = SearchRunner.FlushBatchCap;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Test_" + Guid.NewGuid());

    public SearchSegmentTests()
    {
        HoarderDbWebFactory.EnsureThrowaway(_root);
        Directory.CreateDirectory(_root);
        SearchRunner.BusyRetry = TimeSpan.FromMilliseconds(20);
    }

    public void Dispose()
    {
        SearchRunner.MaxLeaseAge = _age;
        SearchRunner.BusyRetry = _retry;
        SearchRunner.StrideHook = _stride;
        SearchRunner.BufferMatches = _bufM;
        SearchRunner.FlushBatchCap = _cap;
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private sealed class CountingHook : ISearchProgressHook
    {
        public readonly List<long> Scanned = new();
        public void OnChunk(string dbName, long scannedCount) { lock (Scanned) Scanned.Add(scannedCount); }
    }

    private sealed class CaptureLog : ILogger
    {
        public readonly List<string> Lines = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        { lock (Lines) Lines.Add(formatter(state, exception)); }
    }

    private sealed class Run(Job job, Thread thread, CountingHook hook, CaptureLog log)
    {
        public Job Job { get; } = job;
        public CountingHook Hook { get; } = hook;
        public CaptureLog Log { get; } = log;
        public void Join() => Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "search thread did not finish");
        public string[] Matches()
        {
            var all = new List<string>();
            while (true)
            {
                var (_, items) = Job.Spool!.ReadPage(all.Count, 5000, 4 << 20);
                if (items.Count == 0) return all.ToArray();
                all.AddRange(items);
            }
        }
    }

    private static string[] Fixture()
    {
        var rnd = new Random(42);
        const string alpha = "abcdefghijklmnopqrstuvwxyz";
        string R(int n) => string.Create(n, rnd, (s, r) => { for (int i = 0; i < s.Length; i++) s[i] = alpha[r.Next(alpha.Length)]; });
        var w = new List<string>();
        for (int i = 0; i < 30_000; i++) w.Add(R(9));                 // one huge length bucket
        for (int i = 0; i < 4_000; i++) w.Add(R(4 + rnd.Next(14)));   // many lengths
        for (int i = 0; i < 300; i++)
        {
            w.Add("needle" + R(3)); w.Add("NEEDLE" + R(3)); w.Add("Needle" + R(2));   // case variants, length 9 / 8
            w.Add(R(2) + "needle" + R(rnd.Next(4)));
            w.Add("pässwörd" + i); w.Add("日本needle" + i);                            // non-ASCII
            w.Add("nee\0dle" + i);                                                      // NUL inside
        }
        w.AddRange(new[] { "needleabc", "NeedleAbc", "NEEDLEABC" });
        return w.Distinct().ToArray();
    }

    private Run Start(DbManager mgr, string db, SearchSpec spec, SearchSpool spool, JobRegistry reg, CountingHook? hook = null, FakeDisk? disk = null, bool slow = false)
    {
        Assert.True(SearchRunner.TryEnter(), "single search slot must be free");
        var (min, max) = SearchMatcher.EffectiveRange(spec);
        var job = new Job(JobKind.Search, db, spec.Query) { Min = min, Max = max };
        job.Spool = spool.Create(job.Id);
        Assert.True(job.TryStart());
        reg.Add(job);
        hook ??= new CountingHook();
        var log = new CaptureLog();
        IDiskSpace d = slow ? new SlowDisk() : disk ?? new FakeDisk { Free = long.MaxValue };
        var t = new Thread(() => SearchRunner.Execute(job, spec, mgr, d, spool.Dir, reg, log, job.Cts.Token, hook)) { IsBackground = true };
        t.Start();
        return new Run(job, t, hook, log);
    }

    private (DbManager mgr, SearchSpool spool, JobRegistry reg) Host()
    {
        var resolver = new DataRootResolver(_root);
        var spool = new SearchSpool(resolver, NullLogger<SearchSpool>.Instance);
        Assert.True(spool.TryPrepare());
        return (new DbManager(resolver), spool, new JobRegistry());
    }

    private string Seed(string name, IEnumerable<string> words)
    {
        var path = Path.Combine(_root, name);
        HoarderDbWebFactory.Seed(path, words);
        return path;
    }

    /// <summary>The old path: Scan(min, max, null) filtered by the matcher.</summary>
    private static (string[] Matches, long Keys) Oracle(string dbPath, SearchSpec spec)
    {
        var (min, max) = SearchMatcher.EffectiveRange(spec);
        var isMatch = SearchMatcher.Compile(spec);
        using var db = new WordlistDatabase(dbPath, new UTF8Encoding(false));
        long keys = 0;
        var m = new List<string>();
        foreach (var w in db.Scan(min, max, null)) { keys++; if (isMatch(w)) m.Add(w); }
        return (m.ToArray(), keys);
    }

    public static IEnumerable<object[]> Specs()
    {
        yield return new object[] { "needleabc", true, false };      // ignore-case exact
        yield return new object[] { "needle", false, true };      // case-sensitive contains
        yield return new object[] { "needle", true, true };       // ignore-case contains
        yield return new object[] { "wörd1", false, true };       // non-ASCII contains
    }

    [Theory, MemberData(nameof(Specs))]
    public void Spool_EqualsOldScanPath_OnSingleSegmentAndManyForcedYields(string q, bool ic, bool contains)
    {
        var path = Seed("Test_eq", Fixture());
        var spec = new SearchSpec(q, ic, contains);
        var (expected, keys) = Oracle(path, spec);
        Assert.NotEmpty(expected);

        var (mgr, spool, reg) = Host();
        try
        {
            foreach (var age in new[] { TimeSpan.FromMinutes(2), TimeSpan.Zero })
            {
                SearchRunner.MaxLeaseAge = age;
                long before = WordlistDatabase.CursorsOpened;
                var run = Start(mgr, "Test_eq", spec, spool, reg);
                run.Join();
                Assert.Equal(JobStatus.Completed, run.Job.Status);
                Assert.Equal(expected, run.Matches());
                var snap = run.Job.Snapshot();
                Assert.Equal(keys, snap.ScannedCount);
                Assert.Equal(expected.Length, snap.MatchedCount);
                // scanned stays monotonic and exact across the hook calls; one cursor per segment
                var s = run.Hook.Scanned;
                Assert.Equal(s.OrderBy(x => x), s);
                Assert.Equal(keys, s[^1]);
                Assert.True(WordlistDatabase.CursorsOpened - before >= s.Count, "at least one iterator per segment");   // one per worker per segment: see ParallelSearchTests
                if (age == TimeSpan.Zero) Assert.True(s.Count >= 3, "expected >= 3 forced yields, got " + s.Count);
                else Assert.Single(s);
                spool.DisposeAll();
            }
        }
        finally { mgr.DisposeAsync().AsTask().Wait(); }
    }

    [Fact]
    public void FlushBatchCap_ZeroOrNegative_IsClampedToOne()
    {
        SearchRunner.FlushBatchCap = 0;
        Assert.Equal(1, SearchRunner.EffectiveFlushBatchCap);
        SearchRunner.FlushBatchCap = -5;
        Assert.Equal(1, SearchRunner.EffectiveFlushBatchCap);
        SearchRunner.FlushBatchCap = 7;
        Assert.Equal(7, SearchRunner.EffectiveFlushBatchCap);
    }

    // Raw keys that are not valid UTF-8 (the engine stores any bytes): the byte matcher (case-sensitive contains) and
    // the string matcher (decoded lossily) must give the same spool, end to end through the runner.
    [Theory]
    [InlineData(false, true)]    // case-sensitive contains: byte path
    [InlineData(true, true)]     // ignore-case contains: string path
    public void InvalidUtf8Keys_SearchEqualsStringOracle(bool ignoreCase, bool contains)
    {
        var path = Seed("Test_rawkeys", new[] { "needle1", "xxneedlexx", "unrelated" });
        var raw = new[]
        {
            new byte[] { (byte)'n', (byte)'e', (byte)'e', (byte)'d', (byte)'l', (byte)'e', 0xFF },          // needle\xFF
            new byte[] { 0xFF, (byte)'n', (byte)'e', (byte)'e', (byte)'d', (byte)'l', (byte)'e' },          // \xFFneedle
            new byte[] { (byte)'n', (byte)'e', 0xFF, (byte)'e', (byte)'d', (byte)'l', (byte)'e' },          // ne\xFFedle: no match
            new byte[] { 0xC3, (byte)'n', (byte)'e', (byte)'e', (byte)'d', (byte)'l', (byte)'e', 0xE6, 0x97 },   // truncated multi-byte around it
        };
        using (var rdb = RocksDbSharp.RocksDb.Open(new RocksDbSharp.DbOptions(), path))
            foreach (var k in raw)
            {
                var key = new byte[4 + k.Length];
                System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(key, k.Length);
                k.CopyTo(key, 4);
                rdb.Put(key, new byte[] { 0 });
            }

        var spec = new SearchSpec("needle", ignoreCase, contains);
        var (expected, _) = Oracle(path, spec);
        Assert.Contains(expected, m => m.Contains('\uFFFD'));   // the invalid keys really are hits

        var (mgr, spool, reg) = Host();
        try
        {
            var run = Start(mgr, "Test_rawkeys", spec, spool, reg);
            run.Join();
            Assert.Equal(JobStatus.Completed, run.Job.Status);
            Assert.Equal(expected.OrderBy(x => x, StringComparer.Ordinal), run.Matches().OrderBy(x => x, StringComparer.Ordinal));
            spool.DisposeAll();
        }
        finally { mgr.DisposeAsync().AsTask().Wait(); }
    }

    [Fact]
    public void SegmentLog_IsNumbersOnly()
    {
        Seed("Test_log", Fixture());
        var (mgr, spool, reg) = Host();
        try
        {
            SearchRunner.MaxLeaseAge = TimeSpan.Zero;
            var run = Start(mgr, "Test_log", new SearchSpec("needle", true, true), spool, reg);
            run.Join();
            lock (run.Log.Lines)
            {
                Assert.True(run.Log.Lines.Count >= 3);
                Assert.All(run.Log.Lines, l =>
                {
                    Assert.Matches(@"^Search segment: \d+ workers, \d+ of \d+ tiles done, \d+ keys, scan \d+ ms, \d+ matches, (end|yield)$", l);
                    Assert.DoesNotContain("needle", l, StringComparison.OrdinalIgnoreCase);
                });
            }
        }
        finally { mgr.DisposeAsync().AsTask().Wait(); }
    }

    // Slows the scan: 5 ms per 256 keys, signals the first stride.
    private static ManualResetEventSlim SlowScan()
    {
        var reached = new ManualResetEventSlim(false);
        SearchRunner.StrideHook = _ => { reached.Set(); Thread.Sleep(5); };
        return reached;
    }

    private static IEnumerable<string> Big() => Enumerable.Range(0, 150_000).Select(i => "needle" + i.ToString("D6"));

    [Fact]
    public async Task QueuedWriteWindow_StartsWithinShortBound_WhileSearchMidScan_AndSearchResumesExactly()
    {
        var path = Seed("Test_yield", Big());
        var spec = new SearchSpec("needle", false, true);
        var (expected, keys) = Oracle(path, spec);
        var (mgr, spool, reg) = Host();
        try
        {
            var reached = SlowScan();   // ~3 s scan with the lease held; MaxLeaseAge stays 2 min, so only WriterWaiting can end the segment
            var run = Start(mgr, "Test_yield", spec, spool, reg);
            Assert.True(reached.Wait(TimeSpan.FromSeconds(30)));
            var started = new TaskCompletionSource<TimeSpan>(TaskCreationOptions.RunContinuationsAsynchronously);
            var sw = Stopwatch.StartNew();
            var write = mgr.WithWriteAsync("Test_yield", DbOpenMode.Read, (db, _) =>
            {
                started.TrySetResult(sw.Elapsed);
                db.Compact();
                return Task.FromResult(true);
            }, CancellationToken.None);
            var waited = await started.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.True(waited < TimeSpan.FromSeconds(1.2), $"write window waited {waited.TotalMilliseconds:F0} ms");
            Assert.False(run.Job.IsTerminal, "the search must still be mid-scan when the write window starts");
            await write;

            run.Join();
            Assert.Equal(JobStatus.Completed, run.Job.Status);
            Assert.Equal(expected, run.Matches());      // none lost, none repeated across the yield
            Assert.Equal(keys, run.Job.Snapshot().ScannedCount);
            Assert.True(run.Hook.Scanned.Count >= 2);
        }
        finally { await mgr.DisposeAsync(); }
    }

    // Slow spool volume: every free-space probe (one per drained batch) takes 40 ms.
    private sealed class SlowDisk : IDiskSpace
    {
        public long RecheckIntervalBytes => 32L * 1024 * 1024;
        public long? FreeBytes(string dir) { Thread.Sleep(40); return long.MaxValue; }
    }

    [Fact]
    public async Task DenseQuery_SlowDisk_FlushCapped_WriterGetsInWhileBacklogPending_AndSpoolEqualsOracle()
    {
        var path = Seed("Test_flush", Big().Take(30_000));
        var spec = new SearchSpec("needle", false, true);
        var (expected, keys) = Oracle(path, spec);
        var (mgr, spool, reg) = Host();
        try
        {
            SearchRunner.BufferMatches = 8;      // tiny batches: every stride of dense hits needs many batches
            SearchRunner.FlushBatchCap = 2;
            var run = Start(mgr, "Test_flush", spec, spool, reg, disk: null, hook: null, slow: true);
            while (run.Job.Snapshot().ScannedCount == 0 && !run.Job.IsTerminal) await Task.Delay(5);
            var started = new TaskCompletionSource<TimeSpan>(TaskCreationOptions.RunContinuationsAsynchronously);
            var sw = Stopwatch.StartNew();
            var write = mgr.WithWriteAsync("Test_flush", DbOpenMode.Read, (db, _) =>
            {
                started.TrySetResult(sw.Elapsed);
                return Task.FromResult(true);
            }, CancellationToken.None);
            var waited = await started.Task.WaitAsync(TimeSpan.FromSeconds(30));
            // Uncapped, the drain loop never returned while the backlog was non-empty; capped it polls every ~2 batches (~80 ms).
            Assert.True(waited < TimeSpan.FromSeconds(1.5), $"write window waited {waited.TotalMilliseconds:F0} ms");
            Assert.False(run.Job.IsTerminal, "the drain backlog must still be pending when the write window starts");
            await write;

            run.Join();
            Assert.Equal(JobStatus.Completed, run.Job.Status);
            Assert.Equal(expected, run.Matches());
            Assert.Equal(keys, run.Job.Snapshot().ScannedCount);
            Assert.True(run.Hook.Scanned.Count >= 2, "the search must have yielded and resumed");
        }
        finally { await mgr.DisposeAsync(); }
    }

    [Fact]
    public void MaxLeaseAge_EndsSegments_EvenWithNoWriterWaiting()
    {
        var path = Seed("Test_age", Big().Take(40_000));
        var spec = new SearchSpec("needle", false, true);
        var (expected, keys) = Oracle(path, spec);
        var (mgr, spool, reg) = Host();
        try
        {
            SearchRunner.MaxLeaseAge = TimeSpan.FromMilliseconds(60);
            SlowScan();   // ~160 strides x 5 ms = ~0.8 s => several 60 ms segments
            var run = Start(mgr, "Test_age", spec, spool, reg);
            run.Join();
            Assert.Equal(JobStatus.Completed, run.Job.Status);
            Assert.True(run.Hook.Scanned.Count >= 3, "segments: " + run.Hook.Scanned.Count);
            Assert.Equal(expected, run.Matches());
            Assert.Equal(keys, run.Job.Snapshot().ScannedCount);
        }
        finally { mgr.DisposeAsync().AsTask().Wait(); }
    }

    [Fact]
    public async Task Cancel_MidScan_JoinsDisposesCursorAndLease_DbStaysClosable()
    {
        Seed("Test_cancel", Big());
        var (mgr, spool, reg) = Host();
        try
        {
            var reached = SlowScan();
            var run = Start(mgr, "Test_cancel", new SearchSpec("needle", false, true), spool, reg);
            Assert.True(reached.Wait(TimeSpan.FromSeconds(30)));
            run.Job.Cts.Cancel();
            run.Join();
            Assert.Equal(JobStatus.Cancelled, run.Job.Status);
            Assert.True(run.Job.Spool!.IsDisposed);
            SearchRunner.StrideHook = null;
            // The lease is gone: a write window gets in at once.
            await mgr.WithWriteAsync("Test_cancel", DbOpenMode.Read, (db, _) => Task.FromResult(db.Contains("needle000001")), CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { await mgr.DisposeAsync(); }
    }

    [Fact]
    public void Shutdown_EndsTheSegment_WellInsideTheCloseAllWait()
    {
        Seed("Test_shut", Big());
        var (mgr, spool, reg) = Host();
        var reached = SlowScan();
        var run = Start(mgr, "Test_shut", new SearchSpec("needle", false, true), spool, reg);
        Assert.True(reached.Wait(TimeSpan.FromSeconds(30)));
        var sw = Stopwatch.StartNew();
        mgr.StopAsync(CancellationToken.None).Wait();   // CloseAll: waits up to 10 s for readers
        var closed = sw.Elapsed;
        run.Join();
        Assert.True(closed < TimeSpan.FromSeconds(3), $"CloseAll took {closed.TotalSeconds:F1} s");
        Assert.True(run.Job.IsTerminal);
    }
}

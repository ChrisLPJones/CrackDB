using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using HoarderDB.Web.Jobs;
using HoarderDB.Web.Options;
using HoarderDB.Web.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

#pragma warning disable xUnit1031

namespace HoarderDB.Web.Tests;

/// <summary>
/// Parallel tiles with ordered output: the spool must equal the old serial <c>Scan</c> path byte for byte at every thread
/// cap and layout, and the lease/permit/cancel rules must hold. Throwaway Test_&lt;guid&gt; data roots only; every static
/// knob changed here is restored in Dispose.
/// </summary>
public sealed class ParallelSearchTests : IDisposable
{
    private readonly TimeSpan _age = SearchRunner.MaxLeaseAge;
    private readonly TimeSpan _retry = SearchRunner.BusyRetry;
    private readonly Action<long>? _stride = SearchRunner.StrideHook;
    private readonly int _bufM = SearchRunner.BufferMatches;
    private readonly long _bufC = SearchRunner.BufferChars;
    private readonly long? _target = SearchRunner.TileTargetBytes;
    private readonly TimeSpan _grace = SearchRunner.WaiterGrace;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Test_" + Guid.NewGuid());
    private readonly List<DbManager> _mgrs = new();

    private readonly Xunit.Abstractions.ITestOutputHelper output;

    public ParallelSearchTests(Xunit.Abstractions.ITestOutputHelper output)
    {
        this.output = output;
        HoarderDbWebFactory.EnsureThrowaway(_root);
        Directory.CreateDirectory(_root);
        SearchRunner.BusyRetry = TimeSpan.FromMilliseconds(20);
        SearchRunner.TileTargetBytes = 1;   // many tiles even on a tiny db
    }

    public void Dispose()
    {
        foreach (var m in _mgrs) m.DisposeAsync().AsTask().Wait();
        SearchRunner.MaxLeaseAge = _age;
        SearchRunner.BusyRetry = _retry;
        SearchRunner.StrideHook = _stride;
        SearchRunner.BufferMatches = _bufM;
        SearchRunner.BufferChars = _bufC;
        SearchRunner.TileTargetBytes = _target;
        SearchRunner.WaiterGrace = _grace;
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
        // (workers, tiles) of every segment line
        public List<(int Workers, int Tiles)> Segments()
        {
            lock (Lines)
                return Lines.Select(l => Regex.Match(l, @"^Search segment: (\d+) workers, \d+ of (\d+) tiles done"))
                            .Where(m => m.Success).Select(m => (int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value))).ToList();
        }
    }

    private sealed class Run(Job job, Thread thread, CountingHook hook, CaptureLog log)
    {
        public Job Job { get; } = job;
        public CountingHook Hook { get; } = hook;
        public CaptureLog Log { get; } = log;
        public void Join(int seconds = 60) => Assert.True(thread.Join(TimeSpan.FromSeconds(seconds)), "search thread did not finish");
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

    private DbManager Host(int cap, out SearchSpool spool, out JobRegistry reg)
    {
        var resolver = new DataRootResolver(_root);
        spool = new SearchSpool(resolver, NullLogger<SearchSpool>.Instance);
        Assert.True(spool.TryPrepare());
        reg = new JobRegistry();
        var m = new DbManager(resolver, storage: Microsoft.Extensions.Options.Options.Create(new StorageOptions { ScanThreads = cap }));
        _mgrs.Add(m);
        return m;
    }

    private Run Start(DbManager mgr, string db, SearchSpec spec, SearchSpool spool, JobRegistry reg, FakeDisk? disk = null)
    {
        Assert.True(SearchRunner.TryEnter(), "single search slot must be free");
        var (min, max) = SearchMatcher.EffectiveRange(spec);
        var job = new Job(JobKind.Search, db, spec.Query) { Min = min, Max = max };
        job.Spool = spool.Create(job.Id);
        Assert.True(job.TryStart());
        reg.Add(job);
        var hook = new CountingHook();
        var log = new CaptureLog();
        var d = disk ?? new FakeDisk { Free = long.MaxValue };
        var t = new Thread(() => SearchRunner.Execute(job, spec, mgr, d, spool.Dir, reg, log, job.Cts.Token, hook)) { IsBackground = true };
        t.Start();
        return new Run(job, t, hook, log);
    }

    private static (string[] Matches, long Keys) Oracle(string dbPath, SearchSpec spec)
    {
        var (min, max) = SearchMatcher.EffectiveRange(spec);
        var isMatch = SearchMatcher.Compile(spec);
        using var db = new WordlistDatabase(dbPath, new UTF8Encoding(false), enableAutoCompaction: false);   // must not compact the layout under test
        long keys = 0;
        var m = new List<string>();
        foreach (var w in db.Scan(min, max, null)) { keys++; if (isMatch(w)) m.Add(w); }
        return (m.ToArray(), keys);
    }

    // Skewed: many lengths, one huge length (9), non-ASCII, NUL and U+FFFF edge characters, case variants.
    private static List<string> Skewed()
    {
        var rnd = new Random(42);
        const string alpha = "abcdefghijklmnopqrstuvwxyz";
        string R(int n) => string.Create(n, rnd, (s, r) => { for (int i = 0; i < s.Length; i++) s[i] = alpha[r.Next(alpha.Length)]; });
        var w = new List<string>();
        for (int i = 0; i < 30_000; i++) w.Add(R(9));
        for (int i = 0; i < 4_000; i++) w.Add(R(4 + rnd.Next(30)));
        for (int i = 0; i < 300; i++)
        {
            w.Add("needle" + R(3)); w.Add("NEEDLE" + R(3)); w.Add("Needle" + R(2));
            w.Add(R(2) + "needle" + R(rnd.Next(12)));
            w.Add("pässwörd" + i); w.Add("日本needle" + i); w.Add("😀needle" + i);
            w.Add("nee\0dle" + i); w.Add("needle\0" + i); w.Add("needle￿" + i); w.Add("￿needle" + i); w.Add("needle�\0" + i);
        }
        w.AddRange(new[] { "needleabc", "NeedleAbc", "NEEDLEABC", "\0\0\0\0needle", new string('z', 50) + "needle" });
        return w.Distinct().ToList();
    }

    private string MakeDb(string name, string layout)
    {
        var path = Path.Combine(_root, name);
        var words = Skewed();
        using (var db = new WordlistDatabase(path, new UTF8Encoding(false), enableAutoCompaction: false))
        {
            switch (layout)
            {
                case "compacted": db.Import(words, null, null, threads: 2); db.Compact(); break;
                case "manyL0": Chunks(db, words, 10); break;
                case "tooManyL0": Chunks(db, words, 20); Assert.True(db.NumFilesAtLevel0() > 16, "fixture must exceed the parallel L0 guard"); break;
                case "bigPlusSmall":
                    db.Import(words.Take(words.Count - 400), null, null, threads: 2);
                    db.Compact();
                    Chunks(db, words.Skip(words.Count - 400).ToList(), 8);
                    break;
            }
        }
        return path;
    }

    private static void Chunks(WordlistDatabase db, List<string> words, int parts)
    {
        int n = words.Count / parts + 1;
        for (int i = 0; i < words.Count; i += n) db.Import(words.Skip(i).Take(n), null, null, threads: 1);
    }

    [Theory]
    [InlineData("compacted")]
    [InlineData("manyL0")]
    [InlineData("tooManyL0")]
    [InlineData("bigPlusSmall")]
    public void Spool_EqualsOldScanPath_AtEveryThreadCap_AndLayout(string layout)
    {
        var path = MakeDb("Test_eq_" + layout, layout);
        bool parallelOk = layout != "tooManyL0";
        foreach (var spec in new[] { new SearchSpec("needle", false, true), new SearchSpec("NEEDLE", true, true), new SearchSpec("needleabc", true, false), new SearchSpec("wörd1", false, true) })
        {
            var (expected, keys) = Oracle(path, spec);
            Assert.NotEmpty(expected);
            foreach (int cap in new[] { 1, 2, 3, 8 })
                foreach (var age in new[] { TimeSpan.FromMinutes(2), TimeSpan.Zero })
                {
                    if (age == TimeSpan.Zero && cap != 3 && spec.Query != "needle") continue;   // forced yields: enough variety, keep the run short
                    var mgr = Host(cap, out var spool, out var reg);
                    SearchRunner.MaxLeaseAge = age;
                    // forced yields: 2 ms per stride keeps the scan alive long enough for the search thread to yield it >= 3 times, even on a loaded box
                    SearchRunner.StrideHook = age == TimeSpan.Zero ? _ => Thread.Sleep(2) : null;
                    long before = WordlistDatabase.CursorsOpened;
                    var run = Start(mgr, Path.GetFileName(path), spec, spool, reg);
                    run.Join();
                    var ctx = $"{layout} cap {cap} age {age} {spec.Query}";
                    Assert.True(JobStatus.Completed == run.Job.Status, ctx);
                    Assert.True(expected.SequenceEqual(run.Matches()), "spool differs from the serial path: " + ctx);
                    var snap = run.Job.Snapshot();
                    Assert.True(keys == snap.ScannedCount, ctx + " scanned " + snap.ScannedCount + " vs " + keys);
                    Assert.Equal(expected.Length, snap.MatchedCount);
                    var s = run.Hook.Scanned;
                    Assert.Equal(s.OrderBy(x => x), s);             // monotonic
                    Assert.Equal(keys, s[^1]);
                    var segs = run.Log.Segments();
                    Assert.Equal(s.Count, segs.Count);
                    Assert.All(segs, g => Assert.True(g.Workers <= cap, ctx + ": workers beyond the cap"));
                    if (!parallelOk || cap == 1) Assert.All(segs, g => Assert.Equal(1, g.Workers));
                    else if (age != TimeSpan.Zero && spec.Contains) Assert.True(segs.Max(g => g.Workers) >= 2, ctx + ": expected a parallel segment");
                    if (parallelOk && cap > 1 && spec.Contains) Assert.True(segs[0].Tiles >= 3, ctx + ": expected several tiles");
                    // one iterator per worker per segment, never more
                    Assert.True(WordlistDatabase.CursorsOpened - before >= segs.Sum(g => g.Workers));
                    if (age == TimeSpan.Zero) Assert.True(s.Count >= 3, "expected >= 3 forced yields, got " + s.Count);
                    spool.DisposeAll();
                    mgr.DisposeAsync().AsTask().Wait();
                    _mgrs.Remove(mgr);
                }
        }
    }

    [Fact]
    public void MatchDense_TinyBuffers_FinishesInOrder_NoDeadlock()
    {
        var rnd = new Random(5);
        var words = new List<string>();
        for (int len = 7; len < 40; len++)
            for (int i = 0; i < 1500; i++) words.Add("needle" + string.Create(len - 6, rnd, (s, r) => { for (int k = 0; k < s.Length; k++) s[k] = (char)('a' + r.Next(26)); }));
        var path = Path.Combine(_root, "Test_dense");
        HoarderDbWebFactory.Seed(path, words);
        var spec = new SearchSpec("needle", false, true);
        var (expected, keys) = Oracle(path, spec);
        Assert.Equal(keys, expected.Length);   // every key matches

        SearchRunner.BufferMatches = 4;
        SearchRunner.BufferChars = 64 * 1024;
        var mgr = Host(3, out var spool, out var reg);
        var run = Start(mgr, "Test_dense", spec, spool, reg);
        run.Join(45);   // hard timeout: a deadlock fails here
        Assert.Equal(JobStatus.Completed, run.Job.Status);
        Assert.True(expected.SequenceEqual(run.Matches()));
        Assert.Equal(keys, run.Job.Snapshot().ScannedCount);
        Assert.True(run.Log.Segments().Max(g => g.Workers) >= 2);
    }

    private static string[] Big() => Enumerable.Range(0, 150_000).Select(i => "needle" + i.ToString("D6")).ToArray();

    private static ManualResetEventSlim SlowScan()
    {
        var reached = new ManualResetEventSlim(false);
        SearchRunner.StrideHook = _ => { reached.Set(); Thread.Sleep(5); };
        return reached;
    }

    [Fact]
    public async Task Cancel_MidScan_JoinsEveryWorker_ReturnsPermits_DbStaysClosable()
    {
        var path = MakeDb("Test_cancel", "manyL0");
        var mgr = Host(3, out var spool, out var reg);
        var reached = SlowScan();
        var run = Start(mgr, "Test_cancel", new SearchSpec("needle", false, true), spool, reg);
        Assert.True(reached.Wait(TimeSpan.FromSeconds(30)));
        Assert.True(mgr.ScanLimiter.Free < 3, "the running search holds scan permits");
        run.Job.Cts.Cancel();
        run.Join(20);
        Assert.Equal(JobStatus.Cancelled, run.Job.Status);
        Assert.True(run.Job.Spool!.IsDisposed);
        Assert.Equal(3, mgr.ScanLimiter.Free);
        SearchRunner.StrideHook = null;
        await mgr.WithWriteAsync("Test_cancel", DbOpenMode.Read, (db, _) => Task.FromResult(db.Contains("needleabc")), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(path, Path.Combine(_root, "Test_cancel"));
    }

    [Fact]
    public void LowDisk_StopsWithTheOrderedPrefix()
    {
        var path = MakeDb("Test_low", "manyL0");
        var spec = new SearchSpec("needle", false, true);
        var (expected, _) = Oracle(path, spec);
        var disk = new FakeDisk { Free = long.MaxValue };
        var mgr = Host(3, out var spool, out var reg);
        SearchRunner.StrideHook = s => { if (s > 4000) disk.Free = 0; };
        var run = Start(mgr, "Test_low", spec, spool, reg, disk);
        run.Join();
        var got = run.Matches();
        Assert.True(got.Length < expected.Length, "the scan must have stopped early");
        Assert.True(expected.Take(got.Length).SequenceEqual(got), "what is kept is an exact ordered prefix");
        Assert.StartsWith("Stopped: low disk space", run.Job.Note);
        Assert.Equal(3, mgr.ScanLimiter.Free);
    }

    [Fact]
    public async Task QueuedWriteWindow_StartsQuickly_ParallelSearchResumesExactly()
    {
        var path = Path.Combine(_root, "Test_yield");
        HoarderDbWebFactory.Seed(path, Big());
        var spec = new SearchSpec("needle", false, true);
        var (expected, keys) = Oracle(path, spec);
        SearchRunner.TileTargetBytes = null;
        var mgr = Host(3, out var spool, out var reg);
        var reached = SlowScan();
        var run = Start(mgr, "Test_yield", spec, spool, reg);
        Assert.True(reached.Wait(TimeSpan.FromSeconds(30)));
        var started = new TaskCompletionSource<TimeSpan>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sw = Stopwatch.StartNew();
        var write = mgr.WithWriteAsync("Test_yield", DbOpenMode.Read, (db, _) => { started.TrySetResult(sw.Elapsed); db.Compact(); return Task.FromResult(true); }, CancellationToken.None);
        var waited = await started.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(waited < TimeSpan.FromSeconds(1.2), $"write window waited {waited.TotalMilliseconds:F0} ms");
        Assert.False(run.Job.IsTerminal);
        await write;
        run.Join();
        Assert.Equal(JobStatus.Completed, run.Job.Status);
        Assert.True(expected.SequenceEqual(run.Matches()));
        Assert.Equal(keys, run.Job.Snapshot().ScannedCount);
        Assert.True(run.Hook.Scanned.Count >= 2);
    }

    [Fact]
    public void Shutdown_WithAParallelSearchRunning_CompletesWellInsideTenSeconds()
    {
        MakeDb("Test_shut", "manyL0");
        var mgr = Host(3, out var spool, out var reg);
        var reached = SlowScan();
        var run = Start(mgr, "Test_shut", new SearchSpec("needle", false, true), spool, reg);
        Assert.True(reached.Wait(TimeSpan.FromSeconds(30)));
        var sw = Stopwatch.StartNew();
        mgr.StopAsync(CancellationToken.None).Wait();
        var closed = sw.Elapsed;
        run.Join(20);
        Assert.True(closed < TimeSpan.FromSeconds(3), $"CloseAll took {closed.TotalSeconds:F1} s");
        Assert.True(run.Job.IsTerminal);
    }

    [Fact]
    public void ScanPermits_NeverExceedTheCap_WhenAnotherScanHoldsSome()
    {
        var path = MakeDb("Test_perm", "manyL0");
        var spec = new SearchSpec("needle", false, true);
        var (expected, _) = Oracle(path, spec);
        var mgr = Host(3, out var spool, out var reg);
        int other = mgr.ScanLimiter.AcquireUpTo(1, default);   // e.g. a breakdown worker
        var threadIds = new HashSet<int>();
        SearchRunner.StrideHook = _ => { lock (threadIds) threadIds.Add(Environment.CurrentManagedThreadId); };
        var run = Start(mgr, "Test_perm", spec, spool, reg);
        run.Join();
        Assert.True(expected.SequenceEqual(run.Matches()));
        Assert.True(run.Log.Segments().Max(g => g.Workers) <= 2, "the search may only use the permits that were free");
        lock (threadIds) Assert.True(threadIds.Count <= 2);
        Assert.Equal(2, mgr.ScanLimiter.Free);
        mgr.ScanLimiter.Release(other);
        Assert.Equal(3, mgr.ScanLimiter.Free);
    }

    [Fact]
    public void AllPermitsHeld_SearchWaitsWithANote_Cancellable_ThenRunsWhenFreed()
    {
        var path = MakeDb("Test_wait", "compacted");
        var spec = new SearchSpec("needle", false, true);
        var (expected, _) = Oracle(path, spec);
        var mgr = Host(3, out var spool, out var reg);
        int all = mgr.ScanLimiter.AcquireUpTo(3, default);
        Assert.Equal(3, all);
        var run = Start(mgr, "Test_wait", spec, spool, reg);
        var sw = Stopwatch.StartNew();
        while (run.Job.Note != SearchRunner.WaitingForThreadNote && sw.Elapsed < TimeSpan.FromSeconds(10)) Thread.Sleep(10);
        Assert.Equal("Waiting for a free scan thread.", run.Job.Note);
        Assert.Equal(0, mgr.ScanLimiter.Free);
        Assert.Equal(0, run.Job.Snapshot().ScannedCount);
        mgr.ScanLimiter.Release(all);
        run.Join();
        Assert.Equal(JobStatus.Completed, run.Job.Status);
        Assert.Null(run.Job.Note);
        Assert.True(expected.SequenceEqual(run.Matches()));
        Assert.Equal(3, mgr.ScanLimiter.Free);

        // and a waiting search can be cancelled
        spool.DisposeAll();
        all = mgr.ScanLimiter.AcquireUpTo(3, default);
        var run2 = Start(mgr, "Test_wait", spec, spool, reg);
        sw.Restart();
        while (run2.Job.Note != SearchRunner.WaitingForThreadNote && sw.Elapsed < TimeSpan.FromSeconds(10)) Thread.Sleep(10);
        run2.Job.Cts.Cancel();
        run2.Join(10);
        Assert.Equal(JobStatus.Cancelled, run2.Job.Status);
        mgr.ScanLimiter.Release(all);
        Assert.Equal(3, mgr.ScanLimiter.Free);
    }

    /// <summary>Timing only (not a regression test): serial vs parallel search on a generated db. Skipped unless HOARDERDB_PAR_KEYS is set (e.g. 6000000).</summary>
    [Fact]
    public void Speedup_Measurement()
    {
        if (!int.TryParse(Environment.GetEnvironmentVariable("HOARDERDB_PAR_KEYS"), out int n) || n <= 0) return;
        var path = Path.Combine(_root, "Test_speed");
        var rnd = new Random(3);
        using (var db = new WordlistDatabase(path, new UTF8Encoding(false), enableAutoCompaction: false))
        {
            for (int part = 0; part < 6; part++)
                db.Import(Enumerable.Range(0, n / 6).Select(_ => string.Create(6 + rnd.Next(12), rnd, (sp, r) => { for (int i = 0; i < sp.Length; i++) sp[i] = (char)('a' + r.Next(26)); })), null, null, threads: 2);
            db.Compact();
        }
        SearchRunner.TileTargetBytes = null;
        foreach (int cap in new[] { 1, 3, 3, 1 })
        {
            var mgr = Host(cap, out var spool, out var reg);
            var sw = Stopwatch.StartNew();
            var run = Start(mgr, "Test_speed", new SearchSpec("qzqz", true, true), spool, reg);
            run.Join(600);
            var segs = run.Log.Segments();
            output.WriteLine($"cap {cap}: {sw.ElapsedMilliseconds} ms, {run.Job.Snapshot().ScannedCount:N0} keys, {run.Job.Snapshot().MatchedCount} matches, workers {segs.Max(g => g.Workers)}, tiles {segs[0].Tiles}");
            spool.DisposeAll();
            mgr.DisposeAsync().AsTask().Wait();
            _mgrs.Remove(mgr);
        }
    }

    [Fact]
    public void BlockedWaiter_GetsPermitsAfterTheNextYield_AndSearchResumesExactly()
    {
        var path = Path.Combine(_root, "Test_fair");
        HoarderDbWebFactory.Seed(path, Big().Take(40_000));
        var spec = new SearchSpec("needle", false, true);
        var (expected, keys) = Oracle(path, spec);
        SearchRunner.TileTargetBytes = null;
        SearchRunner.MaxLeaseAge = TimeSpan.FromMilliseconds(60);
        SlowScan();
        var mgr = Host(3, out var spool, out var reg);
        var lim = mgr.ScanLimiter;
        var run = Start(mgr, "Test_fair", spec, spool, reg);
        var sw = Stopwatch.StartNew();
        while (lim.Free > 0 && sw.Elapsed < TimeSpan.FromSeconds(20)) Thread.Sleep(5);   // the search holds every permit
        var waiter = Task.Run(() => { int p = lim.AcquireUpTo(1, default); Thread.Sleep(200); lim.Release(p); return sw.Elapsed; });
        // (the waiter may also win the very first gap; either way it must get through quickly)
        Assert.True(waiter.Wait(TimeSpan.FromSeconds(20)), "a blocked scan must not starve behind the search");
        run.Join();
        Assert.Equal(JobStatus.Completed, run.Job.Status);
        Assert.True(expected.SequenceEqual(run.Matches()));
        Assert.Equal(keys, run.Job.Snapshot().ScannedCount);
        Assert.Equal(3, lim.Free);
        Assert.Equal(0, lim.Waiting);
    }

    [Fact]
    public void Permits_ReturnAfterAWorkerFault()
    {
        MakeDb("Test_fault", "manyL0");
        var mgr = Host(3, out var spool, out var reg);
        SearchRunner.StrideHook = s => { if (s > 2000) throw new InvalidOperationException("boom"); };
        var run = Start(mgr, "Test_fault", new SearchSpec("needle", false, true), spool, reg);
        run.Join();
        Assert.Equal(JobStatus.Failed, run.Job.Status);
        Assert.Equal(3, mgr.ScanLimiter.Free);
    }

    [Fact]
    public async Task Permits_AreNotHeldDuringTheBusyBackoff()
    {
        var path = MakeDb("Test_busy", "manyL0");
        var spec = new SearchSpec("needle", false, true);
        var (expected, _) = Oracle(path, spec);
        var mgr = Host(3, out var spool, out var reg);
        var inside = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var write = mgr.WithWriteAsync("Test_busy", DbOpenMode.Read, async (_, _) => { inside.SetResult(); await release.Task; return true; }, CancellationToken.None);
        await inside.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var run = Start(mgr, "Test_busy", spec, spool, reg);
        var sw = Stopwatch.StartNew();
        while (run.Job.Note is null || !run.Job.Note.StartsWith("Paused") ) { Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10)); await Task.Delay(10); }
        await Task.Delay(100);
        Assert.Equal(3, mgr.ScanLimiter.Free);       // backing off holds no scan thread
        release.SetResult();
        await write;
        run.Join();
        Assert.Equal(JobStatus.Completed, run.Job.Status);
        Assert.True(expected.SequenceEqual(run.Matches()));
        Assert.Equal(3, mgr.ScanLimiter.Free);
    }

    [Fact]
    public void SegmentLog_IsNumbersOnly()
    {
        MakeDb("Test_log", "manyL0");
        var mgr = Host(3, out var spool, out var reg);
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
}

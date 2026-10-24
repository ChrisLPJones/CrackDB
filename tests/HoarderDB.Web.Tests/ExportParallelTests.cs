using System.Text;
using HoarderDB.Web.Endpoints;
using HoarderDB.Web.Options;
using HoarderDB.Web.Storage;

#pragma warning disable xUnit1031

namespace HoarderDB.Web.Tests;

/// <summary>
/// The ordered tile fan-out of the streamed export (D2): bytes must equal the serial path and the Range() oracle, and the
/// join-before-lease / permits-last rules must hold on every path. Throwaway Test_&lt;guid&gt; data roots only; statics are
/// restored in Dispose.
/// </summary>
public sealed class ExportParallelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Test_" + Guid.NewGuid());
    private readonly List<DbManager> _mgrs = new();
    private readonly int _chunk = ExportPipeline.ChunkBytes;
    private readonly long _cap = ExportPipeline.TileCapBytes;
    private readonly long? _target = ExportPipeline.TileTargetBytes;
    private readonly Action<long>? _hook = ExportPipeline.ReaderFaultHook;

    public ExportParallelTests()
    {
        HoarderDbWebFactory.EnsureThrowaway(_root);
        Directory.CreateDirectory(_root);
        ExportPipeline.TileTargetBytes = 1;   // many tiles even on a small db
    }

    public void Dispose()
    {
        ExportPipeline.ChunkBytes = _chunk;
        ExportPipeline.TileCapBytes = _cap;
        ExportPipeline.TileTargetBytes = _target;
        ExportPipeline.ReaderFaultHook = _hook;
        foreach (var m in _mgrs) m.DisposeAsync().AsTask().Wait();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private DbManager Host(int cap)
    {
        var m = new DbManager(new DataRootResolver(_root),
            storage: Microsoft.Extensions.Options.Options.Create(new StorageOptions { ScanThreads = cap }));
        _mgrs.Add(m);
        return m;
    }

    private static List<string> Words()
    {
        var rnd = new Random(7);
        const string alpha = "abcdefghijklmnopqrstuvwxyz";
        string R(int n) => string.Create(n, rnd, (s, r) => { for (int i = 0; i < s.Length; i++) s[i] = alpha[r.Next(alpha.Length)]; });
        var w = new List<string>();
        for (int i = 0; i < 12_000; i++) w.Add(R(9));
        for (int i = 0; i < 3_000; i++) w.Add(R(3 + rnd.Next(20)));
        for (int i = 0; i < 300; i++) { w.Add("café" + i); w.Add("日本語" + i); w.Add("😀x" + i); w.Add("cr\r" + i); }
        return w.Distinct().ToList();
    }

    private string MakeDb(string name, int parts)
    {
        var path = Path.Combine(_root, name);
        var words = Words();
        using var db = new WordlistDatabase(path, new UTF8Encoding(false), enableAutoCompaction: false);
        int n = words.Count / parts + 1;
        for (int i = 0; i < words.Count; i += n) db.Import(words.Skip(i).Take(n), null, null, threads: 1);
        return path;
    }

    private static byte[] Oracle(DbManager mgr, string name, int? min, int? max, long? limit)
    {
        using var lease = mgr.AcquireRead(name);
        var sb = new StringBuilder();
        foreach (var w in lease.Db.Range(min, max, limit)) sb.Append(w).Append('\n');
        return new UTF8Encoding(false).GetBytes(sb.ToString());
    }

    private sealed class Outcome { public byte[] Bytes = []; public bool Aborted; public ExportPlan? Plan; }

    // Mirrors StreamingExportResult: plan, run, lease released, THEN permits released.
    private static async Task<Outcome> Run(DbManager mgr, string name, int? min, int? max, long? limit, Stream? sink = null, CancellationToken ct = default)
    {
        var o = new Outcome();
        var ms = new MemoryStream();
        var lease = mgr.AcquireRead(name);
        ExportPlan? plan = null;
        try
        {
            plan = ExportPipeline.TryPlan(lease, mgr.ScanLimiter, min, max, limit, ct);
            o.Plan = plan;
            await ExportPipeline.RunAsync(lease, sink ?? ms, min, max, limit, null, () => o.Aborted = true, ct, plan)
                .WaitAsync(TimeSpan.FromSeconds(60));
        }
        finally { lease.Dispose(); plan?.Dispose(); }
        o.Bytes = ms.ToArray();
        return o;
    }

    private static void AssertClean(DbManager mgr)
    {
        Assert.Equal(mgr.ScanLimiter.Cap, mgr.ScanLimiter.Free);
        Assert.Equal(0, ExportPipeline.LiveWorkerCursors);
        Assert.Equal(0, ExportPipeline.ChunksInFlight);
    }

    [Theory]
    [InlineData(null, null, 4096L, 4L * 1024 * 1024)]
    [InlineData(null, null, 256L, 4L * 1024 * 1024)]
    [InlineData(null, null, 512L, 1L)]            // tiny per-tile cap: deadlock guard
    [InlineData(3, 12, 1024L, 1L)]
    [InlineData(null, 5, 4096L, 4L * 1024 * 1024)]
    [InlineData(9, 9, 4096L, 4L * 1024 * 1024)]
    public async Task Parallel_EqualsSerialAndOracle(int? min, int? max, long chunkBytes, long tileCap)
    {
        MakeDb("Test_many", 10);
        var mgr = Host(3);   // 2 export workers
        var expected = Oracle(mgr, "Test_many", min, max, null);
        ExportPipeline.ChunkBytes = (int)chunkBytes;
        ExportPipeline.TileCapBytes = tileCap;
        var o = await Run(mgr, "Test_many", min, max, null);
        Assert.NotNull(o.Plan);
        Assert.Equal(2, o.Plan!.Workers);
        Assert.True(o.Plan.Tiles.Count >= 3, "expected several tiles, got " + o.Plan.Tiles.Count);
        Assert.Equal(expected, o.Bytes);
        Assert.False(o.Aborted);
        AssertClean(mgr);
    }

    [Fact]
    public async Task EmptyRange_Parallel_WritesNothing()
    {
        MakeDb("Test_many", 10);
        var mgr = Host(3);
        var o = await Run(mgr, "Test_many", 60, 70, null);
        Assert.Empty(o.Bytes);
        Assert.False(o.Aborted);
        AssertClean(mgr);
    }

    [Fact]
    public async Task SlowClient_BoundedMemory_NoDeadlock_SameBytes()
    {
        MakeDb("Test_many", 10);
        var mgr = Host(3);
        var expected = Oracle(mgr, "Test_many", null, null, null);
        ExportPipeline.ChunkBytes = 512;
        ExportPipeline.TileCapBytes = 2048;
        ExportPipeline.ResetPeak();
        var sink = new SlowStream(1);
        var o = await Run(mgr, "Test_many", null, null, null, sink);
        Assert.NotNull(o.Plan);
        Assert.Equal(expected, sink.ToArray());
        // window (2 x workers) x (cap + one chunk) + the chunk being filled per worker + the one being written
        int bound = 4 * (2048 / 512 + 1) + 2 + 1;
        Assert.True(ExportPipeline.PeakChunksInFlight <= bound, $"peak {ExportPipeline.PeakChunksInFlight} > {bound}");
        AssertClean(mgr);
    }

    private sealed class SlowStream(int delayMs) : MemoryStream
    {
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            await Task.Delay(delayMs, ct);
            await base.WriteAsync(buffer, ct);
        }
    }

    private sealed class GatedStream : MemoryStream
    {
        public readonly TaskCompletionSource FirstWrite = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            FirstWrite.TrySetResult();
            await Gate.Task.WaitAsync(ct);
            await base.WriteAsync(buffer, ct);
        }
    }

    [Fact]
    public async Task ClientAbort_JoinsWorkers_ReleasesLeaseAndPermits()
    {
        MakeDb("Test_many", 10);
        var mgr = Host(3);
        ExportPipeline.ChunkBytes = 256;
        ExportPipeline.TileCapBytes = 1024;
        var sink = new GatedStream();
        using var cts = new CancellationTokenSource();
        var o = new Outcome();
        var lease = mgr.AcquireRead("Test_many");
        var plan = ExportPipeline.TryPlan(lease, mgr.ScanLimiter, null, null, null, cts.Token);
        Assert.NotNull(plan);
        Assert.True(mgr.ScanLimiter.Free < 3);
        var run = ExportPipeline.RunAsync(lease, sink, null, null, null, null, () => o.Aborted = true, cts.Token, plan);
        await sink.FirstWrite.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Task.Delay(150);   // workers fill their tiles and block on the full buffers
        Assert.True(ExportPipeline.LiveWorkerCursors >= 1);
        cts.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, ExportPipeline.LiveWorkerCursors);   // joined before RunAsync returned
        lease.Dispose();
        plan!.Dispose();
        Assert.False(o.Aborted);   // a client abort ends quietly
        AssertClean(mgr);
        Assert.True(await mgr.WithWriteAsync("Test_many", DbOpenMode.Read,
            (db, _) => Task.FromResult(db.Contains("café1")), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task WorkerFault_AbortsConnection_ReleasesLeaseAndPermits()
    {
        MakeDb("Test_many", 10);
        var mgr = Host(3);
        ExportPipeline.ChunkBytes = 256;
        long n = 0;
        ExportPipeline.ReaderFaultHook = _ => { if (Interlocked.Increment(ref n) == 5) throw new InvalidOperationException("injected secret-ish text"); };
        var logged = new List<string>();
        var o = new Outcome();
        var lease = mgr.AcquireRead("Test_many");
        var plan = ExportPipeline.TryPlan(lease, mgr.ScanLimiter, null, null, null, default);
        Assert.NotNull(plan);
        await ExportPipeline.RunAsync(lease, new MemoryStream(), null, null, null, new ListLogger(logged), () => o.Aborted = true, default, plan)
            .WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(0, ExportPipeline.LiveWorkerCursors);
        lease.Dispose();
        plan!.Dispose();
        Assert.True(o.Aborted);
        AssertClean(mgr);
        Assert.NotEmpty(logged);
        Assert.All(logged, l => Assert.DoesNotContain("injected", l));
        Assert.True(await mgr.WithWriteAsync("Test_many", DbOpenMode.Read,
            (db, _) => Task.FromResult(db.Contains("café1")), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task Shutdown_Closing_EndsParallelStreamWithAbort()
    {
        MakeDb("Test_many", 10);
        var mgr = Host(3);
        ExportPipeline.ChunkBytes = 256;
        var sink = new GatedStream();
        var o = new Outcome();
        var lease = mgr.AcquireRead("Test_many");
        var plan = ExportPipeline.TryPlan(lease, mgr.ScanLimiter, null, null, null, default);
        Assert.NotNull(plan);
        var run = ExportPipeline.RunAsync(lease, sink, null, null, null, null, () => o.Aborted = true, default, plan);
        await sink.FirstWrite.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var close = Task.Run(() => mgr.DisposeAsync().AsTask());
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!lease.Closing && sw.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(10);
        Assert.True(lease.Closing);
        sink.Gate.SetResult();
        await run.WaitAsync(TimeSpan.FromSeconds(10));
        lease.Dispose();
        plan!.Dispose();
        await close.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.True(o.Aborted);
        AssertClean(mgr);
    }

    [Fact]
    public async Task Fallbacks_SerialPath_IdenticalBytes()
    {
        MakeDb("Test_many", 10);
        MakeDb("Test_toomany", 20);
        using (var d = new WordlistDatabase(Path.Combine(_root, "Test_toomany"), new UTF8Encoding(false), enableAutoCompaction: false))
            Assert.True(d.NumFilesAtLevel0() > 16, "fixture must exceed the parallel L0 guard");

        // limit set
        var m3 = Host(3);
        var o = await Run(m3, "Test_many", null, null, 500);
        Assert.Null(o.Plan);
        Assert.Equal(Oracle(m3, "Test_many", null, null, 500), o.Bytes);
        AssertClean(m3);

        // 0 free permits (a scan holds everything)
        int held = m3.ScanLimiter.TryAcquireUpTo(3);
        Assert.Equal(3, held);
        o = await Run(m3, "Test_many", null, null, null);
        Assert.Null(o.Plan);
        Assert.Equal(Oracle(m3, "Test_many", null, null, null), o.Bytes);
        m3.ScanLimiter.Release(held);

        await m3.DisposeAsync();
        _mgrs.Remove(m3);

        // ScanThreads = 1
        var m1 = Host(1);
        o = await Run(m1, "Test_many", null, null, null);
        Assert.Null(o.Plan);
        Assert.Equal(Oracle(m1, "Test_many", null, null, null), o.Bytes);
        AssertClean(m1);
        await m1.DisposeAsync();
        _mgrs.Remove(m1);

        // L0 > 16
        var mL = Host(3);
        o = await Run(mL, "Test_toomany", null, null, null);
        Assert.Null(o.Plan);
        Assert.Equal(Oracle(mL, "Test_toomany", null, null, null), o.Bytes);
        AssertClean(mL);
    }

    [Fact]
    public async Task TwoPermitCap_NeverFansOut_PermitsUntouched()
    {
        MakeDb("Test_many", 10);
        var mgr = Host(2);
        var o = await Run(mgr, "Test_many", null, null, null);
        Assert.Null(o.Plan);
        Assert.Equal(Oracle(mgr, "Test_many", null, null, null), o.Bytes);
        AssertClean(mgr);
    }

    [Fact]
    public void OnlyOneFreePermit_NoPlan_FreeUnchanged()
    {
        MakeDb("Test_many", 10);
        var mgr = Host(3);
        Assert.Equal(2, mgr.ScanLimiter.TryAcquireUpTo(2));   // another scan holds two
        using var lease = mgr.AcquireRead("Test_many");
        Assert.Null(ExportPipeline.TryPlan(lease, mgr.ScanLimiter, null, null, null, default));
        Assert.Equal(1, mgr.ScanLimiter.Free);
        mgr.ScanLimiter.Release(2);
    }

    [Fact]
    public void Plan_LeavesOneFreePermit_AndNeverTakesTheLastFree()
    {
        MakeDb("Test_many", 10);
        var mgr = Host(4);
        using var lease = mgr.AcquireRead("Test_many");
        Assert.Equal(1, mgr.ScanLimiter.TryAcquireUpTo(1));   // 3 free: a plan may take 2 and must leave 1
        var plan = ExportPipeline.TryPlan(lease, mgr.ScanLimiter, null, null, null, default);
        Assert.NotNull(plan);
        Assert.Equal(1, mgr.ScanLimiter.Free);
        plan!.Dispose();
        Assert.Equal(3, mgr.ScanLimiter.Free);
        Assert.Equal(1, mgr.ScanLimiter.TryAcquireUpTo(1));   // 2 free: fewer than three, so fall back to serial
        Assert.Null(ExportPipeline.TryPlan(lease, mgr.ScanLimiter, null, null, null, default));
        Assert.Equal(2, mgr.ScanLimiter.Free);
        mgr.ScanLimiter.Release(2);
    }

    [Fact]
    public void TryAcquireUpToLeavingOne_Counting()
    {
        var l = new ScanLimiter(4);
        Assert.Equal(2, l.TryAcquireUpToLeavingOne(2)); Assert.Equal(2, l.Free);
        Assert.Equal(1, l.TryAcquireUpToLeavingOne(2)); Assert.Equal(1, l.Free);
        Assert.Equal(0, l.TryAcquireUpToLeavingOne(2)); Assert.Equal(1, l.Free);
        l.Release(3); Assert.Equal(4, l.Free);
    }

    private sealed class ListLogger(List<string> lines) : Microsoft.Extensions.Logging.ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId,
            TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        { lock (lines) lines.Add(formatter(state, exception) + (exception?.Message ?? "")); }
    }
}

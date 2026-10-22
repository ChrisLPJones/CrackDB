using System.Text;
using HoarderDB.Web.Endpoints;
using HoarderDB.Web.Storage;

#pragma warning disable xUnit1031

namespace HoarderDB.Web.Tests;

/// <summary>
/// The streamed export's reader-thread pipeline (D1): bytes must equal the Range() oracle, memory stays bounded behind a
/// slow client, and the reader is always joined before the lease is released (client abort, fault, shutdown).
/// Throwaway Test_&lt;guid&gt; data roots only; statics are restored in Dispose.
/// </summary>
public sealed class ExportPipelineTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Test_" + Guid.NewGuid());
    private readonly List<DbManager> _mgrs = new();
    private readonly int _chunk = ExportPipeline.ChunkBytes;
    private readonly Action<long>? _hook = ExportPipeline.ReaderFaultHook;

    public ExportPipelineTests()
    {
        HoarderDbWebFactory.EnsureThrowaway(_root);
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        ExportPipeline.ChunkBytes = _chunk;
        ExportPipeline.ReaderFaultHook = _hook;
        foreach (var m in _mgrs) m.DisposeAsync().AsTask().Wait();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private DbManager Mgr()
    {
        var m = new DbManager(new DataRootResolver(_root));
        _mgrs.Add(m);
        return m;
    }

    private void Seed(string name, IEnumerable<string> words, Encoding? enc = null)
    {
        var path = Path.Combine(_root, name);
        if (enc == null) { HoarderDbWebFactory.Seed(path, words); return; }
        using var db = new WordlistDatabase(path, enc);
        db.Import(words, null, null, threads: 2);
    }

    private static string[] Mixed()
    {
        var w = new List<string> { "a", "ab", "abc", "zz", "café", "naïve", "日本語", "\U0001F600x", "with\rcr", "cr\r", "tab\tx", "" , "longerwordhere" };
        for (int i = 0; i < 3000; i++) w.Add("w" + i.ToString("D5") + (i % 7 == 0 ? "ü" : ""));
        return w.Where(x => x.Length > 0).ToArray();
    }

    private sealed class Outcome { public byte[] Bytes = []; public bool Aborted; }

    private static async Task<Outcome> Run(DbManager mgr, string name, int? min, int? max, long? limit, Stream? sink = null, CancellationToken ct = default)
    {
        var o = new Outcome();
        var ms = new MemoryStream();
        using var lease = mgr.AcquireRead(name);
        await ExportPipeline.RunAsync(lease, sink ?? ms, min, max, limit, null, () => o.Aborted = true, ct);
        o.Bytes = ms.ToArray();
        return o;
    }

    private static byte[] Oracle(DbManager mgr, string name, int? min, int? max, long? limit)
    {
        using var lease = mgr.AcquireRead(name);
        var sb = new StringBuilder();
        foreach (var w in lease.Db.Range(min, max, limit)) sb.Append(w).Append('\n');
        return new UTF8Encoding(false).GetBytes(sb.ToString());
    }

    [Theory]
    [InlineData(null, null, null)]
    [InlineData(2, 5, null)]
    [InlineData(null, 3, null)]
    [InlineData(4, null, null)]
    [InlineData(3, 3, null)]
    [InlineData(null, null, 10L)]
    [InlineData(null, null, 1L)]
    [InlineData(2, 4, 7L)]
    [InlineData(50, 60, null)]   // empty range
    public async Task Bytes_EqualRangeOracle(int? min, int? max, long? limit)
    {
        Seed("Test_mixed", Mixed());
        var mgr = Mgr();
        var expected = Oracle(mgr, "Test_mixed", min, max, limit);
        // Default chunk size, then tiny chunks (many chunks, words straddling flushes, words larger than a chunk).
        Assert.Equal(expected, (await Run(mgr, "Test_mixed", min, max, limit)).Bytes);
        ExportPipeline.ChunkBytes = 16;
        var tiny = await Run(mgr, "Test_mixed", min, max, limit);
        Assert.Equal(expected, tiny.Bytes);
        Assert.False(tiny.Aborted);
        Assert.Equal(0, ExportPipeline.ChunksInFlight);
    }

    [Fact]
    public async Task EmptyDb_WritesNothing()
    {
        Seed("Test_empty", Array.Empty<string>());
        var o = await Run(Mgr(), "Test_empty", null, null, null);
        Assert.Empty(o.Bytes);
        Assert.False(o.Aborted);
    }

    [Fact]
    public async Task InvalidUtf8Key_MatchesOldDecodePath()
    {
        // Stored as Latin-1 (0xE9 is not valid UTF-8), then read through the normal UTF-8 handle.
        Seed("Test_latin", new[] { "café", "plain", "naïve", "éé" }, Encoding.Latin1);
        var mgr = Mgr();
        var expected = Oracle(mgr, "Test_latin", null, null, null);
        var got = (await Run(mgr, "Test_latin", null, null, null)).Bytes;
        Assert.Equal(expected, got);
        Assert.True(got.AsSpan().IndexOf(new byte[] { 0xEF, 0xBF, 0xBD }) >= 0, "expected U+FFFD in the output");
    }

    private sealed class SlowStream(int delayMs) : MemoryStream
    {
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            await Task.Delay(delayMs, ct);
            await base.WriteAsync(buffer, ct);
        }
    }

    [Fact]
    public async Task SlowClient_BoundedMemory_NoDeadlock_SameBytes()
    {
        Seed("Test_mixed", Mixed());
        var mgr = Mgr();
        var expected = Oracle(mgr, "Test_mixed", null, null, null);
        ExportPipeline.ChunkBytes = 256;
        ExportPipeline.ResetPeak();
        var sink = new SlowStream(1);
        var o = await Run(mgr, "Test_mixed", null, null, null, sink).WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Equal(expected, sink.ToArray());
        // queued chunks + the one being filled + the one being written
        Assert.True(ExportPipeline.PeakChunksInFlight <= ExportPipeline.QueueDepth + 2,
            $"peak {ExportPipeline.PeakChunksInFlight}");
        Assert.Equal(0, ExportPipeline.ChunksInFlight);
        Assert.False(o.Aborted);
    }

    // Blocks every write until released; signals when the first write starts.
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
    public async Task ClientAbort_JoinsReader_ReleasesLease()
    {
        Seed("Test_mixed", Mixed());
        var mgr = Mgr();
        ExportPipeline.ChunkBytes = 64;
        var sink = new GatedStream();
        using var cts = new CancellationTokenSource();
        var o = new Outcome();
        var lease = mgr.AcquireRead("Test_mixed");
        var run = ExportPipeline.RunAsync(lease, sink, null, null, null, null, () => o.Aborted = true, cts.Token);
        await sink.FirstWrite.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Task.Delay(100);   // let the reader fill the queue and block on it
        cts.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(10));
        lease.Dispose();
        Assert.False(o.Aborted);   // a client abort ends quietly
        Assert.Equal(0, ExportPipeline.ChunksInFlight);
        Assert.True(await mgr.WithWriteAsync("Test_mixed", DbOpenMode.Read,
            (db, _) => Task.FromResult(db.Contains("ab")), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task ReaderFault_AbortsConnection_ReleasesLease()
    {
        Seed("Test_mixed", Mixed());
        var mgr = Mgr();
        ExportPipeline.ChunkBytes = 64;
        ExportPipeline.ReaderFaultHook = i => { if (i == 3) throw new InvalidOperationException("injected secret-ish text"); };
        var logged = new List<string>();
        var o = new Outcome();
        var lease = mgr.AcquireRead("Test_mixed");
        await ExportPipeline.RunAsync(lease, new MemoryStream(), null, null, null, new ListLogger(logged), () => o.Aborted = true, default)
            .WaitAsync(TimeSpan.FromSeconds(10));
        lease.Dispose();
        Assert.True(o.Aborted);
        Assert.Equal(0, ExportPipeline.ChunksInFlight);
        Assert.NotEmpty(logged);
        Assert.All(logged, l => Assert.DoesNotContain("injected", l));   // exception message never logged
        Assert.True(await mgr.WithWriteAsync("Test_mixed", DbOpenMode.Read,
            (db, _) => Task.FromResult(db.Contains("ab")), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task Shutdown_Closing_EndsStreamWithAbort()
    {
        Seed("Test_mixed", Mixed());
        var mgr = Mgr();
        ExportPipeline.ChunkBytes = 64;
        var sink = new GatedStream();
        var o = new Outcome();
        var lease = mgr.AcquireRead("Test_mixed");
        var run = ExportPipeline.RunAsync(lease, sink, null, null, null, null, () => o.Aborted = true, default);
        await sink.FirstWrite.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var close = Task.Run(() => mgr.DisposeAsync().AsTask());   // sets Closing, then waits for the reader lease
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!lease.Closing && sw.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(10);
        Assert.True(lease.Closing);
        sink.Gate.SetResult();
        await run.WaitAsync(TimeSpan.FromSeconds(10));
        lease.Dispose();
        await close.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.True(o.Aborted);
        Assert.Equal(0, ExportPipeline.ChunksInFlight);
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

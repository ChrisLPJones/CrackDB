using System.Buffers;
using System.Text;
using System.Text.Unicode;
using System.Threading.Channels;
using HoarderDB.Web.Jobs;
using HoarderDB.Web.Storage;

namespace HoarderDB.Web.Endpoints;

/// <summary>
/// The body of a streamed export: ONE dedicated reader thread walks a scan cursor and fills pooled byte chunks into a
/// bounded queue while the async handler only awaits the socket writes, so reading and sending overlap (before, the
/// two strictly alternated). Output is byte-for-byte what <c>Range()</c> joined with "\n" gives (UTF-8, no BOM):
/// keys that are valid UTF-8 in a plain UTF-8 database are copied as they are; anything else goes through the string
/// decode/re-encode (U+FFFD replacement), exactly as before.
/// Lifecycle rule: the reader (and its cursor) is always finished before <see cref="RunAsync"/> returns, so the caller
/// may release the read lease right after. Memory is bounded: <see cref="QueueDepth"/> queued chunks plus the one being
/// filled and the one being written.
/// </summary>
internal static class ExportPipeline
{
    /// <summary>Chunk size; above Kestrel's 64 KiB response-pipe threshold so each write is worth one await.</summary>
    internal static int ChunkBytes = 512 * 1024;
    internal const int QueueDepth = 4;

    /// <summary>Test seam: called on the reader thread before each chunk is queued (argument = chunk index); may throw to inject a fault.</summary>
    internal static Action<long>? ReaderFaultHook;

    // Test diagnostics: chunk arrays currently rented from the pool, and the peak since the last reset.
    private static long _inFlight, _peak;
    internal static long PeakChunksInFlight => Interlocked.Read(ref _peak);
    internal static long ChunksInFlight => Interlocked.Read(ref _inFlight);
    internal static void ResetPeak() => Interlocked.Exchange(ref _peak, Interlocked.Read(ref _inFlight));

    private readonly record struct Chunk(byte[] Buf, int Len);

    internal static byte[] Rent(int size)
    {
        var b = ArrayPool<byte>.Shared.Rent(size);
        long n = Interlocked.Increment(ref _inFlight);
        long p;
        while (n > (p = Interlocked.Read(ref _peak)) && Interlocked.CompareExchange(ref _peak, n, p) != p) { }
        return b;
    }

    internal static void Return(byte[] b)
    {
        Interlocked.Decrement(ref _inFlight);
        ArrayPool<byte>.Shared.Return(b);
    }

    /// <summary>
    /// Streams the export to <paramref name="body"/>. A reader fault or a shutdown calls <paramref name="abort"/> (reset the
    /// connection: the chunked body is NOT finished, so the client cannot mistake a truncated file for a complete one).
    /// A client disconnect just ends quietly. Log lines are fixed text: never a password, never a key.
    /// </summary>
    public static Task RunAsync(DbLease lease, Stream body, int? min, int? max, long? limit,
        ILogger? log, Action abort, CancellationToken ct, ExportPlan? plan = null) =>
        plan is null ? RunSerialAsync(lease, body, min, max, limit, log, abort, ct)
                     : RunParallelAsync(lease, body, min, max, plan, log, abort, ct);

    private static async Task RunSerialAsync(DbLease lease, Stream body, int? min, int? max, long? limit,
        ILogger? log, Action abort, CancellationToken ct)
    {
        var ch = Channel.CreateBounded<Chunk>(new BoundedChannelOptions(QueueDepth)
        { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
        using var stop = new CancellationTokenSource();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool started = false;
        try
        {
            var db = lease.Db;
            var thread = new Thread(() => ReaderMain(db, min, max, limit, ch.Writer, stop.Token, done))
            { IsBackground = true, Name = "export-reader" };
            thread.Start();
            started = true;

            while (await ch.Reader.WaitToReadAsync(ct))
            {
                while (ch.Reader.TryRead(out var c))
                {
                    try
                    {
                        if (lease.Closing)
                        {
                            log?.LogWarning("Export stopped: the server is shutting down.");
                            abort();
                            return;
                        }
                        ct.ThrowIfCancellationRequested();
                        await body.WriteAsync(c.Buf.AsMemory(0, c.Len), ct);
                    }
                    finally { Return(c.Buf); }
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { /* client went away */ }
        catch (Exception e)
        {
            // Headers are already sent, so there is no 500: reset the connection. Type name only, no message.
            log?.LogError("Export failed mid-stream ({ExceptionType}); connection aborted.", e.GetType().Name);
            abort();
        }
        finally
        {
            stop.Cancel();
            if (started) await done.Task;   // reader + cursor are gone before the caller releases the lease
            while (ch.Reader.TryRead(out var c)) Return(c.Buf);
        }
    }

    private static void ReaderMain(WordlistDatabase db, int? min, int? max, long? limit,
        ChannelWriter<Chunk> queue, CancellationToken stop, TaskCompletionSource done)
    {
        byte[]? buf = null;
        try
        {
            using var cur = db.OpenScan(min, max);
            cur.SeekTo(null);
            int chunkSize = ChunkBytes;
            buf = Rent(chunkSize);
            int n = 0;
            long produced = 0, chunkIndex = 0;
            var utf8 = new UTF8Encoding(false);

            void Flush()
            {
                if (n == 0) return;
                ReaderFaultHook?.Invoke(chunkIndex++);
                var c = new Chunk(buf!, n);
                while (!queue.TryWrite(c))
                    queue.WaitToWriteAsync(stop).AsTask().GetAwaiter().GetResult();   // dedicated thread: blocking is fine
                buf = null;
                n = 0;
            }

            while (cur.MoveNext())
            {
                if (stop.IsCancellationRequested) return;
                var key = cur.PasswordBytes;
                bool copy = cur.IsPlainUtf8 && Utf8.IsValid(key);
                string? word = copy ? null : cur.Word;
                int need = (copy ? key.Length : utf8.GetByteCount(word!)) + 1;
                if (buf == null || n + need > buf.Length)
                {
                    Flush();
                    if (buf != null && need > buf.Length) { Return(buf); buf = null; }
                    buf ??= Rent(Math.Max(chunkSize, need));
                }
                if (copy) key.CopyTo(buf.AsSpan(n)); else utf8.GetBytes(word!, buf.AsSpan(n));
                n += need - 1;
                buf[n++] = (byte)'\n';
                if (limit.HasValue && ++produced >= limit.Value) break;
            }
            Flush();
            queue.TryComplete();
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { queue.TryComplete(); }
        catch (Exception e) { queue.TryComplete(e); }
        finally
        {
            if (buf != null) Return(buf);
            done.TrySetResult();
        }
    }

    // ---- D2: ordered tile fan-out ------------------------------------------------------------------------------

    /// <summary>Export workers at most (internal, not config): every extra iterator pins the index of every L0 file for the whole download.</summary>
    internal const int MaxWorkers = 2;
    /// <summary>Per-tile byte cap (an empty tile admits one chunk); test knob.</summary>
    internal static long TileCapBytes = 4L * 1024 * 1024;
    /// <summary>Test knob: tile size target in bytes of live files (null = total / (threads x 8)); small values make many tiles on a small db.</summary>
    internal static long? TileTargetBytes;

    private static long _liveCursors, _chunkCounter;
    /// <summary>Test diagnostic: parallel-worker cursors currently open (must be 0 once RunAsync returned).</summary>
    internal static long LiveWorkerCursors => Interlocked.Read(ref _liveCursors);

    /// <summary>
    /// Decides whether this export fans out. Null = use the serial path (same bytes): a limit is set, ScanThreads is 1,
    /// no permit is free, the db has more than 16 L0 files, or the range is a single tile. Otherwise holds 1..2 scan
    /// permits (never the last one: at most Cap-1 are taken, non-blocking) until <see cref="ExportPlan.Dispose"/>, which the
    /// caller must call AFTER releasing the lease.
    /// </summary>
    internal static ExportPlan? TryPlan(DbLease lease, ScanLimiter limiter, int? min, int? max, long? limit, CancellationToken ct)
    {
        if (limit.HasValue || limiter.Cap < 2 || !lease.Db.ParallelScanAllowed) return null;
        int got = limiter.TryAcquireUpTo(Math.Min(MaxWorkers, limiter.Cap - 1));
        if (got < 2) { limiter.Release(got); return null; }   // one worker adds nothing over D1 and could take the last free permit
        try
        {
            var tiles = lease.Db.PlanSearchTiles(min, max, limiter.Cap, ct, TileTargetBytes);
            if (tiles.Count < 2) { limiter.Release(got); return null; }
            int workers = Math.Min(got, tiles.Count);
            if (got > workers) limiter.Release(got - workers);
            return new ExportPlan(limiter, workers, tiles);
        }
        catch { limiter.Release(got); throw; }
    }

    private static async Task RunParallelAsync(DbLease lease, Stream body, int? min, int? max, ExportPlan plan,
        ILogger? log, Action abort, CancellationToken ct)
    {
        var buf = new OrderedChunkBuffers(plan.Tiles.Count, TileCapBytes);
        buf.SetWindow(2 * plan.Workers);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);   // client abort OR our own teardown
        using var wake = stop.Token.Register(buf.Wake);
        var workers = new List<Task>();
        string? failType = null;
        bool shutdown = false;
        try
        {
            var db = lease.Db;
            var token = stop.Token;
            for (int i = 0; i < plan.Workers; i++)
                workers.Add(Task.Factory.StartNew(() => WorkerMain(db, min, max, plan.Tiles, buf, token),
                    CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default));

            while (true)
            {
                if (lease.Closing) { shutdown = true; return; }
                ct.ThrowIfCancellationRequested();
                if (buf.Fault is { } wf) { failType = wf.GetType().Name; return; }
                if (buf.TryTake(out var c))
                {
                    try { await body.WriteAsync(c.Buf.AsMemory(0, c.Len), ct); }
                    finally { Return(c.Buf); }
                    continue;
                }
                if (buf.AllDrained) break;
                await buf.WaitActivityAsync(50, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { /* client went away */ }
        catch (Exception e) { failType = e.GetType().Name; }
        finally
        {
            stop.Cancel();
            await Task.WhenAll(workers);   // never abandoned: every worker disposed its cursor before the caller releases the lease
            buf.ReleaseAll(Return);
            if (failType != null || shutdown)
            {
                // Headers are already sent, so there is no 500: reset the connection (never finish the chunked body).
                // Fixed text and the exception type name only: no keys, no passwords, no exception messages.
                if (shutdown) log?.LogWarning("Export stopped: the server is shutting down.");
                else log?.LogError("Export failed mid-stream ({ExceptionType}); connection aborted.", failType);
                abort();
            }
        }
    }

    // One worker: ONE iterator for its whole life, reseeked per claimed tile. Never touches the socket.
    private static void WorkerMain(WordlistDatabase db, int? min, int? max, IReadOnlyList<WordlistDatabase.Tile> tiles,
        OrderedChunkBuffers buf, CancellationToken stop)
    {
        try
        {
            using var cur = db.OpenScan(min, max);
            Interlocked.Increment(ref _liveCursors);
            try
            {
                int chunkSize = ChunkBytes;
                var utf8 = new UTF8Encoding(false);
                while (true)
                {
                    int idx = buf.Claim(stop);
                    if (idx < 0) return;
                    var upper = tiles[idx].Upper;
                    cur.SeekToLower(tiles[idx].Lower);
                    byte[]? b = null;
                    int n = 0;
                    try
                    {
                        void Flush()
                        {
                            if (n == 0) return;
                            ReaderFaultHook?.Invoke(Interlocked.Increment(ref _chunkCounter) - 1);
                            buf.Add(idx, b!, n, stop);   // ownership passes on success
                            b = null;
                            n = 0;
                        }

                        while (cur.MoveNext())
                        {
                            if (upper is not null && cur.Key.SequenceCompareTo(upper) >= 0) break;
                            stop.ThrowIfCancellationRequested();
                            var key = cur.PasswordBytes;
                            bool copy = cur.IsPlainUtf8 && Utf8.IsValid(key);
                            string? word = copy ? null : cur.Word;
                            int need = (copy ? key.Length : utf8.GetByteCount(word!)) + 1;
                            if (b == null || n + need > b.Length)
                            {
                                Flush();
                                if (b != null && need > b.Length) { Return(b); b = null; }
                                b ??= Rent(Math.Max(chunkSize, need));
                            }
                            if (copy) key.CopyTo(b.AsSpan(n)); else utf8.GetBytes(word!, b.AsSpan(n));
                            n += need - 1;
                            b[n++] = (byte)'\n';
                        }
                        Flush();
                        buf.Complete(idx);
                    }
                    finally { if (b != null) Return(b); }
                }
            }
            finally { Interlocked.Decrement(ref _liveCursors); }   // the cursor itself is disposed by `using` right after
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception e) { buf.Fail(e); }
    }
}

/// <summary>
/// The scan permits and tiles of a parallel export (see <see cref="ExportPipeline.TryPlan"/>). Dispose releases the permits;
/// call it only after the read lease is released.
/// </summary>
internal sealed class ExportPlan(ScanLimiter limiter, int workers, List<WordlistDatabase.Tile> tiles) : IDisposable
{
    private int _permits = workers;
    public int Workers { get; } = workers;
    public List<WordlistDatabase.Tile> Tiles { get; } = tiles;
    public void Dispose() => limiter.Release(Interlocked.Exchange(ref _permits, 0));
}

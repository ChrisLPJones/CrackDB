using System.Diagnostics;
using HoarderDB.Web.Storage;

namespace HoarderDB.Web.Jobs;

/// <summary>Test seam, called after every lease segment's lease is released. Default is a no-op.</summary>
public interface ISearchProgressHook
{
    void OnChunk(string dbName, long scannedCount);
}

public sealed class NoopSearchProgressHook : ISearchProgressHook
{
    public void OnChunk(string dbName, long scannedCount) { }
}

/// <summary>
/// Executes a scanning search (ignore-case exact, or contains) as its own lightweight scanning path - deliberately never
/// through <see cref="JobQueue"/> (enqueuing there would occupy its one-job-at-a-time slot for the
/// whole scan and delay every unrelated import/compact behind it) and never by holding one
/// <see cref="IDbManager.AcquireRead"/> lease for the whole scan (that would itself stall a future
/// write window on this job's own db indefinitely, per <see cref="DbManager"/>'s untimed write-gate
/// spin-wait). Instead it takes its scan-thread permits, then a lease, and runs up to T worker threads over key-ordered
/// tiles (each worker holds ONE iterator for the segment; a small ordered-buffer class lets the search thread, the only spool
/// writer, append matches strictly in tile order, so the spool equals the serial scan's) and yields on demand: the segment ends when a
/// write window is waiting (<see cref="DbLease.WriterWaiting"/>), at shutdown, on cancel or at
/// <see cref="MaxLeaseAge"/>, then it re-acquires the lease (Busy back-off) and resumes from a raw-key cursor. So it only
/// ever contends with a write window on its own database - exactly like any ordinary read today - and never touches the
/// import/compact queue, and never blocks a search or job on a different database.
/// </summary>
public static class SearchRunner
{
    /// <summary>Below this many characters a scanning search is refused outright (enforced server-side).</summary>
    public const int MinQueryLength = SearchMatcher.MinChars;

    /// <summary>A write window (import/compact) on the searched db holds the single search slot while we wait; give up after this.</summary>
    public static TimeSpan BusyTimeout { get; set; } = TimeSpan.FromMinutes(30);
    public static TimeSpan BusyRetry { get; set; } = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Safety cap on one lease segment: the scan ends the segment and re-acquires the lease after this long even if
    /// nobody is waiting (bounds how long a lease can pin the handle; costs one seek per segment). Normally a segment
    /// ends earlier, on demand: <see cref="DbLease.WriterWaiting"/> (a queued import/compact), <see cref="DbLease.Closing"/>
    /// (shutdown) or cancel. Test knob (restore it); <see cref="TimeSpan.Zero"/> ends a segment at every stride.
    /// There is deliberately no per-segment key or time budget: a seek into a huge SST re-reads its whole index
    /// (see PROJECT.md Gotchas), and a budget that includes the seek would starve the scan.
    /// </summary>
    public static TimeSpan MaxLeaseAge { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>Test seam, null in production: called with the scanned count at every stride while the lease is held (lets a test slow or pause a scan deterministically).</summary>
    internal static Action<long>? StrideHook { get; set; }

    // Keys a worker scans between cancel checks and commits to the ordered buffers. ~1 ms of scanning at the measured count rate.
    private const int StrideKeys = 256;
    // How often the lease is polled for WriterWaiting / Closing (takes the entry gate, so not per stride).
    private static readonly long PollTicks = Stopwatch.Frequency / 50;   // ~20 ms
    // Per-tile match buffer caps and the size of one spool append (memory bound for dense queries): window x cap in all.
    // Test knobs (restore them): a tiny cap makes a match-dense query stall every non-head tile.
    internal static int BufferMatches { get; set; } = 64 * 1024;
    internal static long BufferChars { get; set; } = 8L * 1024 * 1024;
    // Progress visibility: the scanned count is published at least this often during a long segment.
    private static readonly long PublishTicks = Stopwatch.Frequency / 5;   // ~200 ms

    /// <summary>Test knob: tile size target in bytes of live files (null = total / (threads x 8)); small values make many tiles on a small db.</summary>
    internal static long? TileTargetBytes { get; set; }

    /// <summary>Longest a search lets another scan that is blocked on permits go first before it re-takes them (keeps a breakdown/count from starving behind it, and the search from starving behind a stream of waiters).</summary>
    internal static TimeSpan WaiterGrace { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Shown while the search waits for a scan thread (a breakdown or another scan holds every permit).</summary>
    public const string WaitingForThreadNote = "Waiting for a free scan thread.";

    // Independent of JobQueue's channel and of JobEndpoints.UploadGate: one search job at a time,
    // globally (not per-db) - the simplest IO-contention story, and the one real server-side lever
    // against a sustained full-db scan alongside the disk reserve and minimum query length.
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>Non-blocking: true if this request now owns the single search slot.</summary>
    public static bool TryEnter() => Gate.Wait(0);
    public static void Exit() => Gate.Release();

    /// <summary>
    /// Runs the scan to completion, cancellation or failure, then releases the single-search gate and
    /// marks the job finished in the registry. SearchRunner owns the job end-to-end - it is never
    /// enqueued on <see cref="JobQueue"/>, so nothing else will ever clean it up. Synchronous on purpose:
    /// the caller runs it on a dedicated (LongRunning) thread, because a scan can last hours and must not
    /// occupy a thread-pool thread. Results go to <c>job.Spool</c> (disk); cancelled and failed jobs delete
    /// it, completed ones keep it for review until the next search or the spool's TTL.
    /// </summary>
    public static void Execute(Job job, SearchSpec spec, IDbManager dbs, IDiskSpace disk, string spoolDir,
        JobRegistry registry, ILogger log, CancellationToken ct, ISearchProgressHook? hook = null)
    {
        try
        {
            Scan(job, spec, dbs, disk, spoolDir, ct, hook ?? new NoopSearchProgressHook(), log);
            job.Finish(ct.IsCancellationRequested ? JobStatus.Cancelled : JobStatus.Completed);
        }
        catch (OperationCanceledException) { job.Finish(JobStatus.Cancelled); }
        catch (Exception ex)
        {
            // Full detail to the log only; the job carries a fixed message (no paths, no data) -
            // same posture as JobQueue's import/compact failure handling.
            log.LogError(ex, "Search job {Id} ({Db}) failed", job.Id, job.DbName);
            job.Finish(JobStatus.Failed, ex switch
            {
                DbNotFoundException => "Database not found.",
                DbBusyException => "Database is busy.",
                BusyTimeoutException => "The database stayed busy for too long; the search was stopped.",
                _ => "The search failed. See the server log.",
            });
        }
        finally
        {
            try
            {
                var spool = job.Spool;
                if (spool is not null)
                {
                    if (job.Status == JobStatus.Completed) spool.CompleteWriting(DateTimeOffset.UtcNow);
                    else spool.Dispose();   // cancelled / failed: discard the results
                }
            }
            catch (Exception ex) { log.LogWarning(ex, "Search job {Id} spool cleanup failed", job.Id); }
            job.Cts.Dispose();
            registry.MarkFinished(job);
            Exit();
        }
    }

    private sealed class BusyTimeoutException : Exception;

    private static void Scan(Job job, SearchSpec spec, IDbManager dbs, IDiskSpace disk, string spoolDir,
        CancellationToken ct, ISearchProgressHook hook, ILogger log)
    {
        var spool = job.Spool ?? throw new InvalidOperationException("Search job has no result spool.");
        var (min, max) = SearchMatcher.EffectiveRange(spec);
        var isMatch = SearchMatcher.Compile(spec);
        var fast = SearchMatcher.CompileUtf8(spec);   // case-sensitive contains only; null = string matcher
        var limiter = dbs.ScanLimiter;
        List<WordlistDatabase.Tile>? tiles = null;   // planned once, on the first lease; tiles hold for any data, so they stay valid across segments
        OrderedTileBuffers? buf = null;
        DateTime? busySince = null;
        var batch = new List<string>();

        // Drains the ordered prefix into the spool (this thread is the ONLY spool writer). False = low disk: the scan stops,
        // keeping what is already written (the drained batch is dropped, so the spool is still an exact ordered prefix).
        bool Flush()
        {
            while (true)
            {
                batch.Clear();
                if (buf!.Drain(batch) == 0) return true;
                if (disk.FreeBytes(spoolDir) is { } free && free < SearchSpool.ReserveBytes)
                {
                    // The DB shares this volume and compaction needs headroom: keep what we have and stop.
                    job.SetScanned(buf.Scanned);
                    job.SetMatched(spool.Count, capped: true);
                    job.Note = "Stopped: low disk space; " + spool.Count.ToString("N0", System.Globalization.CultureInfo.InvariantCulture) + " matches kept.";
                    return false;
                }
                spool.Append(batch);
                job.SetMatched(spool.Count, capped: false);
            }
        }

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            // Permits first, then the lease; both go back at every yield (a 30 min Busy back-off must never hold scan threads).
            int held = AcquirePermits(job, limiter, ct);
            DbLease? lease = null;
            bool busy = false, lowDisk = false, exhausted = false;
            Exception? fault = null;
            int workers = 0;
            long segStartKeys = 0, segStartMatches = spool.Count, scanMs = 0;
            string reason = "end";
            try
            {
                try { lease = dbs.AcquireRead(job.DbName); }
                catch (DbBusyException) { busy = true; }
                if (!busy)
                {
                    if (busySince is not null) { busySince = null; job.Note = null; }
                    // Parallel only while the L0 count allows it (every iterator holds every L0 file's index); else one worker, same code.
                    bool parallel = lease!.Db.ParallelScanAllowed;
                    if (tiles is null)
                    {
                        tiles = lease.Db.PlanSearchTiles(min, max, parallel ? limiter.Cap : 1, ct, TileTargetBytes);
                        buf = new OrderedTileBuffers(tiles.Count, BufferMatches, BufferChars);
                    }
                    workers = Math.Max(1, Math.Min(parallel ? held : 1, tiles.Count));
                    if (held > workers) { limiter.Release(held - workers); held = workers; }
                    buf!.SetWindow(2 * workers);
                    segStartKeys = buf.Scanned;

                    // One lease segment: up to `workers` threads, each holding ONE iterator, end on demand (writer waiting, shutdown,
                    // cancel, MaxLeaseAge, low disk). Every worker is joined BEFORE the lease is released (the iterators pin the handle).
                    using var segCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    using var wake = segCts.Token.Register(buf.Wake);
                    var db = lease.Db;
                    var token = segCts.Token;
                    var running = new CountdownEvent(1);
                    var threads = new List<Thread>();
                    var scanSw = Stopwatch.StartNew();
                    try
                    {
                        for (int i = 0; i < workers; i++)
                        {
                            running.AddCount();
                            var t = new Thread(() =>
                            {
                                try { RunWorker(db, min, max, tiles, buf, isMatch, fast, token); }
                                catch (OperationCanceledException) when (token.IsCancellationRequested) { }
                                catch (Exception e) { buf.Fail(e); try { segCts.Cancel(); } catch (ObjectDisposedException) { } }
                                finally { running.Signal(); }
                            }) { IsBackground = true, Name = "hoarderdb-search" };
                            try { t.Start(); threads.Add(t); }
                            catch { running.Signal(); throw; }
                        }
                        running.Signal();   // the initial count: workers hold the rest

                        long start = Stopwatch.GetTimestamp(), lastPoll = start, lastPublish = start;
                        while (true)
                        {
                            buf.WaitActivity(20);
                            if (ct.IsCancellationRequested || buf.Fault != null) break;
                            bool workersGone = running.CurrentCount == 0;
                            if (!Flush()) { lowDisk = true; break; }
                            long now = Stopwatch.GetTimestamp();
                            if (now - lastPublish >= PublishTicks) { lastPublish = now; job.SetScanned(buf.Scanned); }
                            if (buf.AllDrained) { exhausted = true; break; }
                            if (workersGone) break;
                            bool yield = buf.Scanned > segStartKeys && Stopwatch.GetElapsedTime(start, now) >= MaxLeaseAge;
                            if (!yield && now - lastPoll >= PollTicks)
                            {
                                lastPoll = now;
                                yield = lease.Closing || lease.WriterWaiting;
                            }
                            if (yield) { reason = "yield"; break; }
                        }
                    }
                    finally
                    {
                        try { segCts.Cancel(); } catch (ObjectDisposedException) { }
                        foreach (var t in threads) t.Join();   // BEFORE the lease (and so the handle) can go away
                        running.Dispose();
                        buf.ReleaseRunning();
                        scanMs = scanSw.ElapsedMilliseconds;
                    }
                    fault = buf.Fault;
                }
            }
            finally
            {
                lease?.Dispose();
                limiter.Release(held);
            }

            if (busy)
            {
                // A write window started on this db meanwhile: back off and retry from the same
                // cursors - symmetric with how an ordinary read already treats a busy db, just retried
                // instead of surfaced to a client. While waiting, this holds the one server-wide search
                // slot, so give up after BusyTimeout rather than lock searches on other dbs out forever.
                busySince ??= DateTime.UtcNow;
                if (DateTime.UtcNow - busySince > BusyTimeout) throw new BusyTimeoutException();
                job.Note = "Paused: database is busy (import or compact in progress).";
                ct.WaitHandle.WaitOne(BusyRetry);
                continue;
            }

            if (fault is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(fault).Throw();
            ct.ThrowIfCancellationRequested();
            if (!lowDisk && !Flush()) lowDisk = true;   // remaining spool IO happens after the lease is released, so a write window never waits on the disk
            exhausted = exhausted || buf!.AllDrained;

            // Numbers only: never keys, matches or query text.
            log.LogInformation("Search segment: {Workers} workers, {Done} of {Tiles} tiles done, {Keys} keys, scan {ScanMs} ms, {Matches} matches, {Reason}",
                workers, buf!.DoneTiles, buf.TileCount, buf.Scanned - segStartKeys, scanMs, spool.Count - segStartMatches,
                lowDisk ? "low-disk" : exhausted ? "end" : reason);
            job.SetScanned(buf.Scanned);
            if (lowDisk) return;
            hook.OnChunk(job.DbName, buf.Scanned);   // test seam: fires only after the lease is released

            if (exhausted) return;                    // every tile done and drained: true end of the key range
        }
    }

    // Takes every free scan permit (at least one) before the lease. While none is free (a breakdown or another scan holds
    // them all) it waits with a visible, cancellable note, so the process-wide ScanThreads cap is never exceeded.
    private static int AcquirePermits(Job job, ScanLimiter limiter, CancellationToken ct)
    {
        bool noted = false;
        try
        {
            // Fairness: a scan already blocked on permits goes first (bounded wait), so a queued breakdown/count is not starved by a search that re-takes every permit after each yield.
            var grace = Stopwatch.StartNew();
            while (limiter.Waiting > 0 && grace.Elapsed < WaiterGrace)
            {
                ct.ThrowIfCancellationRequested();
                if (!noted) { job.Note = WaitingForThreadNote; noted = true; }
                ct.WaitHandle.WaitOne(100);
            }
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                int got = limiter.TryAcquireUpTo(limiter.Cap);
                if (got > 0) return got;
                if (!noted) { job.Note = WaitingForThreadNote; noted = true; }
                ct.WaitHandle.WaitOne(100);
            }
        }
        finally { if (noted && job.Note == WaitingForThreadNote) job.Note = null; }
    }

    // One worker: ONE iterator for its whole life, reseeked per tile (or per resume cursor). Matches, the raw cursor and the key
    // count go to the ordered buffers together, once per stride; the worker never touches the spool or the filesystem.
    private static void RunWorker(WordlistDatabase db, int? min, int? max, IReadOnlyList<WordlistDatabase.Tile> tiles,
        OrderedTileBuffers buf, Func<string, bool> isMatch, SearchMatcher.Utf8Match? fast, CancellationToken token)
    {
        using var cur = db.OpenScan(min, max);
        var local = new List<string>();
        while (true)
        {
            int idx = buf.Claim(token, out var resume);
            if (idx < 0) return;
            var upper = tiles[idx].Upper;
            if (resume is null) cur.SeekToLower(tiles[idx].Lower); else cur.SeekTo(resume);
            local.Clear();
            long keys = 0, chars = 0;
            while (true)
            {
                bool done = !cur.MoveNext() || (upper is not null && cur.Key.SequenceCompareTo(upper) >= 0);
                if (!done)
                {
                    keys++;
                    if (fast is not null)
                    {
                        // Match on the raw UTF-8 after the length prefix; decode only hits (same lossy decode as the string path).
                        if (fast(cur.Key.Slice(4))) { var hit = cur.Word; local.Add(hit); chars += hit.Length; }
                    }
                    else
                    {
                        var word = cur.Word;
                        if (isMatch(word)) { local.Add(word); chars += word.Length; }
                    }
                    if (keys < StrideKeys) continue;
                }
                var last = done ? null : cur.KeyCopy();   // exact resume point, taken while the iterator is still on it
                StrideHook?.Invoke(buf.Scanned);
                token.ThrowIfCancellationRequested();
                buf.Commit(idx, local, chars, last, keys, done, token);
                local.Clear();
                keys = 0; chars = 0;
                if (done) break;
            }
        }
    }
}

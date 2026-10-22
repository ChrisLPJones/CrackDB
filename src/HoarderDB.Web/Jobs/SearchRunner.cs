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
/// spin-wait). Instead it holds ONE iterator for a lease segment and yields on demand: the segment ends when a
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

    // Keys between cancel / age / writer-waiting checks. ~1 ms of scanning at the measured count rate.
    private const int StrideKeys = 256;
    // How often the lease is polled for WriterWaiting / Closing (takes the entry gate, so not per stride).
    private static readonly long PollTicks = Stopwatch.Frequency / 50;   // ~20 ms
    // Matches buffered before they are appended to the spool while the lease is still held (memory bound for dense queries).
    private const int FlushMatches = 64 * 1024;
    private const long FlushChars = 8L * 1024 * 1024;
    // Progress visibility: matches and counts are published at least this often during a long segment.
    private static readonly long PublishTicks = Stopwatch.Frequency * 2;

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
        long scanned = 0;
        byte[]? cursor = null;   // raw key of the last scanned key; null = start of the range
        DateTime? busySince = null;
        var pending = new List<string>();
        long pendingChars = 0;

        // Appends the buffered matches (key order) to the spool. False = low disk: the scan stops, keeping what is already written.
        bool Flush()
        {
            if (pending.Count == 0) return true;
            if (disk.FreeBytes(spoolDir) is { } free && free < SearchSpool.ReserveBytes)
            {
                // The DB shares this volume and compaction needs headroom: keep what we have and stop.
                job.SetScanned(scanned);
                job.SetMatched(spool.Count, capped: true);
                job.Note = "Stopped: low disk space; " + spool.Count.ToString("N0", System.Globalization.CultureInfo.InvariantCulture) + " matches kept.";
                return false;
            }
            spool.Append(pending);
            pending.Clear();
            pendingChars = 0;
            job.SetMatched(spool.Count, capped: false);
            return true;
        }

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            DbLease lease;
            try { lease = dbs.AcquireRead(job.DbName); }
            catch (DbBusyException)
            {
                // A write window started on this db meanwhile: back off and retry from the same
                // cursor - symmetric with how an ordinary read already treats a busy db, just retried
                // instead of surfaced to a client. While waiting, this holds the one server-wide search
                // slot, so give up after BusyTimeout rather than lock searches on other dbs out forever.
                busySince ??= DateTime.UtcNow;
                if (DateTime.UtcNow - busySince > BusyTimeout) throw new BusyTimeoutException();
                job.Note = "Paused: database is busy (import or compact in progress).";
                ct.WaitHandle.WaitOne(BusyRetry);
                continue;
            }
            if (busySince is not null) { busySince = null; job.Note = null; }

            // One lease segment: ONE iterator held until the segment ends on demand (writer waiting, shutdown, cancel,
            // MaxLeaseAge). The cursor is disposed BEFORE the lease is released (the iterator pins the handle's files).
            bool exhausted = true;
            bool lowDisk = false;
            long segKeys = 0, segMatches = 0, seekMs = 0, scanMs = 0;
            try
            {
                using var cur = lease.Db.OpenScan(min, max);
                var seekSw = Stopwatch.StartNew();
                cur.SeekTo(cursor);
                seekMs = seekSw.ElapsedMilliseconds;
                var scanSw = Stopwatch.StartNew();
                long start = Stopwatch.GetTimestamp(), lastPoll = start, lastPublish = start;
                int stride = 0;
                while (cur.MoveNext())
                {
                    var word = cur.Word;
                    scanned++;
                    segKeys++;
                    if (isMatch(word)) { pending.Add(word); pendingChars += word.Length; segMatches++; }
                    if (++stride < StrideKeys) continue;
                    stride = 0;
                    StrideHook?.Invoke(scanned);
                    ct.ThrowIfCancellationRequested();
                    long now = Stopwatch.GetTimestamp();
                    bool yield = Stopwatch.GetElapsedTime(start, now) >= MaxLeaseAge;
                    if (!yield && now - lastPoll >= PollTicks)
                    {
                        lastPoll = now;
                        yield = lease.Closing || lease.WriterWaiting;
                    }
                    if (pending.Count >= FlushMatches || pendingChars >= FlushChars
                        || (pending.Count > 0 && now - lastPublish >= PublishTicks))
                    {
                        lastPublish = now;
                        job.SetScanned(scanned);
                        if (!Flush()) { lowDisk = true; break; }
                    }
                    else if (now - lastPublish >= PublishTicks) { lastPublish = now; job.SetScanned(scanned); }
                    if (yield)
                    {
                        cursor = cur.KeyCopy();   // exact resume point (raw bytes), taken while the iterator is still on it
                        exhausted = false;
                        break;
                    }
                }
                scanMs = scanSw.ElapsedMilliseconds;
            }
            finally { lease.Dispose(); }

            // Numbers only: never keys, matches or query text.
            log.LogInformation("Search segment: seek {SeekMs} ms, {Keys} keys, scan {ScanMs} ms, {Matches} matches, {Reason}",
                seekMs, segKeys, scanMs, segMatches, lowDisk ? "low-disk" : exhausted ? "end" : "yield");
            if (lowDisk) return;

            // Remaining spool IO happens after the lease is released, so a write window never waits on the disk.
            ct.ThrowIfCancellationRequested();
            job.SetScanned(scanned);
            if (!Flush()) return;
            hook.OnChunk(job.DbName, scanned);   // test seam: fires only after the lease is released

            if (exhausted) return;                     // the cursor ran out: true end of the key range
        }
    }
}

using System.Diagnostics;
using HoarderDB.Web.Storage;

namespace HoarderDB.Web.Jobs;

/// <summary>Test seam, called after every chunk's lease is released. Default is a no-op.</summary>
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
/// spin-wait). Instead it runs in small chunks against AcquireRead, releasing the lease and
/// re-acquiring it between chunks with a resume cursor, so it only ever contends with a write window
/// on its own database - exactly like any ordinary read today - and never touches the import/compact
/// queue, and never blocks a search or job on a different database.
/// </summary>
public static class SearchRunner
{
    /// <summary>Below this many characters a scanning search is refused outright (enforced server-side).</summary>
    public const int MinQueryLength = SearchMatcher.MinChars;

    /// <summary>A write window (import/compact) on the searched db holds the single search slot while we wait; give up after this.</summary>
    public static TimeSpan BusyTimeout { get; set; } = TimeSpan.FromMinutes(30);
    public static TimeSpan BusyRetry { get; set; } = TimeSpan.FromMilliseconds(250);

    // A few thousand keys or ~200ms, whichever comes first, per AcquireRead lease: frequent enough
    // that a pending write window on this job's own db waits at most about one chunk. Conservative by
    // design (mirrors the "pick a conservative default and comment why" approach used for the import
    // memory budget) - tune down further if a write window is ever observed to wait longer than this
    // in practice.
    private const int ChunkKeys = 4000;
    private static readonly TimeSpan ChunkBudget = TimeSpan.FromMilliseconds(200);

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
            Scan(job, spec, dbs, disk, spoolDir, ct, hook ?? new NoopSearchProgressHook());
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
        CancellationToken ct, ISearchProgressHook hook)
    {
        var spool = job.Spool ?? throw new InvalidOperationException("Search job has no result spool.");
        var (min, max) = SearchMatcher.EffectiveRange(spec);
        var isMatch = SearchMatcher.Compile(spec);
        long scanned = 0;
        string? cursor = null;
        DateTime? busySince = null;

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

            var chunkMatches = new List<string>();
            bool brokeEarly = false;
            var sw = Stopwatch.StartNew();
            try
            {
                int inChunk = 0;
                foreach (var word in lease.Db.Scan(min, max, cursor))
                {
                    cursor = word;
                    scanned++;
                    if (isMatch(word)) chunkMatches.Add(word);
                    inChunk++;
                    if (inChunk >= ChunkKeys || sw.Elapsed >= ChunkBudget)
                    {
                        brokeEarly = true;
                        break;
                    }
                    if ((inChunk & 0xFF) == 0) ct.ThrowIfCancellationRequested();
                }
            }
            finally { lease.Dispose(); }

            // Spool IO happens after the lease is released, so a write window never waits on the disk.
            ct.ThrowIfCancellationRequested();
            if (disk.FreeBytes(spoolDir) is { } free && free < SearchSpool.ReserveBytes)
            {
                // The DB shares this volume and compaction needs headroom: keep what we have and stop.
                job.SetScanned(scanned);
                job.SetMatched(spool.Count, capped: true);
                job.Note = "Stopped: low disk space; " + spool.Count.ToString("N0", System.Globalization.CultureInfo.InvariantCulture) + " matches kept.";
                return;
            }
            spool.Append(chunkMatches);
            job.SetScanned(scanned);
            job.SetMatched(spool.Count, capped: false);
            hook.OnChunk(job.DbName, scanned);   // test seam: fires only after the lease is released

            if (!brokeEarly) return;                   // the iterator ran out: true end of the key range
        }
    }
}

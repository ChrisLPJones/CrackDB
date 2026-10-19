using System.Diagnostics;
using CrackDB.Web.Storage;

namespace CrackDB.Web.Jobs;

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
/// Executes a "contains" substring search as its own lightweight scanning path - deliberately never
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
    /// <summary>Shown by default; see <see cref="MaxResultCap"/> for the hard ceiling.</summary>
    public const int DefaultResultCap = 500;
    /// <summary>Hard ceiling, not user-raisable past this: bounds worst-case scan + SSE-frame cost.</summary>
    public const int MaxResultCap = 2000;
    /// <summary>Below this many characters a substring scan is refused outright (enforced server-side).</summary>
    public const int MinQueryLength = 4;

    // A few thousand keys or ~200ms, whichever comes first, per AcquireRead lease: frequent enough
    // that a pending write window on this job's own db waits at most about one chunk. Conservative by
    // design (mirrors the "pick a conservative default and comment why" approach used for the import
    // memory budget) - tune down further if a write window is ever observed to wait longer than this
    // in practice.
    private const int ChunkKeys = 4000;
    private static readonly TimeSpan ChunkBudget = TimeSpan.FromMilliseconds(200);

    // Independent of JobQueue's channel and of JobEndpoints.UploadGate: one search job at a time,
    // globally (not per-db) - the simplest IO-contention story, and the one real server-side lever
    // against a sustained full-db scan alongside the result cap and minimum query length.
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>Non-blocking: true if this request now owns the single search slot.</summary>
    public static bool TryEnter() => Gate.Wait(0);
    public static void Exit() => Gate.Release();

    /// <summary>
    /// Runs the scan to completion, cancellation or failure, then releases the single-search gate and
    /// marks the job finished in the registry. SearchRunner owns the job end-to-end - it is never
    /// enqueued on <see cref="JobQueue"/>, so nothing else will ever clean it up.
    /// </summary>
    public static async Task ExecuteAsync(Job job, string query, bool ignoreCase, int resultCap,
        IDbManager dbs, JobRegistry registry, ILogger log, CancellationToken ct, ISearchProgressHook? hook = null)
    {
        try
        {
            await ScanAsync(job, query, ignoreCase, resultCap, dbs, ct, hook ?? new NoopSearchProgressHook());
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
                _ => "The search failed. See the server log.",
            });
        }
        finally
        {
            job.Cts.Dispose();
            registry.MarkFinished(job);
            Exit();
        }
    }

    private static async Task ScanAsync(Job job, string query, bool ignoreCase, int resultCap, IDbManager dbs,
        CancellationToken ct, ISearchProgressHook hook)
    {
        var matches = new List<string>();
        long scanned = 0;
        string? cursor = null;
        var cmp = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            DbLease lease;
            try { lease = dbs.AcquireRead(job.DbName); }
            catch (DbBusyException)
            {
                // A write window started on this db meanwhile: back off and retry from the same
                // cursor - symmetric with how an ordinary read already treats a busy db, just retried
                // instead of surfaced to a client.
                await Task.Delay(250, ct);
                continue;
            }

            bool brokeEarly = false;
            var sw = Stopwatch.StartNew();
            try
            {
                int inChunk = 0;
                foreach (var word in lease.Db.Scan(job.Min, job.Max, cursor))
                {
                    cursor = word;
                    scanned++;
                    if (matches.Count < resultCap && word.Contains(query, cmp)) matches.Add(word);
                    inChunk++;
                    if (matches.Count >= resultCap || inChunk >= ChunkKeys || sw.Elapsed >= ChunkBudget)
                    {
                        brokeEarly = true;
                        break;
                    }
                    if ((inChunk & 0xFF) == 0) ct.ThrowIfCancellationRequested();
                }
            }
            finally { lease.Dispose(); }

            job.SetScanned(scanned);
            job.SetMatches(new List<string>(matches), matches.Count, matches.Count >= resultCap);
            hook.OnChunk(job.DbName, scanned);   // test seam: fires only after the lease is released

            if (matches.Count >= resultCap) return;   // cap reached: stop scanning (bounds worst-case cost)
            if (!brokeEarly) return;                   // the iterator ran out: true end of the key range
        }
    }
}

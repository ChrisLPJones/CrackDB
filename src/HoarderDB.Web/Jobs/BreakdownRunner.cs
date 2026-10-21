using HoarderDB;
using HoarderDB.Web.Storage;

namespace HoarderDB.Web.Jobs;

/// <summary>Test seam, called on the scan thread at the start of a breakdown scan (scanned = 0) and at every progress tick. Default is a no-op.</summary>
public interface IBreakdownProgressHook
{
    void OnProgress(string dbName, long scannedCount);
}

public sealed class NoopBreakdownProgressHook : IBreakdownProgressHook
{
    public void OnProgress(string dbName, long scannedCount) { }
}

/// <summary>
/// Full length-breakdown scan, run ON the serial job queue. It holds one <see cref="IDbManager.AcquireRead"/>
/// lease for the whole scan: because the queue is serial, no import/compact write window can start on the
/// db meanwhile and the lease never sees a 409. It deliberately does NOT use WithWriteAsync (that would
/// reopen the handle and 409 every read/search for the scan's duration). The result is saved only after a
/// complete scan; cancellation throws, so a cancelled scan never overwrites the previous result.
/// </summary>
public static class BreakdownRunner
{
    public static Task RunAsync(Job job, IDbManager dbs, ILengthBreakdownStore store, IBreakdownProgressHook hook, CancellationToken ct) =>
        // LongRunning: a scan can last an hour and must not occupy a thread-pool thread.
        Task.Factory.StartNew(() => Run(job, dbs, store, hook, ct), CancellationToken.None,
            TaskCreationOptions.LongRunning, TaskScheduler.Default);

    private static void Run(Job job, IDbManager dbs, ILengthBreakdownStore store, IBreakdownProgressHook hook, CancellationToken ct)
    {
        var lengths = new List<LengthCount>();
        using (var lease = dbs.AcquireRead(job.DbName))      // DbBusyException => job fails "Database is busy."
        {
            // Memory guard: the scan holds an iterator over every L0 file (multi-GB with many files, for hours).
            // Fails closed if the count is unreadable. Nothing has been scanned or saved, so any previous result stays.
            if (lease.Db.NumFilesAtLevel0ForGate() > MemoryDiagnostics.L0WarnFiles)
                throw new BreakdownNeedsCompactionException("Too many uncompacted L0 files.");
            hook.OnProgress(job.DbName, 0);
            foreach (var (length, count) in lease.Db.LengthDistribution(ct, n =>
                     {
                         job.SetScanned(n);
                         hook.OnProgress(job.DbName, n);
                     }))
                lengths.Add(new LengthCount(length, count));
        }
        ct.ThrowIfCancellationRequested();                    // cancelled after the last tick: do not save
        store.Save(job.DbName, DateTimeOffset.UtcNow, lengths);
    }
}

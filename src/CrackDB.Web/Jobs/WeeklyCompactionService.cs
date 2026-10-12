using CrackDB.Web.Storage;

namespace CrackDB.Web.Jobs;

/// <summary>
/// Enqueues a compaction of every database each Sunday 00:00 UTC through the normal job queue (one
/// job at a time, write window per database). No catch-up: if the server was down at the scheduled
/// time, the next run is simply the next future Sunday. Time-driven only; there is no endpoint.
/// </summary>
public sealed class WeeklyCompactionService(IDbManager dbs, JobQueue queue, ILogger<WeeklyCompactionService> log)
    : BackgroundService
{
    /// <summary>The next Sunday 00:00:00 UTC strictly after <paramref name="nowUtc"/>.</summary>
    public static DateTimeOffset NextRun(DateTimeOffset nowUtc)
    {
        var utc = nowUtc.UtcDateTime;
        var days = ((int)DayOfWeek.Sunday - (int)utc.DayOfWeek + 7) % 7;
        var candidate = new DateTimeOffset(DateTime.SpecifyKind(utc.Date, DateTimeKind.Utc)).AddDays(days);
        return candidate > nowUtc ? candidate : candidate.AddDays(7);
    }

    public static int EnqueueAll(IDbManager dbs, JobQueue queue, ILogger log)
    {
        var names = dbs.ListDatabases();
        foreach (var name in names)
        {
            try { queue.Enqueue(new Job(JobKind.Compact, name, null)); }
            catch (Exception e) { log.LogWarning(e, "Could not queue weekly compaction for a database."); }
        }
        log.LogInformation("Weekly compaction: queued {Count} database(s).", names.Count);
        return names.Count;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var now = DateTimeOffset.UtcNow;
                var next = NextRun(now);
                log.LogInformation("Weekly compaction scheduled for {Next:u}.", next);
                await Task.Delay(next - now, stoppingToken);
                try { EnqueueAll(dbs, queue, log); }
                catch (Exception e) { log.LogError(e, "Weekly compaction run failed to queue."); }
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
    }
}

using HoarderDB.Web.Options;
using HoarderDB.Web.Storage;
using Microsoft.Extensions.Options;

namespace HoarderDB.Web.Jobs;

/// <summary>
/// Enqueues a compaction of every database each Sunday 00:00 in the configured time zone
/// (<see cref="CompactionOptions"/>, default Australia/Sydney) through the normal job queue (one
/// job at a time, write window per database). No catch-up: if the server was down at the scheduled
/// time, the next run is simply the next future Sunday. Time-driven only; there is no endpoint.
/// </summary>
public sealed class WeeklyCompactionService(
    IDbManager dbs, JobQueue queue, IOptions<CompactionOptions> options, ILogger<WeeklyCompactionService> log)
    : BackgroundService
{
    private static readonly TimeSpan GapStep = TimeSpan.FromMinutes(15);

    /// <summary>The next Sunday 00:00:00 UTC strictly after <paramref name="nowUtc"/>.</summary>
    public static DateTimeOffset NextRun(DateTimeOffset nowUtc) => NextRun(nowUtc, TimeZoneInfo.Utc);

    /// <summary>
    /// The next Sunday 00:00 local time in <paramref name="tz"/> strictly after <paramref name="now"/>.
    /// A local midnight that does not exist (DST gap) moves forward to the first valid time that day;
    /// an ambiguous one (DST overlap) uses its first occurrence. Result is returned in UTC.
    /// </summary>
    public static DateTimeOffset NextRun(DateTimeOffset now, TimeZoneInfo tz)
    {
        var localNow = TimeZoneInfo.ConvertTime(now, tz).DateTime;
        var days = ((int)DayOfWeek.Sunday - (int)localNow.DayOfWeek + 7) % 7;
        var sunday = DateTime.SpecifyKind(localNow.Date.AddDays(days), DateTimeKind.Unspecified);
        for (var i = 0; i < 3; i++)   // at most one extra week is ever needed; the bound is only a guard
        {
            var run = ToUtc(sunday.AddDays(7 * i), tz);
            if (run > now) return run;
        }
        throw new InvalidOperationException("Could not compute the next compaction time.");
    }

    private static DateTimeOffset ToUtc(DateTime local, TimeZoneInfo tz)
    {
        while (tz.IsInvalidTime(local)) local = local.Add(GapStep);   // spring-forward gap
        var offset = tz.IsAmbiguousTime(local)
            ? tz.GetAmbiguousTimeOffsets(local).Max()                 // larger offset = earlier instant
            : tz.GetUtcOffset(local);
        return new DateTimeOffset(local, offset).ToUniversalTime();
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
            var tz = CompactionOptionsValidator.TryResolve(options.Value.TimeZone, out var error)
                ?? throw new InvalidOperationException(error);   // unreachable: validated at startup
            while (!stoppingToken.IsCancellationRequested)
            {
                var now = DateTimeOffset.UtcNow;
                var next = NextRun(now, tz);
                log.LogInformation("Weekly compaction scheduled for {Local:yyyy-MM-dd HH:mm} {Zone} ({Next:u} UTC).",
                    TimeZoneInfo.ConvertTime(next, tz), tz.Id, next);
                await Task.Delay(next - now, stoppingToken);
                try { EnqueueAll(dbs, queue, log); }
                catch (Exception e) { log.LogError(e, "Weekly compaction run failed to queue."); }
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
    }
}

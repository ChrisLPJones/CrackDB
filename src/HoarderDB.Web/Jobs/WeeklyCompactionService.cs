using HoarderDB.Web.Options;
using HoarderDB.Web.Storage;
using Microsoft.Extensions.Options;

namespace HoarderDB.Web.Jobs;

/// <summary>
/// Enqueues a compaction of every database on the configured weekly schedule (day, hour, minute in
/// the <see cref="CompactionOptions"/> time zone, default Sunday 00:00 Australia/Sydney; the schedule
/// itself is edited on the Settings page and read from <see cref="CompactionSchedule"/>) through the
/// normal job queue (one job at a time, write window per database). No catch-up: if the server was
/// down at the scheduled time, the next run is simply the next future occurrence.
/// </summary>
public sealed class WeeklyCompactionService(
    IDbManager dbs, JobQueue queue, JobRegistry registry, CompactionSchedule schedule,
    IOptions<CompactionOptions> options, ILogger<WeeklyCompactionService> log, TimeProvider? time = null)
    : BackgroundService
{
    private static readonly TimeSpan GapStep = TimeSpan.FromMinutes(15);
    /// <summary>Longest sleep before the clock is re-read (survives clock jumps).</summary>
    private static readonly TimeSpan MaxWait = TimeSpan.FromHours(1);
    /// <summary>Two scheduled runs are never started closer together than this (DST overlap, rapid edits).</summary>
    private static readonly TimeSpan MinGap = TimeSpan.FromHours(1);
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>The next Sunday 00:00:00 UTC strictly after <paramref name="nowUtc"/>.</summary>
    public static DateTimeOffset NextRun(DateTimeOffset nowUtc) => NextRun(nowUtc, TimeZoneInfo.Utc);

    /// <summary>The next Sunday 00:00 local time in <paramref name="tz"/> strictly after <paramref name="now"/>.</summary>
    public static DateTimeOffset NextRun(DateTimeOffset now, TimeZoneInfo tz) => NextRun(now, tz, DayOfWeek.Sunday, 0, 0);

    /// <summary>
    /// The next <paramref name="day"/> at hour:minute local time in <paramref name="tz"/> strictly after
    /// <paramref name="now"/>. A local time that does not exist (DST gap) moves forward in 15-minute steps
    /// to the first valid time; an ambiguous one (DST overlap) uses its first occurrence. Result is in UTC.
    /// </summary>
    public static DateTimeOffset NextRun(DateTimeOffset now, TimeZoneInfo tz, DayOfWeek day, int hour, int minute)
    {
        var localNow = TimeZoneInfo.ConvertTime(now, tz).DateTime;
        var days = ((int)day - (int)localNow.DayOfWeek + 7) % 7;
        var first = DateTime.SpecifyKind(localNow.Date.AddDays(days).AddHours(hour).AddMinutes(minute), DateTimeKind.Unspecified);
        for (var i = 0; i < 3; i++)   // at most one extra week is ever needed; the bound is only a guard
        {
            var run = ToUtc(first.AddDays(7 * i), tz);
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

    /// <summary>
    /// Queues a compact job per database. With a <paramref name="registry"/>, a database that already has
    /// a queued or running compact job is skipped (no stacking); <paramref name="runId"/> tags the jobs of
    /// one scheduled run so the history can group them.
    /// </summary>
    public static int EnqueueAll(IDbManager dbs, JobQueue queue, ILogger log, JobRegistry? registry = null, string? runId = null)
    {
        var names = dbs.ListDatabases();
        var busy = registry?.List()
            .Where(j => j.Kind == "compact" && j.Status is "queued" or "running")
            .Select(j => j.Db).ToHashSet(StringComparer.Ordinal) ?? [];
        var queued = 0;
        foreach (var name in names)
        {
            if (busy.Contains(name)) continue;
            try { queue.Enqueue(new Job(JobKind.Compact, name, null) { RunId = runId }); queued++; }
            catch (Exception e) { log.LogWarning(e, "Could not queue weekly compaction for a database."); }
        }
        log.LogInformation("Weekly compaction: queued {Count} database(s).", queued);
        return queued;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var tz = CompactionOptionsValidator.TryResolve(options.Value.TimeZone, out var error)
                ?? throw new InvalidOperationException(error);   // unreachable: validated at startup
            DateTimeOffset? lastFire = null;
            while (!stoppingToken.IsCancellationRequested)
            {
                var changed = schedule.Changed;     // taken BEFORE the snapshot so a save in between is never missed
                var s = schedule.Current;
                if (!s.Enabled)
                {
                    await changed.WaitAsync(stoppingToken);   // truly idle while disabled
                    continue;
                }
                var now = _time.GetUtcNow();
                var next = NextRun(now, tz, (DayOfWeek)s.Day, s.Hour, s.Minute);
                var wait = next - now;
                if (wait > MaxWait) wait = MaxWait;

                using var sleep = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                var delay = Task.Delay(wait, _time, sleep.Token);
                await Task.WhenAny(delay, changed);
                sleep.Cancel();
                try { await delay; } catch (OperationCanceledException) { /* woken early */ }
                stoppingToken.ThrowIfCancellationRequested();
                if (changed.IsCompleted) continue;               // edited: recompute from the new schedule

                now = _time.GetUtcNow();
                if (now < next) continue;                        // hourly wake or early timer: re-read the clock
                if (lastFire is { } lf && now - lf < MinGap)
                {
                    log.LogWarning("Weekly compaction skipped: another scheduled run started less than an hour ago.");
                    continue;
                }
                lastFire = now;
                log.LogInformation("Weekly compaction starting ({Zone}).", tz.Id);
                try { EnqueueAll(dbs, queue, log, registry, Guid.NewGuid().ToString("N")); }
                catch (Exception e) { log.LogError(e, "Weekly compaction run failed to queue."); }
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
    }
}

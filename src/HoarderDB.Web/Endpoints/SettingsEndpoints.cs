using System.Text.Json;
using HoarderDB.Web.Jobs;
using HoarderDB.Web.Options;
using HoarderDB.Web.Storage;
using Microsoft.Extensions.Options;

namespace HoarderDB.Web.Endpoints;

/// <summary>
/// Settings page API. Cookie sessions only (a script API key gets 403), JSON only for writes, rate limited.
/// Responses never carry paths, mount names or file-system types.
/// </summary>
public static class SettingsEndpoints
{
    public const string Limiter = "settings";

    public static void MapSettings(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/settings", (HttpContext ctx, CompactionSchedule schedule, CompactionHistory history, JobRegistry registry,
            IDiskSpace disk, DataRootResolver data, SearchSpool spool, IOptions<CompactionOptions> opts) =>
        {
            if (AccountEndpoints.DenyNonCookie(ctx) is { } denied) return denied;
            var tz = CompactionOptionsValidator.TryResolve(opts.Value.TimeZone, out _)!;

            DiskVolume? dataVol = disk.Volume(data.Root);
            var spoolDir = Directory.Exists(spool.Dir) ? spool.Dir : data.Root;
            DiskVolume? spoolVol = disk.Volume(spoolDir);
            // The spool is shown only when it lives on a different volume than the data.
            var showSpool = spoolVol is not null && (dataVol is null || spoolVol.MountKey != dataVol.MountKey);

            return Results.Ok(new
            {
                compaction = CompactionBody(schedule, tz),
                status = new
                {
                    disk = new
                    {
                        data = dataVol is null ? null : new { freeBytes = dataVol.FreeBytes, totalBytes = dataVol.TotalBytes },
                        spool = showSpool ? new { freeBytes = spoolVol!.FreeBytes, totalBytes = spoolVol.TotalBytes } : null,
                    },
                    compactionRunning = registry.List().Any(j => j.Kind == "compact" && j.Status is "queued" or "running"),
                    lastCompaction = history.LastRun() is { } r ? new
                    {
                        trigger = r.Trigger, startedAt = r.StartedAt, endedAt = r.EndedAt,
                        durationSeconds = r.DurationSeconds, result = r.Result,
                        databases = r.Databases.Select(d => new { name = d.Name, result = d.Result, durationSeconds = d.DurationSeconds }),
                    } : null,
                },
            });
        });

        app.MapPut("/api/settings/compaction", async (HttpContext ctx, CompactionSchedule schedule,
            IOptions<CompactionOptions> opts, ILoggerFactory lf) =>
        {
            if (AccountEndpoints.DenyNonCookie(ctx) is { } denied) return denied;
            if (!ctx.Request.HasJsonContentType())
                return Results.Problem(statusCode: 415, title: "Content-Type must be application/json.");

            CompactionSettings? s;
            try
            {
                using var doc = await JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ctx.RequestAborted);
                if (!CompactionSettings.TryParse(doc.RootElement, out s))
                    return Results.Problem(statusCode: 400, title: "Invalid schedule.");
            }
            catch (JsonException) { return Results.Problem(statusCode: 400, title: "Invalid schedule."); }

            try { schedule.Save(s!); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Memory is unchanged (persist-first); log the type only, never paths or input.
                lf.CreateLogger("HoarderDB.Web.Settings").LogError("Could not save the settings ({Error})", e.GetType().Name);
                return Results.Problem(statusCode: 500, title: "Could not save the settings.");
            }
            return Results.Ok(CompactionBody(schedule, CompactionOptionsValidator.TryResolve(opts.Value.TimeZone, out _)!));
        }).RequireRateLimiting(Limiter);
    }

    private static object CompactionBody(CompactionSchedule schedule, TimeZoneInfo tz)
    {
        var s = schedule.Current;
        var now = DateTimeOffset.UtcNow;
        DateTimeOffset? next = s.Enabled
            ? WeeklyCompactionService.NextRun(now, tz, (DayOfWeek)s.Day, s.Hour, s.Minute) : null;
        var off = tz.GetUtcOffset(now);
        return new
        {
            enabled = s.Enabled, day = s.Day, hour = s.Hour, minute = s.Minute,
            timeZone = tz.Id,
            utcOffset = (off < TimeSpan.Zero ? "-" : "+") + off.ToString(@"hh\:mm"),
            nextRunUtc = next,
            nextRunLocal = next is { } n ? TimeZoneInfo.ConvertTime(n, tz).ToString("yyyy-MM-dd HH:mm") : null,
            isDefault = s == CompactionSettings.Default,
            recovered = schedule.Recovered,
        };
    }
}

using System.Text;
using System.Text.Json;
using HoarderDB.Web.Jobs;
using HoarderDB.Web.Storage;

namespace HoarderDB.Web.Endpoints;

public static class ApiEndpoints
{
    private const int MaxLookupChars = 4096;
    /// <summary>Longest search term accepted (the UI input has the same maxlength).</summary>
    public const int MaxSearchQueryChars = 256;
    /// <summary>Named fixed-window rate-limit policy for the search-start route (same pattern as AuthEndpoints.LoginLimiter).</summary>
    public const string SearchLimiter = "search";
    /// <summary>Rate-limit policy for the pre-upload duplicate check (one or two calls per queued file).</summary>
    public const string SeenLimiter = "importseen";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static void MapApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api").RequireAuthorization();

        // A busy database is expected (a long import/compact holds it), not a server fault: answer with a
        // friendly 409 and no exception, so it is not logged as an unhandled exception with a stack trace.
        api.AddEndpointFilter(async (ctx, next) =>
        {
            try { return await next(ctx); }
            catch (DbBusyException)
            {
                var jobs = ctx.HttpContext.RequestServices.GetRequiredService<JobRegistry>();
                var name = ctx.HttpContext.GetRouteValue("name") as string;
                return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "Database is busy.",
                    detail: BusyMessage.For(jobs, name));
            }
        });

        api.MapGet("/dbs", (IDbManager dbs) => Results.Ok(new { databases = dbs.ListDatabases() }));

        api.MapPost("/dbs", async (CreateDbRequest req, IDbManager dbs, CancellationToken ct) =>
        {
            await dbs.CreateAsync(req.Name!, ct);   // null/invalid names are rejected by the resolver (400)
            return Results.Json(new { name = req.Name }, statusCode: StatusCodes.Status201Created);
        });

        api.MapDelete("/dbs/{name}", async (string name, IDbManager dbs, JobRegistry jobs, CancellationToken ct) =>
        {
            // Layer 1: refuse while a queued or running job targets this db. Layer 2 is DeleteAsync's fail-fast
            // WriteGate. Known residual: a job enqueued between this check and the gate is not seen (a queued
            // import would later re-create the name); not reachable through the single-user UI. Jobs are never cancelled here.
            if (jobs.List().Any(j => j.Db == name && !IsTerminal(j.Status)))
                throw new DbBusyException("Database is busy.");
            await dbs.DeleteAsync(name, ct);
            return Results.NoContent();
        });

        api.MapGet("/dbs/{name}/stats", (string name, bool? full, IDbManager dbs, CancellationToken ct) =>
        {
            using var lease = dbs.AcquireRead(name);
            var isFull = full == true;
            // TODO(P3): move to job system. A full scan holds this request for a long time on a huge DB.
            // Script-only: the UI uses GET /dbs/{name}/length-breakdown (stored) + POST /api/jobs/length-breakdown.
            // This stays a live, slow scan and never writes the stored breakdown (single writer = the job queue).
            var lengths = isFull
                ? lease.Db.LengthDistribution(ct).Select(x => new { length = x.length, count = x.count }).ToList()
                : null;
            return Results.Ok(new { name, estimatedCount = lease.Db.EstimatedCount(), full = isFull, lengths });
        });

        // Stored length breakdown (counts + timestamp only): reads the sidecar, no DbLease, so it works (and never
        // 409s) during an import. 200 with both fields null = never computed. An empty list is a valid result.
        api.MapGet("/dbs/{name}/length-breakdown", (string name, DataRootResolver data, ILengthBreakdownStore store) =>
        {
            data.ResolveExisting(name);                       // 400 bad name / 404 missing
            var b = store.Load(name);
            return Results.Ok(new
            {
                name,
                asOf = b?.AsOf,
                lengths = b?.Lengths.Select(l => new { length = l.Length, count = l.Count }).ToList(),
            });
        });

        api.MapGet("/dbs/{name}/count", (string name, int? min, int? max, int? length, IDbManager dbs, CancellationToken ct) =>
        {
            if (Validate(ref min, ref max, length, null) is { } bad) return bad;
            using var lease = dbs.AcquireRead(name);
            if (min is null && max is null)
                return Results.Ok(new { name, min, max, exact = false, count = lease.Db.EstimatedCount() });
            // TODO(P3): move to job system. Exact count is a full range scan; it can block on a huge DB.
            return Results.Ok(new { name, min, max, exact = true, count = lease.Db.CountRange(min, max, ct) });
        });

        api.MapGet("/dbs/{name}/lookup", (string name, string? word, IDbManager dbs) =>
        {
            if (string.IsNullOrEmpty(word) || word.Length > MaxLookupChars)
                return Problem(400, $"'word' is required and at most {MaxLookupChars} characters.");
            using var lease = dbs.AcquireRead(name);
            return Results.Ok(new { name, word, found = lease.Db.Contains(word) });
        });

        // Scanning search (ignore-case exact and/or contains). Plain exact match is
        // NOT served here: it is one point read, GET /lookup (no job, no rate limit). The scan runs as its own
        // background job (SearchRunner), never through JobQueue (would block unrelated imports/compacts) and
        // never holding one AcquireRead lease for the whole scan (would stall a write window). Matches stream
        // to a server-owned temp file (SearchSpool), so there is no result ceiling. Rate-limited and gated to
        // one search at a time, both server-side. Every field is validated here; the UI is only a convenience.
        api.MapPost("/dbs/{name}/search", (string name, SearchRequest req, IDbManager dbs, JobRegistry registry,
            SearchSpool spool, IDiskSpace disk, ILoggerFactory loggerFactory, ISearchProgressHook searchHook) =>
        {
            var log = loggerFactory.CreateLogger("HoarderDB.Web.Jobs.SearchRunner");
            var query = req.Query ?? "";
            bool ignoreCase = req.IgnoreCase == true, contains = req.Contains == true;
            if (query.Length == 0) return Problem(400, "'query' is required.");
            if (query.Length > MaxSearchQueryChars) return Problem(400, $"'query' must be at most {MaxSearchQueryChars} characters.");
            if (!ignoreCase && !contains) return Problem(400, "Use GET /lookup for exact match.");
            var spec = new SearchSpec(query, ignoreCase, contains);
            if (query.Length < SearchRunner.MinQueryLength)
                return Problem(400, $"'query' must contain at least {SearchRunner.MinQueryLength} characters.");
            var (rangeMin, rangeMax) = SearchMatcher.EffectiveRange(spec);

            using (dbs.AcquireRead(name)) { }   // 400/404/409 database semantics, same as every other read

            if (!SearchRunner.TryEnter())
                return Results.Problem(statusCode: 409, title: "Search is busy.",
                    detail: "Another search is already running. Only one search runs at a time.");
            Job? job = null;
            bool handedOff = false;   // true once SearchRunner.Execute owns the slot (it releases it)
            try
            {
                if (!spool.TryPrepare())
                    return Problem(500, "Search results cannot be stored on this server.");
                if (disk.FreeBytes(spool.Dir) is { } free && free < SearchSpool.ReserveBytes)
                    return Problem(507, "Not enough free disk space for search results.");
                spool.DisposeAll();   // a new search replaces the previous results (only one is ever useful)
                job = new Job(JobKind.Search, name, query) { Min = rangeMin, Max = rangeMax };
                job.Spool = spool.Create(job.Id);
                if (!job.TryStart()) throw new InvalidOperationException();   // unreachable for a fresh job
                registry.Add(job);
                var dir = spool.Dir;
                // Dedicated thread (LongRunning), not the pool: the scan is synchronous and can run for hours.
                // Never through JobQueue.Enqueue either (see SearchRunner's doc comment).
                _ = Task.Factory.StartNew(() => SearchRunner.Execute(job, spec, dbs, disk, dir, registry, log, job.Cts.Token, searchHook),
                    CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                handedOff = true;
            }
            finally
            {
                if (!handedOff)
                {
                    job?.Dispose();   // never leak a spool file or the single-search slot on a refused/failed start
                    SearchRunner.Exit();
                }
            }
            return Results.Json(new { id = job.Id, query, ignoreCase, contains,
                min = rangeMin, max = rangeMax }, Json, statusCode: StatusCodes.Status202Accepted);
        }).RequireRateLimiting(SearchLimiter);

        // Import history: reads the sidecar only (no DbLease), so it works while the db is importing.
        api.MapGet("/dbs/{name}/imports", (string name, int? limit, int? offset, DataRootResolver data, IImportHistory history) =>
        {
            data.ResolveExisting(name);                       // 400 bad name / 404 missing
            var page = history.List(name, Math.Clamp(limit ?? 50, 1, ImportHistory.MaxEntries), Math.Max(0, offset ?? 0));
            return Results.Ok(new { total = page.Total, entries = page.Entries });
        });

        api.MapPost("/dbs/{name}/imports/check", (string name, ImportCheckRequest req, DataRootResolver data, IImportHistory history) =>
        {
            data.ResolveExisting(name);
            if (req.Min < 0 || req.Max < 0) return Problem(400, "min and max must be non-negative.");
            if (req.Min > req.Max) return Problem(400, "min must be less than or equal to max.");
            if (req.Sha256 != null)
            {
                if (!ImportHistory.IsValidSha256(req.Sha256)) return Problem(400, "sha256 must be 64 hexadecimal characters.");
                var label = req.Name is { Length: > 0 } n && n.Length <= MaxNameChars ? "upload:" + JobEndpoints.SafeLabel(n) : null;
                var c = history.Check(name, req.Sha256, label, req.Min, req.Max);
                return Results.Ok(new
                {
                    advisory = false,
                    duplicate = c.Duplicate == null ? null : ImportRunner.ToMatch(c.Duplicate),
                    changed = c.Changed == null ? null : ImportRunner.ToMatch(c.Changed),
                    note = c.Note,
                });
            }
            if (req.Name is not { Length: > 0 } nm || nm.Length > MaxNameChars || req.Size is null or < 0)
                return Problem(400, "Provide sha256, or name and size.");
            // Advisory only: the name is the sanitised label (a hint, not proof), the size must also match.
            var labels = new[] { "upload:" + JobEndpoints.SafeLabel(nm) };
            var hit = history.FindByNameAndSize(name, labels, req.Size.Value, req.Min, req.Max);
            var changed = hit == null ? history.FindChangedByName(name, labels) : null;
            return Results.Ok(new
            {
                advisory = true,
                duplicate = hit == null ? null : ImportRunner.ToMatch(hit),
                changed = changed == null ? null : ImportRunner.ToMatch(changed),
                note = (string?)null,
            });
        });

        // Pre-upload duplicate check for the browser: booleans only (never names, hashes or history rows). Reads the
        // sidecar only (no DbLease), so it answers while the db is importing; the post-upload SHA-256 409 stays the backstop.
        api.MapPost("/dbs/{name}/imports/seen", (string name, ImportSeenRequest? req, DataRootResolver data, IImportHistory history) =>
        {
            data.ResolveExisting(name);                       // 400 bad name / 404 missing
            if (req?.Size is null or < 0) return Problem(400, "size must be a non-negative integer.");
            if (req.Sha256 != null && !ImportHistory.IsValidSha256(req.Sha256)) return Problem(400, "sha256 must be 64 hexadecimal characters.");
            var (sizeMatch, duplicate) = history.Seen(name, req.Size.Value, req.Sha256);
            return Results.Ok(new { sizeMatch, duplicate });
        }).RequireRateLimiting(SeenLimiter);

        // Records files already imported outside the app (hashed by the caller's own script; never re-read here).
        api.MapPost("/dbs/{name}/imports/backfill", (string name, List<BackfillEntryRequest>? body, DataRootResolver data, IImportHistory history) =>
        {
            data.ResolveExisting(name);                       // 400 bad name / 404 missing
            var entries = body ?? [];
            var parsed = new List<BackfillEntry>(entries.Count);
            for (int i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                if (e.Name is not { Length: > 0 } nm || nm.Length > MaxNameChars)
                    return Problem(400, $"entry {i}: name is required and at most {MaxNameChars} characters.");
                if (!ImportHistory.IsValidSha256(e.Sha256)) return Problem(400, $"entry {i}: sha256 must be 64 hexadecimal characters.");
                if (e.SizeBytes is null or < 0) return Problem(400, $"entry {i}: sizeBytes must be non-negative.");
                if (e.ImportedAtUtc is null) return Problem(400, $"entry {i}: importedAtUtc is required.");
                // Sanitised once, inside ImportHistory.Backfill (SafeLabel: strips any path, keeps the base name).
                parsed.Add(new BackfillEntry(nm, e.Sha256!, e.SizeBytes.Value, e.ImportedAtUtc.Value));
            }
            // ImportHistoryCapExceededException bubbles to the global handler (413); same convention as the
            // other db exceptions from DataRootResolver/DbManager.
            var r = history.Backfill(name, parsed);
            return Results.Ok(new { written = r.Written, skipped = r.Skipped, total = r.Total });
        });

        api.MapPost("/dbs/{name}/export", async (string name, HttpContext ctx, IDbManager dbs, ExportSlots slots) =>
        {
            var (req, error) = await ReadExportRequest(ctx);
            if (error != null) return error;
            int? min = req.Min, max = req.Max;
            if (Validate(ref min, ref max, req.Length, req.Limit) is { } bad) return bad;
            if (!slots.TryEnter()) return Problem(429, "Too many exports are running. Try again shortly.");
            try
            {
                var lease = dbs.AcquireRead(name);   // owned by the result; released after the stream ends
                return new StreamingExportResult(lease, name, min, max, req.Limit, req.Filename, dbs.ScanLimiter, slots.Exit);
            }
            catch { slots.Exit(); throw; }
        });
    }

    private static bool IsTerminal(string status) => status is "completed" or "failed" or "cancelled";

    private const int MaxNameChars = 256;
    private sealed record CreateDbRequest(string? Name);
    private sealed record SearchRequest(string? Query, bool? IgnoreCase, bool? Contains);
    private sealed record ImportCheckRequest(string? Sha256, string? Name, long? Size, int? Min, int? Max);
    private sealed record ImportSeenRequest(long? Size, string? Sha256);
    private sealed record BackfillEntryRequest(string? Name, string? Sha256, long? SizeBytes, DateTimeOffset? ImportedAtUtc);

    private static IResult Problem(int status, string detail) =>
        Results.Problem(statusCode: status, detail: detail);

    /// <summary>length is shorthand for min=max; all values non-negative; min &lt;= max.</summary>
    private static IResult? Validate(ref int? min, ref int? max, int? length, long? limit)
    {
        if (length is not null) { min = length; max = length; }
        if (min < 0 || max < 0 || limit < 0) return Problem(400, "min, max, length and limit must be non-negative.");
        if (min > max) return Problem(400, "min must be less than or equal to max.");
        return null;
    }

    private sealed record ExportRequest(int? Min, int? Max, int? Length, long? Limit, string? Filename = null);

    // The UI submits a plain form POST (so the browser streams the download to disk); scripts may send JSON.
    private static async Task<(ExportRequest req, IResult? error)> ReadExportRequest(HttpContext ctx)
    {
        try
        {
            if (ctx.Request.HasFormContentType)
            {
                var f = await ctx.Request.ReadFormAsync(ctx.RequestAborted);
                // Absent field = legacy name; present (even empty) = sanitised user name.
                string? fn = f.TryGetValue("filename", out var fv) && fv.Count > 0 ? fv[0] ?? "" : null;
                return (new ExportRequest(Int(f["min"]), Int(f["max"]), Int(f["length"]), Long(f["limit"]), fn), null);
            }
            if (ctx.Request.ContentLength is null or 0 && !ctx.Request.Headers.ContainsKey("Transfer-Encoding"))
                return (new ExportRequest(null, null, null, null), null);
            var body = await ctx.Request.ReadFromJsonAsync<ExportRequest>(ctx.RequestAborted);
            return (body ?? new ExportRequest(null, null, null, null), null);
        }
        catch (Exception e) when (e is FormatException or System.Text.Json.JsonException or InvalidDataException)
        {
            return (default!, Problem(400, "Invalid export parameters."));
        }

        static int? Int(string? s) => string.IsNullOrWhiteSpace(s) ? null : int.Parse(s);
        static long? Long(string? s) => string.IsNullOrWhiteSpace(s) ? null : long.Parse(s);
    }
}

/// <summary>
/// Streams the export straight to the socket in length-then-password order (same bytes as Range()). A dedicated reader
/// thread fills a bounded queue of byte chunks; this handler only awaits the writes (see <see cref="ExportPipeline"/>).
/// The lease outlives the whole stream and is released in finally.
/// </summary>
internal sealed class StreamingExportResult(DbLease lease, string name, int? min, int? max, long? limit, string? filename, ScanLimiter? limiter = null, Action? onDone = null) : IResult
{
    public async Task ExecuteAsync(HttpContext ctx)
    {
        var ct = ctx.RequestAborted;
        ExportPlan? plan = null;
        try
        {
            var range = (min, max) switch
            {
                (null, null) => "all",
                _ => $"{min?.ToString() ?? "0"}-{max?.ToString() ?? "max"}",
            };
            // Legacy name is built server-side from the validated db name and numbers only; a user-supplied
            // name only ever reaches the header through ExportFileName's allowlist rebuild.
            ctx.Response.ContentType = "text/plain; charset=utf-8";
            ctx.Response.Headers.ContentDisposition = filename is null
                ? $"attachment; filename=\"{name}_{range}_{DateTime.UtcNow:yyyyMMddHHmmss}.txt\""
                : ExportFileName.ContentDisposition(ExportFileName.Sanitise(filename));

            var log = ctx.RequestServices.GetService<ILoggerFactory>()?.CreateLogger("HoarderDB.Web.Export");
            try { plan = limiter is null ? null : ExportPipeline.TryPlan(lease, limiter, min, max, limit, ct); }
            catch (Exception e) when (e is not OperationCanceledException) { plan = null; }   // planning is an optimisation: serial fallback (TryPlan releases its permits)
            await ExportPipeline.RunAsync(lease, ctx.Response.Body, min, max, limit, log, ctx.Abort, ct, plan);
        }
        // RunAsync has joined its reader/workers (and disposed every cursor) on every path before this releases the lease;
        // the scan permits (if any) go back last.
        finally { lease.Dispose(); plan?.Dispose(); onDone?.Invoke(); }
    }
}

/// <summary>Process-wide cap on simultaneous exports (each holds a lease and dedicated reader threads). Operator-fixed, not client input.</summary>
public sealed class ExportSlots(int max = ExportSlots.DefaultMax)
{
    public const int DefaultMax = 3;
    private int _active;
    internal int Active => Volatile.Read(ref _active);
    internal bool TryEnter()
    {
        if (Interlocked.Increment(ref _active) <= max) return true;
        Interlocked.Decrement(ref _active);
        return false;
    }
    internal void Exit() => Interlocked.Decrement(ref _active);
}

using System.Text;
using System.Text.Json;
using CrackDB.Web.Jobs;
using CrackDB.Web.Storage;

namespace CrackDB.Web.Endpoints;

public static class ApiEndpoints
{
    private const int MaxLookupChars = 4096;
    /// <summary>Named fixed-window rate-limit policy for the search-start route (same pattern as AuthEndpoints.LoginLimiter).</summary>
    public const string SearchLimiter = "search";
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

        api.MapGet("/dbs/{name}/stats", (string name, bool? full, IDbManager dbs) =>
        {
            using var lease = dbs.AcquireRead(name);
            var isFull = full == true;
            // TODO(P3): move to job system. A full scan holds this request for a long time on a huge DB.
            var lengths = isFull
                ? lease.Db.LengthDistribution().Select(x => new { length = x.length, count = x.count }).ToList()
                : null;
            return Results.Ok(new { name, estimatedCount = lease.Db.EstimatedCount(), full = isFull, lengths });
        });

        api.MapGet("/dbs/{name}/count", (string name, int? min, int? max, int? length, IDbManager dbs) =>
        {
            if (Validate(ref min, ref max, length, null) is { } bad) return bad;
            using var lease = dbs.AcquireRead(name);
            if (min is null && max is null)
                return Results.Ok(new { name, min, max, exact = false, count = lease.Db.EstimatedCount() });
            // TODO(P3): move to job system. Exact count is a full range scan; it can block on a huge DB.
            return Results.Ok(new { name, min, max, exact = true, count = lease.Db.CountRange(min, max) });
        });

        api.MapGet("/dbs/{name}/lookup", (string name, string? word, IDbManager dbs) =>
        {
            if (string.IsNullOrEmpty(word) || word.Length > MaxLookupChars)
                return Problem(400, $"'word' is required and at most {MaxLookupChars} characters.");
            using var lease = dbs.AcquireRead(name);
            return Results.Ok(new { name, word, found = lease.Db.Contains(word) });
        });

        // "Contains" substring search. Exact match is answered inline (one point read, same as
        // /lookup); the substring scan runs as its own background job (SearchRunner), never through
        // JobQueue (would block unrelated imports/compacts) and never holding one AcquireRead lease
        // for the whole scan (would stall a future write window). Rate-limited and gated to one
        // search job at a time, both server-side (see SearchRunner).
        api.MapPost("/dbs/{name}/search", (string name, SearchRequest req, IDbManager dbs, JobRegistry registry,
            ILoggerFactory loggerFactory, ISearchProgressHook searchHook) =>
        {
            var log = loggerFactory.CreateLogger("CrackDB.Web.Jobs.SearchRunner");
            var query = req.Query ?? "";
            if (query.Length < SearchRunner.MinQueryLength)
                return Problem(400, $"'query' must be at least {SearchRunner.MinQueryLength} characters.");
            if (req.Min < 0 || req.Max < 0) return Problem(400, "min and max must be non-negative.");
            if (req.Min > req.Max) return Problem(400, "min must be less than or equal to max.");
            // Automatic length-narrowing: never scan entries shorter than the query itself, on top of
            // whatever (wider) lower bound the caller asked for.
            int min = Math.Max(query.Length, req.Min ?? 0);
            int? max = req.Max;
            int cap = Math.Clamp(req.Cap ?? SearchRunner.DefaultResultCap, 1, SearchRunner.MaxResultCap);

            bool exactFound;
            using (var lease = dbs.AcquireRead(name)) exactFound = lease.Db.Contains(query);

            if (!SearchRunner.TryEnter())
                return Results.Problem(statusCode: 409, title: "Search is busy.",
                    detail: "Another search is already running. Only one search runs at a time.");
            var job = new Job(JobKind.Search, name, query) { Min = min, Max = max };
            if (!job.TryStart())
            {
                // Unreachable for a freshly constructed job (TryStart only fails if already
                // non-Queued), kept only as a safety net so the gate can never leak.
                SearchRunner.Exit();
                return Results.Problem(statusCode: 500, detail: "Could not start the search job.");
            }
            registry.Add(job);
            // Task.Run, not a bare call: SearchRunner.ExecuteAsync must never run inline on this
            // request thread (its AcquireRead/Scan work is synchronous C# iteration with no `await`
            // between chunks, so without Task.Run the whole first chunk - and any test/prod hook -
            // would block the HTTP response itself). Never through JobQueue.Enqueue either (see
            // SearchRunner's doc comment): this is a fire-and-forget background task of its own.
            _ = Task.Run(() => SearchRunner.ExecuteAsync(job, query, ignoreCase: true, cap, dbs, registry, log, job.Cts.Token, searchHook));
            return Results.Json(new { id = job.Id, exactFound, query, cap }, Json, statusCode: StatusCodes.Status202Accepted);
        }).RequireRateLimiting(SearchLimiter);

        // Import history: reads the sidecar only (no DbLease), so it works while the db is importing.
        api.MapGet("/dbs/{name}/imports", (string name, int? limit, int? offset, DataRootResolver data, IImportHistory history) =>
        {
            data.ResolveExisting(name);                       // 400 bad name / 404 missing
            var page = history.List(name, Math.Clamp(limit ?? 50, 1, 200), Math.Max(0, offset ?? 0));
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

        api.MapPost("/dbs/{name}/export", async (string name, HttpContext ctx, IDbManager dbs) =>
        {
            var (req, error) = await ReadExportRequest(ctx);
            if (error != null) return error;
            int? min = req.Min, max = req.Max;
            if (Validate(ref min, ref max, req.Length, req.Limit) is { } bad) return bad;
            var lease = dbs.AcquireRead(name);   // owned by the result; released after the stream ends
            return new StreamingExportResult(lease, name, min, max, req.Limit);
        });
    }

    private static bool IsTerminal(string status) => status is "completed" or "failed" or "cancelled";

    private const int MaxNameChars = 256;
    private sealed record CreateDbRequest(string? Name);
    private sealed record SearchRequest(string? Query, int? Min, int? Max, int? Cap);
    private sealed record ImportCheckRequest(string? Sha256, string? Name, long? Size, int? Min, int? Max);
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

    private sealed record ExportRequest(int? Min, int? Max, int? Length, long? Limit);

    // The UI submits a plain form POST (so the browser streams the download to disk); scripts may send JSON.
    private static async Task<(ExportRequest req, IResult? error)> ReadExportRequest(HttpContext ctx)
    {
        try
        {
            if (ctx.Request.HasFormContentType)
            {
                var f = await ctx.Request.ReadFormAsync(ctx.RequestAborted);
                return (new ExportRequest(Int(f["min"]), Int(f["max"]), Int(f["length"]), Long(f["limit"])), null);
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
/// Streams Range() straight to the socket in length-then-password order. Bounded memory: output is
/// buffered in ~512 KiB chunks and written asynchronously (Kestrel forbids sync body writes).
/// The lease outlives the whole stream and is released in finally.
/// </summary>
internal sealed class StreamingExportResult(DbLease lease, string name, int? min, int? max, long? limit) : IResult
{
    private const int ChunkBytes = 512 * 1024;

    public async Task ExecuteAsync(HttpContext ctx)
    {
        var ct = ctx.RequestAborted;
        try
        {
            var range = (min, max) switch
            {
                (null, null) => "all",
                _ => $"{min?.ToString() ?? "0"}-{max?.ToString() ?? "max"}",
            };
            // Filename is built server-side from the validated db name and numbers only.
            ctx.Response.ContentType = "text/plain; charset=utf-8";
            ctx.Response.Headers.ContentDisposition =
                $"attachment; filename=\"{name}_{range}_{DateTime.UtcNow:yyyyMMddHHmmss}.txt\"";

            using var buf = new MemoryStream(ChunkBytes + 64 * 1024);
            await using var writer = new StreamWriter(buf, new UTF8Encoding(false), 64 * 1024, leaveOpen: true) { NewLine = "\n" };
            var body = ctx.Response.Body;
            foreach (var word in lease.Db.Range(min, max, limit))
            {
                writer.WriteLine(word);
                if (buf.Length >= ChunkBytes)
                {
                    writer.Flush();
                    ct.ThrowIfCancellationRequested();
                    await body.WriteAsync(buf.GetBuffer().AsMemory(0, (int)buf.Length), ct);
                    buf.SetLength(0);
                }
            }
            writer.Flush();
            if (buf.Length > 0) await body.WriteAsync(buf.GetBuffer().AsMemory(0, (int)buf.Length), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { /* client went away */ }
        finally { lease.Dispose(); }
    }
}

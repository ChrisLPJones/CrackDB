using System.Text;
using CrackDB.Web.Storage;

namespace CrackDB.Web.Endpoints;

public static class ApiEndpoints
{
    private const int MaxLookupChars = 4096;

    public static void MapApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api").RequireAuthorization();

        api.MapGet("/dbs", (IDbManager dbs) => Results.Ok(new { databases = dbs.ListDatabases() }));

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

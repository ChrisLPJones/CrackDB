using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CrackDB.Web.Jobs;
using CrackDB.Web.Options;
using CrackDB.Web.Storage;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace CrackDB.Web.Endpoints;

public static class JobEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const int MaxFieldBytes = 256;
    public const long DiskReserveBytes = 64L * 1024 * 1024;
    // One browser upload streams to disk at a time: parallel uploads would each only self-limit.
    private static readonly SemaphoreSlim UploadGate = new(1, 1);
    private static readonly TimeSpan SsePoll = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan SseKeepalive = TimeSpan.FromSeconds(15);

    public sealed record ImportRequest(string? Db, string? Source, int? Min, int? Max, bool? Force = null);
    public sealed record CompactRequest(string? Db);

    public static void MapJobs(this IEndpointRouteBuilder app)
    {
        // Same posture as /api: every route authenticated (the SSE route is cookie-gated the same way;
        // EventSource cannot send X-API-Key, scripts poll GET /api/jobs/{id}).
        var jobs = app.MapGroup("/api/jobs").RequireAuthorization();

        jobs.MapPost("/import", (ImportRequest req, DataRootResolver data, IncomingPathResolver incoming, JobQueue queue) =>
        {
            data.Resolve(req.Db);                                  // 400 on a bad name; the db may be new
            var path = incoming.Resolve(req.Source);               // 400 / 404
            if (Validate(req.Min, req.Max) is { } bad) return bad;
            var job = new Job(JobKind.Import, req.Db!, req.Source)
            {
                SourcePath = path, Min = req.Min, Max = req.Max, Force = req.Force == true,
            };
            queue.Enqueue(job);
            return Accepted(job);
        });

        jobs.MapPost("/compact", (CompactRequest req, DataRootResolver data, JobQueue queue) =>
        {
            data.ResolveExisting(req.Db);                          // 404 if the db is missing
            var job = new Job(JobKind.Compact, req.Db!, null);
            queue.Enqueue(job);
            return Accepted(job);
        });

        jobs.MapPost("/upload", UploadAsync);

        jobs.MapGet("", (JobRegistry reg) => Results.Ok(new { jobs = reg.List() }));

        jobs.MapGet("/{id}", (string id, JobRegistry reg) =>
            Results.Ok((reg.Get(id) ?? throw new JobNotFoundException("Job not found.")).Snapshot()));

        jobs.MapPost("/{id}/cancel", (string id, JobRegistry reg, JobQueue queue) =>
        {
            var job = reg.Get(id) ?? throw new JobNotFoundException("Job not found.");
            return Results.Json(queue.Cancel(job), Json, statusCode: StatusCodes.Status202Accepted);
        });

        jobs.MapGet("/{id}/events", EventsAsync);
    }

    private static IResult Accepted(Job job) =>
        Results.Json(new { id = job.Id }, statusCode: StatusCodes.Status202Accepted);

    private static IResult? Validate(int? min, int? max)
    {
        if (min < 0 || max < 0) return Results.Problem(statusCode: 400, detail: "min and max must be non-negative.");
        if (min > max) return Results.Problem(statusCode: 400, detail: "min must be less than or equal to max.");
        return null;
    }

    // ---- SSE ----

    private static async Task EventsAsync(string id, HttpContext ctx, JobRegistry reg)
    {
        var job = reg.Get(id) ?? throw new JobNotFoundException("Job not found.");
        var ct = ctx.RequestAborted;
        ctx.Response.ContentType = "text/event-stream";
        ctx.Response.Headers.CacheControl = "no-store";
        ctx.Response.Headers["X-Accel-Buffering"] = "no";     // proxies must not buffer the stream
        await ctx.Response.Body.FlushAsync(ct);

        string? last = null;
        var lastWrite = DateTime.UtcNow;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var snap = job.Snapshot();
                var frame = JsonSerializer.Serialize(snap, Json);
                var terminal = snap.Status is "completed" or "failed" or "cancelled";
                if (frame != last)
                {
                    await ctx.Response.WriteAsync($"data: {frame}\n\n", ct);
                    await ctx.Response.Body.FlushAsync(ct);
                    last = frame; lastWrite = DateTime.UtcNow;
                }
                else if (DateTime.UtcNow - lastWrite >= SseKeepalive)
                {
                    await ctx.Response.WriteAsync(": keepalive\n\n", ct);
                    await ctx.Response.Body.FlushAsync(ct);
                    lastWrite = DateTime.UtcNow;
                }
                if (terminal) return;                          // final snapshot sent; close the stream
                await Task.Delay(SsePoll, ct);
            }
        }
        catch (OperationCanceledException) { /* client went away */ }
    }

    // ---- upload ----

    // multipart/form-data, streamed. Text fields (db, min, max) must precede the single "file" part.
    private static async Task<IResult> UploadAsync(HttpContext ctx, DataRootResolver data,
        IncomingPathResolver incoming, JobQueue queue, IOptions<UploadOptions> upload, IDiskSpace disk,
        IImportHistory history)
    {
        var max = upload.Value.MaxBytes;
        var unlimited = max <= 0;                                  // 0 / unset: no byte cap
        var root = incoming.Root;                                  // throws 404 when file import is not configured

        // The 64 KiB global limit is lifted for this route only, and replaced by the upload cap
        // (plus slack for multipart framing), or removed when unlimited. Not available on every
        // server (e.g. TestServer).
        var limit = ctx.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (limit is { IsReadOnly: false }) limit.MaxRequestBodySize = unlimited ? null : max + 1024 * 1024;
        if (!unlimited && ctx.Request.ContentLength > max + 1024 * 1024)
            throw new UploadTooLargeException("Upload exceeds the size limit.");

        if (!MediaTypeHeaderValue.TryParse(ctx.Request.ContentType, out var ct)
            || !ct.MediaType.Equals("multipart/form-data", StringComparison.OrdinalIgnoreCase)
            || HeaderUtilities.RemoveQuotes(ct.Boundary).Value is not { Length: > 0 and <= 200 } boundary)
            return Results.Problem(statusCode: 400, detail: "Expected multipart/form-data.");

        // Room for the declared (or worst-case) size plus a reserve, before writing anything.
        var need = (unlimited ? ctx.Request.ContentLength ?? 0 : Math.Min(ctx.Request.ContentLength ?? max, max)) + DiskReserveBytes;
        if (disk.FreeBytes(root) is { } free && free < need)
            return Results.Problem(statusCode: 507, detail: "Not enough free disk space for the upload.");

        if (!await UploadGate.WaitAsync(0))
            return Results.Problem(statusCode: 409, detail: "Another upload is already in progress.");
        try
        {
            string? db = null; int? min = null, maxLen = null; bool force = false;
            var reader = new MultipartReader(boundary, ctx.Request.Body);
            string? temp = null;
            try
            {
                MultipartSection? section;
                while ((section = await reader.ReadNextSectionAsync(ctx.RequestAborted)) != null)
                {
                    var cd = section.GetContentDispositionHeader();
                    if (cd == null) return Results.Problem(statusCode: 400, detail: "Malformed upload.");
                    if (cd.IsFileDisposition())
                    {
                        if (!string.Equals(HeaderUtilities.RemoveQuotes(cd.Name).Value, "file", StringComparison.Ordinal))
                            return Results.Problem(statusCode: 400, detail: "Unexpected upload part.");
                        if (temp != null) return Results.Problem(statusCode: 400, detail: "Only one file per upload.");
                        data.Resolve(db);                              // db must precede the file; 400 otherwise
                        if (Validate(min, maxLen) is { } bad) return bad;

                        var label = "upload:" + SafeLabel(cd.FileName.Value ?? cd.FileNameStar.Value);
                        Directory.CreateDirectory(incoming.UploadDir);
                        // Server-generated name; the client filename is only ever a display label.
                        temp = Path.Combine(incoming.UploadDir, Guid.NewGuid().ToString("N") + ".part");
                        long total = 0, sinceCheck = 0;
                        // Hashed while streaming to disk: no extra pass. Raw bytes, so it equals sha256sum.
                        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                        var opts = new FileStreamOptions
                        {
                            Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None,
                            Options = FileOptions.Asynchronous | FileOptions.SequentialScan, BufferSize = 1 << 16,
                        };
                        if (!OperatingSystem.IsWindows())
                            opts.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                        await using (var fs = new FileStream(temp, opts))
                        {
                            var buf = new byte[1 << 16];
                            int n;
                            while ((n = await section.Body.ReadAsync(buf, ctx.RequestAborted)) > 0)
                            {
                                total += n;
                                if (!unlimited && total > max) throw new UploadTooLargeException("Upload exceeds the size limit.");
                                sha.AppendData(buf, 0, n);
                                await fs.WriteAsync(buf.AsMemory(0, n), ctx.RequestAborted);
                                // Unbounded stream: ContentLength may be absent or understated, so keep re-checking
                                // the free space and abort (the partial file is deleted below) rather than fill the disk.
                                if (unlimited && (sinceCheck += n) >= disk.RecheckIntervalBytes)
                                {
                                    sinceCheck = 0;
                                    if (disk.FreeBytes(root) is { } left && left < DiskReserveBytes)
                                        return Results.Problem(statusCode: 507, detail: "Not enough free disk space for the upload.");
                                }
                            }
                        }
                        if (total == 0) return Results.Problem(statusCode: 400, detail: "The uploaded file is empty.");

                        var hex = Convert.ToHexStringLower(sha.GetHashAndReset());
                        // Early answer so the client learns about a duplicate from the upload itself. The queue worker
                        // re-checks at job start (two queued jobs with the same content must not both import).
                        var check = history.Check(db!, hex, label, min, maxLen);
                        if (check.Duplicate is { } dup && !force)
                            return Results.Problem(statusCode: 409, title: "Already imported.",
                                detail: $"Identical content was already imported on {dup.ImportedAt.UtcDateTime:yyyy-MM-dd}.",
                                extensions: new Dictionary<string, object?>
                                {
                                    ["code"] = "already_imported",
                                    ["duplicateOf"] = ImportRunner.ToMatch(dup),
                                });

                        var job = new Job(JobKind.Import, db!, label)
                        {
                            SourcePath = temp, DeleteSourceWhenDone = true, Min = min, Max = maxLen, Force = force,
                            Note = check.Note,
                        };
                        job.SetContent(hex, total);
                        queue.Enqueue(job);                            // from here the job owns (and deletes) the file
                        temp = null;
                        var prior = check.Duplicate ?? check.Changed ?? check.Narrower;
                        return Results.Json(new { id = job.Id, note = check.Note, previous = prior == null ? null : ImportRunner.ToMatch(prior) },
                            Json, statusCode: StatusCodes.Status202Accepted);
                    }

                    var name = HeaderUtilities.RemoveQuotes(cd.Name).Value;
                    var value = await ReadFieldAsync(section, ctx.RequestAborted);
                    switch (name)
                    {
                        case "db": db = value; break;
                        case "min": min = ParseInt(value); break;
                        case "max": maxLen = ParseInt(value); break;
                        case "force": force = value.Trim().Equals("true", StringComparison.OrdinalIgnoreCase) || value.Trim() == "1"; break;
                        default: return Results.Problem(statusCode: 400, detail: "Unexpected upload field.");
                    }
                }
                return Results.Problem(statusCode: 400, detail: "No file in the upload.");
            }
            catch (InvalidDataException) { return Results.Problem(statusCode: 400, detail: "Malformed upload."); }
            catch (FormatException) { return Results.Problem(statusCode: 400, detail: "min and max must be integers."); }
            finally
            {
                if (temp != null)
                {
                    try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                }
            }
        }
        finally { UploadGate.Release(); }
    }

    private static int? ParseInt(string v) => string.IsNullOrWhiteSpace(v) ? null : int.Parse(v);

    private static async Task<string> ReadFieldAsync(MultipartSection section, CancellationToken ct)
    {
        var buf = new byte[MaxFieldBytes + 1];
        int total = 0, n;
        while (total < buf.Length && (n = await section.Body.ReadAsync(buf.AsMemory(total), ct)) > 0) total += n;
        if (total > MaxFieldBytes) throw new InvalidDataException("Field too long.");
        return Encoding.UTF8.GetString(buf, 0, total);
    }

    /// <summary>Display-only label from the client filename: base name, safe characters, bounded.</summary>
    internal static string SafeLabel(string? clientName)
    {
        var name = Path.GetFileName((clientName ?? "").Replace('\\', '/'));
        var sb = new StringBuilder();
        foreach (var c in name)
        {
            if (sb.Length >= 64) break;
            sb.Append(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' or ' ' ? c : '_');
        }
        return sb.Length == 0 ? "file" : sb.ToString();
    }
}

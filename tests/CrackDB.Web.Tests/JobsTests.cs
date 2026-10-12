using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CrackDB.Web.Jobs;
using CrackDB.Web.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace CrackDB.Web.Tests;

/// <summary>Blocks the running import at the first hook call (every 1024 lines) until released.</summary>
public sealed class BlockingHook : IImportProgressHook
{
    private volatile bool _armed;
    public ManualResetEventSlim Reached { get; private set; } = new(false);
    public ManualResetEventSlim Release { get; private set; } = new(false);

    public void Arm() { Reached = new(false); Release = new(false); _armed = true; }
    public void Disarm() { _armed = false; Release.Set(); }

    public void OnLines(string dbName, long linesRead)
    {
        if (!_armed) return;
        var (reached, release) = (Reached, Release);
        reached.Set();
        release.Wait(TimeSpan.FromSeconds(60));
    }
}

public class JobsTests
{
    private static string Word(int i) => "w" + i.ToString("D8");

    private static string WriteFile(CrackDbWebFactory f, string name, int lines, bool withBlanks = false)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < lines; i++)
        {
            sb.Append(Word(i)).Append('\n');
            if (withBlanks && i % 100 == 0) sb.Append('\n');
        }
        File.WriteAllText(Path.Combine(f.Incoming, name), sb.ToString());
        return name;
    }

    private static async Task<JsonElement> Body(HttpResponseMessage r) =>
        JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.Clone();

    private static async Task<string> Import(HttpClient c, string db, string source, int? min = null, int? max = null)
    {
        var r = await c.PostAsJsonAsync("/api/jobs/import", new { db, source, min, max });
        Assert.Equal(HttpStatusCode.Accepted, r.StatusCode);
        return (await Body(r)).GetProperty("id").GetString()!;
    }

    private static async Task<JsonElement> Get(HttpClient c, string id) =>
        await Body(await c.GetAsync("/api/jobs/" + id));

    private static async Task<JsonElement> WaitStatus(HttpClient c, string id, params string[] statuses)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            var j = await Get(c, id);
            if (statuses.Contains(j.GetProperty("status").GetString())) return j;
            await Task.Delay(25);
        }
        throw new TimeoutException($"job {id} did not reach {string.Join('|', statuses)}");
    }

    private static async Task<long> ExactCount(HttpClient c, string db)
    {
        var r = await c.GetAsync($"/api/dbs/{db}/count?min=0");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return (await Body(r)).GetProperty("count").GetInt64();
    }

    // ---- 1. serialization ----

    [Fact]
    public async Task Imports_RunOneAtATime()
    {
        var hook = new BlockingHook();
        using var f = new CrackDbWebFactory(1000, null, null, hook);
        var c = await f.NewLoggedInClientAsync();
        WriteFile(f, "a.txt", 5000); WriteFile(f, "b.txt", 5000);

        hook.Arm();
        var a = await Import(c, "Test_a", "a.txt");
        Assert.True(hook.Reached.Wait(TimeSpan.FromSeconds(30)));     // A is mid-import
        var b = await Import(c, "Test_b", "b.txt");

        await Task.Delay(300);
        Assert.Equal("running", (await Get(c, a)).GetProperty("status").GetString());
        Assert.Equal("queued", (await Get(c, b)).GetProperty("status").GetString());

        hook.Disarm();
        var ja = await WaitStatus(c, a, "completed", "failed");
        var jb = await WaitStatus(c, b, "completed", "failed");
        Assert.Equal("completed", ja.GetProperty("status").GetString());
        Assert.Equal("completed", jb.GetProperty("status").GetString());
        Assert.True(jb.GetProperty("startedAt").GetDateTimeOffset() >= ja.GetProperty("endedAt").GetDateTimeOffset());
    }

    [Fact]
    public async Task Read_DuringDrainWindow_FailsFastWith409_NotParkedForWholeImport()
    {
        var hook = new BlockingHook();
        using var f = new CrackDbWebFactory(1000, null, null, hook);
        var c = await f.NewLoggedInClientAsync();
        WriteFile(f, "a.txt", 3000); WriteFile(f, "b.txt", 3000);
        var first = await Import(c, "Test_drain", "a.txt");      // hook not armed: creates the db
        await WaitStatus(c, first, "completed");

        var mgr = f.Services.GetRequiredService<IDbManager>();
        var lease = mgr.AcquireRead("Test_drain");                 // holds Readers > 0 so the write window stays in drain
        hook.Arm();
        var second = await Import(c, "Test_drain", "b.txt");

        // Wait (bounded) until the write window has claimed the handle (Swapping) but cannot drain yet.
        var entries = typeof(DbManager).GetField("_entries", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(mgr)!;
        var entry = ((System.Collections.IDictionary)entries)["Test_drain"]!;
        var swapping = entry.GetType().GetField("Swapping")!;
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!(bool)swapping.GetValue(entry)! && DateTime.UtcNow < deadline) await Task.Delay(10);
        Assert.True((bool)swapping.GetValue(entry)!);
        Assert.False(hook.Reached.IsSet);

        // This reader parks in the drain window (Swapping, not yet Writing).
        var reader = Task.Run(() => mgr.AcquireRead("Test_drain"));
        await Task.Delay(200);
        Assert.False(reader.IsCompleted);

        lease.Dispose();                                           // drain completes -> Writing=true -> parked reader must wake
        Assert.True(hook.Reached.Wait(TimeSpan.FromSeconds(30)));  // import is now mid-write and blocked in the hook
        await Assert.ThrowsAsync<DbBusyException>(() => reader.WaitAsync(TimeSpan.FromSeconds(10)));

        hook.Disarm();
        Assert.Equal("completed", (await WaitStatus(c, second, "completed", "failed")).GetProperty("status").GetString());
    }

    // ---- 2. correctness + create-on-import ----

    [Fact]
    public async Task Import_CreatesNewDb_AndLoadsStreamedFile()
    {
        using var f = new CrackDbWebFactory();
        var c = await f.NewLoggedInClientAsync();
        WriteFile(f, "list.txt", 3000, withBlanks: true);   // 3000 words + 30 blank lines

        var id = await Import(c, "Test_new", "list.txt");
        var j = await WaitStatus(c, id, "completed", "failed");
        Assert.Equal("completed", j.GetProperty("status").GetString());
        Assert.Equal(3030, j.GetProperty("linesRead").GetInt64());
        Assert.True(j.GetProperty("bytesTotal").GetInt64() > 0);

        var dbs = await Body(await c.GetAsync("/api/dbs"));
        Assert.Contains("Test_new", dbs.GetProperty("databases").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal(3000, await ExactCount(c, "Test_new"));
        var lk = await Body(await c.GetAsync("/api/dbs/Test_new/lookup?word=" + Word(1234)));
        Assert.True(lk.GetProperty("found").GetBoolean());
        lk = await Body(await c.GetAsync("/api/dbs/Test_new/lookup?word=" + Word(5000)));
        Assert.False(lk.GetProperty("found").GetBoolean());
    }

    [Fact]
    public async Task Import_HonoursMinMax()
    {
        using var f = new CrackDbWebFactory();
        var c = await f.NewLoggedInClientAsync();
        File.WriteAllText(Path.Combine(f.Incoming, "m.txt"), "a\nabc\nabcdef\n");
        var id = await Import(c, "Test_minmax", "m.txt", min: 2, max: 4);
        await WaitStatus(c, id, "completed");
        Assert.Equal(1, await ExactCount(c, "Test_minmax"));
    }

    [Theory]
    [InlineData("../x.txt", "Test_x", 400)]
    [InlineData("/etc/passwd", "Test_x", 400)]
    [InlineData("missing.txt", "Test_x", 404)]
    [InlineData("list.txt", "../evil", 400)]
    [InlineData("list.txt", "a/b", 400)]
    public async Task Import_RejectsBadInput(string source, string db, int status)
    {
        using var f = new CrackDbWebFactory();
        var c = await f.NewLoggedInClientAsync();
        WriteFile(f, "list.txt", 10);
        var r = await c.PostAsJsonAsync("/api/jobs/import", new { db, source });
        Assert.Equal(status, (int)r.StatusCode);
        Assert.Contains("problem+json", r.Content.Headers.ContentType?.ToString());
    }

    [Fact]
    public async Task Import_BadMinMax_Is400()
    {
        using var f = new CrackDbWebFactory();
        var c = await f.NewLoggedInClientAsync();
        WriteFile(f, "list.txt", 10);
        var r = await c.PostAsJsonAsync("/api/jobs/import", new { db = "Test_x", source = "list.txt", min = 5, max = 2 });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    // ---- 3. cancellation ----

    [Fact]
    public async Task Cancel_StopsImport_KeepsFlushedPartialData_AndReopensRead()
    {
        var hook = new BlockingHook();
        using var f = new CrackDbWebFactory(1000, null, null, hook);
        var c = await f.NewLoggedInClientAsync();
        WriteFile(f, "big.txt", 200_000);

        hook.Arm();
        var id = await Import(c, "Test_cancel", "big.txt");
        Assert.True(hook.Reached.Wait(TimeSpan.FromSeconds(30)));
        var r = await c.PostAsync($"/api/jobs/{id}/cancel", null);
        Assert.Equal(HttpStatusCode.Accepted, r.StatusCode);
        hook.Disarm();

        var j = await WaitStatus(c, id, "cancelled", "completed", "failed");
        Assert.Equal("cancelled", j.GetProperty("status").GetString());
        var n = await ExactCount(c, "Test_cancel");   // a successful read proves the db is reopened in Read mode
        Assert.InRange(n, 1, 199_999);
    }

    [Fact]
    public async Task Cancel_QueuedJob_NeverRuns()
    {
        var hook = new BlockingHook();
        using var f = new CrackDbWebFactory(1000, null, null, hook);
        var c = await f.NewLoggedInClientAsync();
        WriteFile(f, "a.txt", 5000); WriteFile(f, "b.txt", 5000);
        hook.Arm();
        var a = await Import(c, "Test_a", "a.txt");
        Assert.True(hook.Reached.Wait(TimeSpan.FromSeconds(30)));
        var b = await Import(c, "Test_b", "b.txt");
        Assert.Equal(HttpStatusCode.Accepted, (await c.PostAsync($"/api/jobs/{b}/cancel", null)).StatusCode);
        hook.Disarm();
        await WaitStatus(c, a, "completed");
        var jb = await Get(c, b);
        Assert.Equal("cancelled", jb.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, jb.GetProperty("startedAt").ValueKind);
        var dbs = await Body(await c.GetAsync("/api/dbs"));
        Assert.DoesNotContain("Test_b", dbs.GetProperty("databases").EnumerateArray().Select(x => x.GetString()));
    }

    // ---- 4. busy fast path ----

    [Fact]
    public async Task ReadOfImportingDb_Is409_OtherDbsStillServe()
    {
        var hook = new BlockingHook();
        using var f = new CrackDbWebFactory(1000, null, null, hook);
        var c = await f.NewLoggedInClientAsync();
        WriteFile(f, "a.txt", 5000);
        hook.Arm();
        var id = await Import(c, CrackDbWebFactory.DbName, "a.txt");   // import INTO the seeded db
        Assert.True(hook.Reached.Wait(TimeSpan.FromSeconds(30)));

        var busy = await c.GetAsync($"/api/dbs/{CrackDbWebFactory.DbName}/lookup?word=abc");
        Assert.Equal(HttpStatusCode.Conflict, busy.StatusCode);
        Assert.Contains("problem+json", busy.Content.Headers.ContentType?.ToString());
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/dbs")).StatusCode);   // other routes unaffected

        hook.Disarm();
        await WaitStatus(c, id, "completed");
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync($"/api/dbs/{CrackDbWebFactory.DbName}/lookup?word=abc")).StatusCode);
    }

    [Fact]
    public async Task ReadOfOtherDb_WorksDuringImport()
    {
        var hook = new BlockingHook();
        using var f = new CrackDbWebFactory(1000, null, null, hook);
        var c = await f.NewLoggedInClientAsync();
        WriteFile(f, "a.txt", 2000); WriteFile(f, "b.txt", 5000);
        await WaitStatus(c, await Import(c, "Test_other", "a.txt"), "completed");

        hook.Arm();
        var id = await Import(c, CrackDbWebFactory.DbName, "b.txt");
        Assert.True(hook.Reached.Wait(TimeSpan.FromSeconds(30)));
        var r = await c.GetAsync("/api/dbs/Test_other/lookup?word=" + Word(5));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await c.GetAsync($"/api/dbs/{CrackDbWebFactory.DbName}/lookup?word=abc")).StatusCode);
        hook.Disarm();
        await WaitStatus(c, id, "completed");
    }

    // ---- 5. compact ----

    [Fact]
    public async Task Compact_ExistingDb_Completes_MissingDb_Is404()
    {
        using var f = new CrackDbWebFactory();
        var c = await f.NewLoggedInClientAsync();
        var r = await c.PostAsJsonAsync("/api/jobs/compact", new { db = CrackDbWebFactory.DbName });
        Assert.Equal(HttpStatusCode.Accepted, r.StatusCode);
        var id = (await Body(r)).GetProperty("id").GetString()!;
        var j = await WaitStatus(c, id, "completed", "failed");
        Assert.Equal("completed", j.GetProperty("status").GetString());
        var lk = await Body(await c.GetAsync($"/api/dbs/{CrackDbWebFactory.DbName}/lookup?word=abc"));
        Assert.True(lk.GetProperty("found").GetBoolean());

        var missing = await c.PostAsJsonAsync("/api/jobs/compact", new { db = "Test_missing" });
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/jobs/compact", new { db = "../x" })).StatusCode);
    }

    // ---- job listing / detail ----

    [Fact]
    public async Task ListAndDetail_ReflectJobs_UnknownIs404()
    {
        using var f = new CrackDbWebFactory();
        var c = await f.NewLoggedInClientAsync();
        WriteFile(f, "list.txt", 100);
        var id = await Import(c, "Test_l", "list.txt");
        await WaitStatus(c, id, "completed");
        var list = await Body(await c.GetAsync("/api/jobs"));
        Assert.Contains(id, list.GetProperty("jobs").EnumerateArray().Select(x => x.GetProperty("id").GetString()));
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/api/jobs/nope")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.PostAsync("/api/jobs/nope/cancel", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/api/jobs/nope/events")).StatusCode);
    }

    [Fact]
    public async Task Registry_KeepsBoundedHistory()
    {
        var reg = new JobRegistry();
        for (int i = 0; i < JobRegistry.MaxFinished + 10; i++)
        {
            var j = new Job(JobKind.Compact, "Test_x", null);
            reg.Add(j); j.Finish(JobStatus.Completed); reg.MarkFinished(j);
        }
        Assert.Equal(JobRegistry.MaxFinished, reg.List().Count);
        await Task.CompletedTask;
    }

    // ---- 8. auth ----

    [Theory]
    [InlineData("GET", "/api/jobs")]
    [InlineData("GET", "/api/jobs/abc")]
    [InlineData("GET", "/api/jobs/abc/events")]
    [InlineData("POST", "/api/jobs/import")]
    [InlineData("POST", "/api/jobs/compact")]
    [InlineData("POST", "/api/jobs/upload")]
    [InlineData("POST", "/api/jobs/abc/cancel")]
    public async Task JobRoutes_WithoutCredentials_Are401(string method, string url)
    {
        using var f = new CrackDbWebFactory();
        var r = await f.NewClient().SendAsync(new HttpRequestMessage(new HttpMethod(method), url));
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
    }

    [Fact]
    public async Task ApiKey_CanQueueAndPollJobs()
    {
        using var f = new CrackDbWebFactory();
        var c = f.NewApiKeyClient();
        var r = await c.PostAsJsonAsync("/api/jobs/compact", new { db = CrackDbWebFactory.DbName });
        Assert.Equal(HttpStatusCode.Accepted, r.StatusCode);
        var id = (await Body(r)).GetProperty("id").GetString()!;
        await WaitStatus(c, id, "completed");
    }

    // ---- 9. SSE ----

    [Fact]
    public async Task Events_StreamProgressThenTerminalFrame()
    {
        var hook = new BlockingHook();
        using var f = new CrackDbWebFactory(1000, null, null, hook);
        var c = await f.NewLoggedInClientAsync();
        WriteFile(f, "a.txt", 5000);
        hook.Arm();
        var id = await Import(c, "Test_sse", "a.txt");
        Assert.True(hook.Reached.Wait(TimeSpan.FromSeconds(30)));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var resp = await c.GetAsync($"/api/jobs/{id}/events", HttpCompletionOption.ResponseHeadersRead, cts.Token);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("text/event-stream", resp.Content.Headers.ContentType?.MediaType);
        using var reader = new StreamReader(await resp.Content.ReadAsStreamAsync(cts.Token));

        var frames = new List<JsonElement>();
        async Task<JsonElement> NextFrame()
        {
            while (true)
            {
                var line = await reader.ReadLineAsync(cts.Token) ?? throw new EndOfStreamException();
                if (line.StartsWith("data: ")) { var j = JsonDocument.Parse(line[6..]).RootElement.Clone(); frames.Add(j); return j; }
            }
        }

        var first = await NextFrame();
        Assert.Equal("running", first.GetProperty("status").GetString());
        hook.Disarm();
        JsonElement last;
        do { last = await NextFrame(); } while (last.GetProperty("status").GetString() == "running");
        Assert.Equal("completed", last.GetProperty("status").GetString());
        Assert.Equal(5000, last.GetProperty("linesRead").GetInt64());
        string? tail;
        do { tail = await reader.ReadLineAsync(cts.Token); } while (tail != null);   // stream closes after the terminal frame
    }

    [Fact]
    public async Task Events_FinishedJob_SendsOneTerminalFrameAndCloses()
    {
        using var f = new CrackDbWebFactory();
        var c = await f.NewLoggedInClientAsync();
        var r = await c.PostAsJsonAsync("/api/jobs/compact", new { db = CrackDbWebFactory.DbName });
        var id = (await Body(r)).GetProperty("id").GetString()!;
        await WaitStatus(c, id, "completed");
        var text = await c.GetStringAsync($"/api/jobs/{id}/events");
        Assert.Contains("\"status\":\"completed\"", text);
    }

    // ---- failures carry a generic message only ----

    [Fact]
    public async Task FailedJob_CarriesGenericMessageOnly()
    {
        var hook = new BlockingHook();
        using var f = new CrackDbWebFactory(1000, null, null, hook);
        var c = await f.NewLoggedInClientAsync();
        WriteFile(f, "a.txt", 5000); WriteFile(f, "gone.txt", 100);
        hook.Arm();
        var a = await Import(c, "Test_a", "a.txt");
        Assert.True(hook.Reached.Wait(TimeSpan.FromSeconds(30)));
        var b = await Import(c, "Test_fail", "gone.txt");
        File.Delete(Path.Combine(f.Incoming, "gone.txt"));      // vanishes while queued
        hook.Disarm();
        await WaitStatus(c, a, "completed");
        var j = await WaitStatus(c, b, "failed");
        var raw = j.GetRawText();
        Assert.DoesNotContain(f.Incoming, raw);
        Assert.False(string.IsNullOrEmpty(j.GetProperty("error").GetString()));
        // the db is still readable (reopened Read) after a failed import
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/dbs/Test_fail/count")).StatusCode);
    }
}

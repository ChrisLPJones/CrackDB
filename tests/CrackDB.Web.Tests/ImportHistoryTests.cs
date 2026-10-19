using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CrackDB.Web.Jobs;
using CrackDB.Web.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace CrackDB.Web.Tests;

/// <summary>Throws from the import loop on demand, to produce a failed import after the content was hashed.</summary>
public sealed class ThrowingHook : IImportProgressHook
{
    public volatile bool Armed;
    public void OnLines(string dbName, long linesRead) { if (Armed) throw new IOException("boom (test)"); }
}

// Shares the "Uploads" collection with UploadTests: the upload gate is process-wide, so parallel classes would see spurious 409s.
[Collection("Uploads")]
public class ImportHistoryTests
{
    private const string Abc = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";   // SHA-256("abc")

    private static byte[] Lines(string prefix, int n) =>
        Encoding.UTF8.GetBytes(string.Join('\n', Enumerable.Range(0, n).Select(i => prefix + i)) + "\n");

    private static string Sha(byte[] b) => Convert.ToHexStringLower(SHA256.HashData(b));

    private static MultipartFormDataContent Form(string db, byte[] file, string fileName = "list.txt",
        string? min = null, string? max = null, bool force = false)
    {
        var form = new MultipartFormDataContent();
        form.Add(new StringContent(db), "db");
        if (min != null) form.Add(new StringContent(min), "min");
        if (max != null) form.Add(new StringContent(max), "max");
        if (force) form.Add(new StringContent("true"), "force");
        var fc = new ByteArrayContent(file);
        fc.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        form.Add(fc, "file", fileName);
        return form;
    }

    private static async Task<JsonElement> Body(HttpResponseMessage r) =>
        JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.Clone();

    private static async Task<JsonElement> WaitTerminal(HttpClient c, string id)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            var j = await Body(await c.GetAsync("/api/jobs/" + id));
            if (j.GetProperty("status").GetString() is "completed" or "failed" or "cancelled") return j;
            await Task.Delay(25);
        }
        throw new TimeoutException();
    }

    /// <summary>Uploads and, if accepted (202), waits for the job. Returns (response status, response body, final job or null).</summary>
    private static async Task<(HttpStatusCode, JsonElement, JsonElement?)> Upload(HttpClient c, string db, byte[] data,
        string name = "list.txt", string? min = null, string? max = null, bool force = false)
    {
        var r = await c.PostAsync("/api/jobs/upload", Form(db, data, name, min, max, force));
        var body = await Body(r);
        if (r.StatusCode != HttpStatusCode.Accepted) return (r.StatusCode, body, null);
        return (r.StatusCode, body, await WaitTerminal(c, body.GetProperty("id").GetString()!));
    }

    private static async Task<JsonElement> History(HttpClient c, string db, string query = "")
    {
        var r = await c.GetAsync($"/api/dbs/{db}/imports{query}");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return await Body(r);
    }

    private static string Sidecar(CrackDbWebFactory f, string db) => Path.Combine(f.Root, db, ImportHistory.FileName);

    // ---- hashing ----

    [Fact]
    public async Task Upload_RecordsKnownSha256Vector_AndMatchesSha256sumOfTheSameBytes()
    {
        using var f = new CrackDbWebFactory();
        var c = await f.NewLoggedInClientAsync();
        var (st, _, job) = await Upload(c, "Test_h1", Encoding.ASCII.GetBytes("abc"));
        Assert.Equal(HttpStatusCode.Accepted, st);
        Assert.Equal("completed", job!.Value.GetProperty("status").GetString());

        var other = Lines("hh", 777);
        await Upload(c, "Test_h1", other, "other.txt");

        var h = await History(c, "Test_h1");
        Assert.Equal(2, h.GetProperty("total").GetInt32());
        var entries = h.GetProperty("entries").EnumerateArray().ToList();   // newest first
        Assert.Equal(Sha(other), entries[0].GetProperty("sha256").GetString());
        Assert.Equal(Abc, entries[1].GetProperty("sha256").GetString());
        Assert.Equal(3, entries[1].GetProperty("sizeBytes").GetInt64());
        Assert.Equal("completed", entries[1].GetProperty("status").GetString());
        Assert.Equal("upload:list.txt", entries[1].GetProperty("source").GetString());
    }

    // ---- duplicates on upload ----

    [Fact]
    public async Task UploadTwice_Second409AlreadyImported_ForceImportsAndTempFilesAreDeleted()
    {
        using var f = new CrackDbWebFactory();
        var c = await f.NewLoggedInClientAsync();
        var data = Lines("dup", 600);
        Assert.Equal(HttpStatusCode.Accepted, (await Upload(c, "Test_d1", data)).Item1);

        var (st, body, _) = await Upload(c, "Test_d1", data);
        Assert.Equal(HttpStatusCode.Conflict, st);
        Assert.Equal("already_imported", body.GetProperty("code").GetString());
        Assert.Equal("upload:list.txt", body.GetProperty("duplicateOf").GetProperty("source").GetString());
        Assert.Equal(600, body.GetProperty("duplicateOf").GetProperty("linesRead").GetInt64());
        Assert.False(Directory.Exists(Path.Combine(f.Incoming, ".uploads")) && Directory.GetFiles(Path.Combine(f.Incoming, ".uploads")).Length > 0);
        Assert.Equal(1, (await History(c, "Test_d1")).GetProperty("total").GetInt32());   // a rejected upload records nothing

        var (st2, _, job) = await Upload(c, "Test_d1", data, force: true);
        Assert.Equal(HttpStatusCode.Accepted, st2);
        Assert.Equal("completed", job!.Value.GetProperty("status").GetString());
        var h = await History(c, "Test_d1");
        Assert.Equal(2, h.GetProperty("total").GetInt32());
        Assert.True(h.GetProperty("entries")[0].GetProperty("forced").GetBoolean());
        Assert.False(h.GetProperty("entries")[1].GetProperty("forced").GetBoolean());
    }

    [Fact]
    public async Task SameBytesUnderADifferentName_Is409_NamingTheOriginal()
    {
        using var f = new CrackDbWebFactory();
        var c = await f.NewLoggedInClientAsync();
        var data = Lines("ren", 300);
        await Upload(c, "Test_r1", data, "original.txt");
        var (st, body, _) = await Upload(c, "Test_r1", data, "renamed.txt");
        Assert.Equal(HttpStatusCode.Conflict, st);
        Assert.Equal("upload:original.txt", body.GetProperty("duplicateOf").GetProperty("source").GetString());
    }

    [Fact]
    public async Task SameNameChangedBytes_IsAccepted_WithNote_AndTwoRecords()
    {
        using var f = new CrackDbWebFactory();
        var c = await f.NewLoggedInClientAsync();
        await Upload(c, "Test_c1", Lines("v1_", 300), "same.txt");
        var (st, body, job) = await Upload(c, "Test_c1", Lines("v2_", 300), "same.txt");
        Assert.Equal(HttpStatusCode.Accepted, st);
        Assert.Contains("Changed file", body.GetProperty("note").GetString());
        Assert.Equal("completed", job!.Value.GetProperty("status").GetString());
        Assert.Contains("Changed file", job.Value.GetProperty("note").GetString());
        Assert.Equal(2, (await History(c, "Test_c1")).GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task NarrowerVersusWiderLengthFilter()
    {
        using var f = new CrackDbWebFactory();
        var c = await f.NewLoggedInClientAsync();
        var data = Encoding.UTF8.GetBytes(string.Join('\n', Enumerable.Range(1, 20).Select(i => new string('x', i))) + "\n");
        Assert.Equal(HttpStatusCode.Accepted, (await Upload(c, "Test_m1", data, min: "4", max: "8")).Item1);

        // Inside the earlier range: a duplicate.
        Assert.Equal(HttpStatusCode.Conflict, (await Upload(c, "Test_m1", data, min: "5", max: "6")).Item1);
        Assert.Equal(HttpStatusCode.Conflict, (await Upload(c, "Test_m1", data, min: "4", max: "8")).Item1);
        // Wider on either side, or unbounded: new work.
        var (st, body, _) = await Upload(c, "Test_m1", data, min: "1", max: "8");
        Assert.Equal(HttpStatusCode.Accepted, st);
        Assert.Contains("narrower", body.GetProperty("note").GetString());
        Assert.Equal(HttpStatusCode.Accepted, (await Upload(c, "Test_m1", data)).Item1);
        // The unbounded import now covers everything.
        Assert.Equal(HttpStatusCode.Conflict, (await Upload(c, "Test_m1", data, min: "2", max: "9")).Item1);
        Assert.Equal(HttpStatusCode.Conflict, (await Upload(c, "Test_m1", data)).Item1);
    }

    // ---- cancelled / failed never count ----

    [Fact]
    public async Task CancelledImport_IsRecorded_ButDoesNotBlockAReimport()
    {
        var hook = new BlockingHook();
        using var f = new CrackDbWebFactory(1000, null, null, hook);
        var c = await f.NewLoggedInClientAsync();
        var data = Lines("can", 5000);
        hook.Arm();
        var r = await c.PostAsync("/api/jobs/upload", Form("Test_x1", data));
        Assert.Equal(HttpStatusCode.Accepted, r.StatusCode);
        var id = (await Body(r)).GetProperty("id").GetString()!;
        Assert.True(hook.Reached.Wait(TimeSpan.FromSeconds(30)));
        await c.PostAsync($"/api/jobs/{id}/cancel", null);
        hook.Disarm();
        Assert.Equal("cancelled", (await WaitTerminal(c, id)).GetProperty("status").GetString());

        var h = await History(c, "Test_x1");
        Assert.Equal("cancelled", h.GetProperty("entries")[0].GetProperty("status").GetString());

        var (st, _, job) = await Upload(c, "Test_x1", data);          // identical bytes: not "already imported"
        Assert.Equal(HttpStatusCode.Accepted, st);
        Assert.Equal("completed", job!.Value.GetProperty("status").GetString());
        Assert.Equal(2, (await History(c, "Test_x1")).GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task FailedImport_IsRecorded_ButDoesNotBlockAReimport()
    {
        var hook = new ThrowingHook { Armed = true };
        using var f = new CrackDbWebFactory(1000, null, null, hook);
        var c = await f.NewLoggedInClientAsync();
        var data = Lines("fail", 3000);
        var (st, _, job) = await Upload(c, "Test_f1", data);
        Assert.Equal(HttpStatusCode.Accepted, st);
        Assert.Equal("failed", job!.Value.GetProperty("status").GetString());
        Assert.Equal("failed", (await History(c, "Test_f1")).GetProperty("entries")[0].GetProperty("status").GetString());

        hook.Armed = false;
        var (st2, _, job2) = await Upload(c, "Test_f1", data);
        Assert.Equal(HttpStatusCode.Accepted, st2);
        Assert.Equal("completed", job2!.Value.GetProperty("status").GetString());
    }

    // ---- server-side import ----

    [Fact]
    public async Task ServerSideImport_SecondRunIsSkipped_ForceImports_AndHashEqualsSha256sum()
    {
        using var f = new CrackDbWebFactory();
        var c = await f.NewLoggedInClientAsync();
        var data = Lines("srv", 400);
        File.WriteAllBytes(Path.Combine(f.Incoming, "a.txt"), data);

        async Task<JsonElement> Run(bool force)
        {
            var r = await c.PostAsJsonAsync("/api/jobs/import", new { db = "Test_s1", source = "a.txt", force });
            Assert.Equal(HttpStatusCode.Accepted, r.StatusCode);
            return await WaitTerminal(c, (await Body(r)).GetProperty("id").GetString()!);
        }

        Assert.Equal("completed", (await Run(false)).GetProperty("status").GetString());
        var skipped = await Run(false);
        Assert.Equal("cancelled", skipped.GetProperty("status").GetString());
        Assert.StartsWith("Skipped:", skipped.GetProperty("error").GetString());
        Assert.Equal("a.txt", skipped.GetProperty("duplicateOf").GetProperty("source").GetString());
        Assert.Equal(1, (await History(c, "Test_s1")).GetProperty("total").GetInt32());

        Assert.Equal("completed", (await Run(true)).GetProperty("status").GetString());
        var h = await History(c, "Test_s1");
        Assert.Equal(2, h.GetProperty("total").GetInt32());
        Assert.Equal(Sha(data), h.GetProperty("entries")[0].GetProperty("sha256").GetString());
        Assert.True(h.GetProperty("entries")[0].GetProperty("forced").GetBoolean());
    }

    [Fact]
    public async Task TwoQueuedJobsWithTheSameContent_ImportOnce()
    {
        var hook = new BlockingHook();
        using var f = new CrackDbWebFactory(1000, null, null, hook);
        var c = await f.NewLoggedInClientAsync();
        var data = Lines("twin", 5000);
        File.WriteAllBytes(Path.Combine(f.Incoming, "one.txt"), data);
        File.WriteAllBytes(Path.Combine(f.Incoming, "two.txt"), data);

        hook.Arm();
        var r1 = await c.PostAsJsonAsync("/api/jobs/import", new { db = "Test_q1", source = "one.txt" });
        var id1 = (await Body(r1)).GetProperty("id").GetString()!;
        Assert.True(hook.Reached.Wait(TimeSpan.FromSeconds(30)));
        var r2 = await c.PostAsJsonAsync("/api/jobs/import", new { db = "Test_q1", source = "two.txt" });
        var id2 = (await Body(r2)).GetProperty("id").GetString()!;
        hook.Disarm();

        Assert.Equal("completed", (await WaitTerminal(c, id1)).GetProperty("status").GetString());
        var second = await WaitTerminal(c, id2);
        Assert.Equal("cancelled", second.GetProperty("status").GetString());
        Assert.StartsWith("Skipped:", second.GetProperty("error").GetString());
        Assert.Equal(1, (await History(c, "Test_q1")).GetProperty("total").GetInt32());
    }

    // ---- persistence, delete, reads ----

    [Fact]
    public async Task History_SurvivesAHostRestart_OnTheSameRoot()
    {
        using var f = new CrackDbWebFactory();
        var c = await f.NewLoggedInClientAsync();
        await Upload(c, "Test_p1", Lines("per", 300));

        // A second host on the same data root has no in-memory state (no jobs, a fresh history service).
        using var f2 = f.WithWebHostBuilder(_ => { });
        var c2 = f2.CreateClient(new() { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false, HandleCookies = true });
        Assert.Equal(HttpStatusCode.NoContent,
            (await c2.PostAsJsonAsync("/login", new { username = CrackDbWebFactory.Username, password = CrackDbWebFactory.Password })).StatusCode);
        var h = await History(c2, "Test_p1");
        Assert.Equal(1, h.GetProperty("total").GetInt32());
        Assert.Empty((await Body(await c2.GetAsync("/api/jobs"))).GetProperty("jobs").EnumerateArray());
    }

    [Fact]
    public async Task DeleteDb_RemovesTheSidecar_AndARecreatedDbHasEmptyHistory()
    {
        using var f = new CrackDbWebFactory();
        var c = await f.NewLoggedInClientAsync();
        var data = Lines("del", 300);
        await Upload(c, "Test_del", data);
        Assert.True(File.Exists(Sidecar(f, "Test_del")));

        Assert.Equal(HttpStatusCode.NoContent, (await c.DeleteAsync("/api/dbs/Test_del")).StatusCode);
        Assert.False(File.Exists(Sidecar(f, "Test_del")));
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/api/dbs/Test_del/imports")).StatusCode);

        Assert.Equal(HttpStatusCode.Created, (await c.PostAsJsonAsync("/api/dbs", new { name = "Test_del" })).StatusCode);
        Assert.Equal(0, (await History(c, "Test_del")).GetProperty("total").GetInt32());
        Assert.Equal(HttpStatusCode.Accepted, (await Upload(c, "Test_del", data)).Item1);   // same bytes are new again
    }

    [Fact]
    public async Task HistoryGet_DuringARunningImport_Returns200_NotBusy()
    {
        var hook = new BlockingHook();
        using var f = new CrackDbWebFactory(1000, null, null, hook);
        var c = await f.NewLoggedInClientAsync();
        await Upload(c, "Test_busy2", Lines("first", 300));
        hook.Arm();
        var r = await c.PostAsync("/api/jobs/upload", Form("Test_busy2", Lines("second", 5000)));
        var id = (await Body(r)).GetProperty("id").GetString()!;
        Assert.True(hook.Reached.Wait(TimeSpan.FromSeconds(30)));

        Assert.Equal(HttpStatusCode.Conflict, (await c.GetAsync("/api/dbs/Test_busy2/stats")).StatusCode);   // reads are busy...
        Assert.Equal(1, (await History(c, "Test_busy2")).GetProperty("total").GetInt32());                    // ...history is not
        var check = await c.PostAsJsonAsync("/api/dbs/Test_busy2/imports/check", new { sha256 = Sha(Lines("first", 300)) });
        Assert.Equal(HttpStatusCode.OK, check.StatusCode);
        hook.Disarm();
        await WaitTerminal(c, id);
    }

    // ---- endpoints: auth, validation, clamping, check ----

    [Fact]
    public async Task Endpoints_Need401WithoutAuth_400ForBadName_404ForMissingDb()
    {
        using var f = new CrackDbWebFactory();
        var anon = f.NewClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync($"/api/dbs/{CrackDbWebFactory.DbName}/imports")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anon.PostAsJsonAsync($"/api/dbs/{CrackDbWebFactory.DbName}/imports/check", new { sha256 = Abc })).StatusCode);

        var c = await f.NewLoggedInClientAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await c.GetAsync("/api/dbs/bad%20name/imports")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.GetAsync("/api/dbs/-dash/imports")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/api/dbs/Test_nothere/imports")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await c.PostAsJsonAsync("/api/dbs/Test_nothere/imports/check", new { sha256 = Abc })).StatusCode);
        // An existing db with no history is simply empty.
        Assert.Equal(0, (await History(c, CrackDbWebFactory.DbName)).GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task HistoryList_ClampsLimitAndOffset_NewestFirst()
    {
        using var f = new CrackDbWebFactory();
        var c = await f.NewLoggedInClientAsync();
        for (int i = 0; i < 3; i++) await Upload(c, "Test_l1", Lines("lim" + i + "_", 50), "f" + i + ".txt");
        var all = await History(c, "Test_l1", "?limit=100000&offset=-5");
        Assert.Equal(3, all.GetProperty("entries").GetArrayLength());
        Assert.Equal("upload:f2.txt", all.GetProperty("entries")[0].GetProperty("source").GetString());
        Assert.Equal(1, (await History(c, "Test_l1", "?limit=0")).GetProperty("entries").GetArrayLength());   // clamped up to 1
        var page = await History(c, "Test_l1", "?limit=1&offset=1");
        Assert.Equal(3, page.GetProperty("total").GetInt32());
        Assert.Equal("upload:f1.txt", page.GetProperty("entries")[0].GetProperty("source").GetString());
        Assert.Equal(0, (await History(c, "Test_l1", "?offset=99")).GetProperty("entries").GetArrayLength());
    }

    [Fact]
    public async Task Check_ByHash_ValidatesInput_AndReportsDuplicates()
    {
        using var f = new CrackDbWebFactory();
        var c = await f.NewLoggedInClientAsync();
        var data = Lines("chk", 200);
        await Upload(c, "Test_k1", data, "known.txt");
        string url = "/api/dbs/Test_k1/imports/check";

        var hit = await Body(await c.PostAsJsonAsync(url, new { sha256 = Sha(data).ToUpperInvariant() }));
        Assert.False(hit.GetProperty("advisory").GetBoolean());
        Assert.Equal("upload:known.txt", hit.GetProperty("duplicate").GetProperty("source").GetString());
        var miss = await Body(await c.PostAsJsonAsync(url, new { sha256 = Sha(Lines("other", 5)) }));
        Assert.Equal(JsonValueKind.Null, miss.GetProperty("duplicate").ValueKind);
        var wider = await Body(await c.PostAsJsonAsync(url, new { sha256 = Sha(data), min = 3 }));   // earlier import was unbounded
        Assert.Equal(JsonValueKind.Object, wider.GetProperty("duplicate").ValueKind);

        foreach (var bad in new object[]
        {
            new { sha256 = "zz" }, new { sha256 = Sha(data) + "00" }, new { sha256 = "../../etc/passwd" },
            new { sha256 = Sha(data), min = -1 }, new { sha256 = Sha(data), min = 5, max = 2 }, new { name = "x.txt" }, new { },
        })
            Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync(url, bad)).StatusCode);
    }

    [Fact]
    public async Task Check_ByNameAndSize_IsAdvisory()
    {
        using var f = new CrackDbWebFactory();
        var c = await f.NewLoggedInClientAsync();
        var data = Lines("adv", 200);
        await Upload(c, "Test_k2", data, "known.txt");
        string url = "/api/dbs/Test_k2/imports/check";

        var hit = await Body(await c.PostAsJsonAsync(url, new { name = "known.txt", size = data.Length }));
        Assert.True(hit.GetProperty("advisory").GetBoolean());
        Assert.Equal(JsonValueKind.Object, hit.GetProperty("duplicate").ValueKind);
        var otherSize = await Body(await c.PostAsJsonAsync(url, new { name = "known.txt", size = data.Length + 1 }));
        Assert.Equal(JsonValueKind.Null, otherSize.GetProperty("duplicate").ValueKind);
        Assert.Equal(JsonValueKind.Object, otherSize.GetProperty("changed").ValueKind);   // same name, other size: probably changed
        var otherName = await Body(await c.PostAsJsonAsync(url, new { name = "nope.txt", size = data.Length }));
        Assert.Equal(JsonValueKind.Null, otherName.GetProperty("duplicate").ValueKind);
    }

    // ---- sidecar robustness ----

    [Fact]
    public async Task CorruptSidecar_IsMovedAside_AndImportsContinue()
    {
        using var f = new CrackDbWebFactory();
        var c = await f.NewLoggedInClientAsync();
        await Upload(c, "Test_z1", Lines("one", 100));
        File.WriteAllText(Sidecar(f, "Test_z1"), "{ not json ]]]");

        Assert.Equal(0, (await History(c, "Test_z1")).GetProperty("total").GetInt32());
        Assert.Single(Directory.GetFiles(Path.Combine(f.Root, "Test_z1"), ImportHistory.FileName + ".corrupt-*"));

        var (st, _, job) = await Upload(c, "Test_z1", Lines("two", 100));
        Assert.Equal(HttpStatusCode.Accepted, st);
        Assert.Equal("completed", job!.Value.GetProperty("status").GetString());
        Assert.Equal(1, (await History(c, "Test_z1")).GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task NoAbsolutePathsInAnyResponse_AndNoLeftoverTmpFiles()
    {
        using var f = new CrackDbWebFactory();
        var c = await f.NewLoggedInClientAsync();
        File.WriteAllBytes(Path.Combine(f.Incoming, "p.txt"), Lines("path", 100));
        var texts = new List<string>();
        var up = Lines("path_up", 100);
        var r1 = await c.PostAsync("/api/jobs/upload", Form("Test_np", up, "../../etc/pass wd.txt"));
        texts.Add(await r1.Content.ReadAsStringAsync());
        await WaitTerminal(c, (await Body(r1)).GetProperty("id").GetString()!);
        var r2 = await c.PostAsync("/api/jobs/upload", Form("Test_np", up, "again.txt"));   // 409
        texts.Add(await r2.Content.ReadAsStringAsync());
        var r3 = await c.PostAsJsonAsync("/api/jobs/import", new { db = "Test_np", source = "p.txt" });
        var id3 = (await Body(r3)).GetProperty("id").GetString()!;
        texts.Add((await WaitTerminal(c, id3)).GetRawText());
        var r4 = await c.PostAsJsonAsync("/api/jobs/import", new { db = "Test_np", source = "p.txt" });   // skipped
        texts.Add((await WaitTerminal(c, (await Body(r4)).GetProperty("id").GetString()!)).GetRawText());
        texts.Add(await c.GetStringAsync("/api/dbs/Test_np/imports"));
        texts.Add(await (await c.PostAsJsonAsync("/api/dbs/Test_np/imports/check", new { sha256 = Sha(up) })).Content.ReadAsStringAsync());

        foreach (var t in texts)
        {
            Assert.DoesNotContain(f.Root, t);
            Assert.DoesNotContain(f.Incoming, t);
            Assert.DoesNotContain(Path.GetTempPath().TrimEnd('/'), t);
        }
        Assert.Contains("upload:pass wd.txt", texts[0] + await c.GetStringAsync("/api/dbs/Test_np/imports"));   // sanitised base name only
        Assert.Empty(Directory.GetFiles(Path.Combine(f.Root, "Test_np"), "*.tmp"));
        Assert.Empty(Directory.GetFiles(Path.Combine(f.Root, "Test_np"), ".crackdb-import-history.*"));
    }

    // ---- the sidecar must be invisible to the engine and to listing ----

    [Fact]
    public void Sidecar_IsIgnoredByRocksDbReopen_IsDatabaseDir_AndList()
    {
        var root = Path.Combine(Path.GetTempPath(), "Test_" + Guid.NewGuid());
        CrackDbWebFactory.EnsureThrowaway(root);
        Directory.CreateDirectory(root);
        try
        {
            var dbDir = Path.Combine(root, "Test_side");
            CrackDbWebFactory.Seed(dbDir, ["alpha", "beta", "gamma"]);
            var resolver = new DataRootResolver(root);
            var history = new ImportHistory(resolver, NullLogger<ImportHistory>.Instance);
            history.Append("Test_side", Rec("job1", "completed"));
            Assert.True(File.Exists(Path.Combine(dbDir, ImportHistory.FileName)));

            // Reopen the engine with the sidecar present: open, read, write more, compact, close, reopen again.
            for (int round = 0; round < 2; round++)
            {
                using var db = new WordlistDatabase(dbDir, new UTF8Encoding(false));
                Assert.True(db.Contains("alpha"));
                db.Import(["delta" + round], null, null, threads: 2);
                db.Compact();
                Assert.True(db.Contains("delta" + round));
            }
            Assert.True(File.Exists(Path.Combine(dbDir, ImportHistory.FileName)));   // RocksDB did not touch it
            Assert.True(DataRootResolver.IsDatabaseDir(dbDir));
            Assert.Equal(new[] { "Test_side" }, resolver.List().ToArray());
            Assert.Equal(1, history.List("Test_side", 50, 0).Total);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    // ---- service unit tests ----

    private static ImportRecord Rec(string job, string status, string? sha = null, string source = "upload:a.txt") =>
        new(Guid.NewGuid().ToString("N"), job, source, sha ?? Abc, 3, DateTimeOffset.UtcNow, null, status, null, null, 1, 0, 1, false);

    private static (string root, ImportHistory h, string dir) Fixture(string name = "Test_unit")
    {
        var root = Path.Combine(Path.GetTempPath(), "Test_" + Guid.NewGuid());
        CrackDbWebFactory.EnsureThrowaway(root);
        Directory.CreateDirectory(root);
        var dir = Path.Combine(root, name);
        CrackDbWebFactory.Seed(dir, ["x"]);
        return (root, new ImportHistory(new DataRootResolver(root), NullLogger<ImportHistory>.Instance), dir);
    }

    [Fact]
    public void Service_CapsAtMaxEntries_DroppingTheOldest()
    {
        var (root, h, dir) = Fixture();
        try
        {
            // Pre-fill the file directly (appending thousands of times would rewrite it thousands of times).
            var sb = new StringBuilder("{\"v\":1,\"entries\":[");
            for (int i = 0; i < ImportHistory.MaxEntries - 2; i++)
                sb.Append(i == 0 ? "" : ",").Append($"{{\"id\":\"i{i}\",\"jobId\":\"job{i}\",\"source\":\"s\",\"sha256\":\"{Abc}\",\"status\":\"completed\"}}");
            File.WriteAllText(Path.Combine(dir, ImportHistory.FileName), sb.Append("]}").ToString());
            Assert.Equal(ImportHistory.MaxEntries - 2, h.List("Test_unit", 1, 0).Total);

            for (int i = ImportHistory.MaxEntries - 2; i < ImportHistory.MaxEntries + 3; i++) h.Append("Test_unit", Rec("job" + i, "completed"));
            var page = h.List("Test_unit", 200, 0);
            Assert.Equal(ImportHistory.MaxEntries, page.Total);
            Assert.Equal("job" + (ImportHistory.MaxEntries + 2), page.Entries[0].JobId);          // newest first
            Assert.Equal("job3", h.List("Test_unit", 1, ImportHistory.MaxEntries - 1).Entries[0].JobId);   // oldest kept; job0..2 dropped
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Service_NeverCreatesDirectories_WhenTheDbIsGone()
    {
        var (root, h, _) = Fixture();
        try
        {
            h.Append("Test_missing", Rec("j", "completed"));                     // no such db
            Assert.False(Directory.Exists(Path.Combine(root, "Test_missing")));
            h.Append("../escape", Rec("j", "completed"));                        // invalid name: dropped, no throw
            Assert.Equal(new[] { "Test_unit" }, Directory.EnumerateDirectories(root).Select(d => Path.GetFileName(d)!).ToArray());
            Assert.Equal(0, h.List("Test_missing", 10, 0).Total);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Service_RefusesASymlinkedSidecar()
    {
        if (OperatingSystem.IsWindows()) return;
        var (root, h, dir) = Fixture();
        try
        {
            var target = Path.Combine(root, "elsewhere.json");
            File.WriteAllText(target, "UNTOUCHED");
            File.CreateSymbolicLink(Path.Combine(dir, ImportHistory.FileName), target);
            h.Append("Test_unit", Rec("j", "completed"));
            Assert.Equal("UNTOUCHED", File.ReadAllText(target));
            Assert.Equal(0, h.List("Test_unit", 10, 0).Total);                   // a symlinked sidecar is never read either
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Service_WritesTheSidecarWithMode0600_AndLeavesNoTmpFile()
    {
        var (root, h, dir) = Fixture();
        try
        {
            h.Append("Test_unit", Rec("j1", "completed"));
            h.Append("Test_unit", Rec("j2", "completed"));
            if (!OperatingSystem.IsWindows())
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(dir, ImportHistory.FileName)));
            Assert.Empty(Directory.GetFiles(dir, "*.tmp"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Service_IgnoresInvalidEntries_AndOnlyCompletedRecordsCountAsDuplicates()
    {
        var (root, h, dir) = Fixture();
        try
        {
            h.Append("Test_unit", Rec("c", "cancelled"));
            h.Append("Test_unit", Rec("f", "failed"));
            Assert.Null(h.Check("Test_unit", Abc, "upload:a.txt", null, null).Duplicate);
            h.Append("Test_unit", Rec("ok", "completed"));
            Assert.Equal("ok", h.Check("Test_unit", Abc.ToUpperInvariant(), "upload:a.txt", null, null).Duplicate!.JobId);

            // A hand-edited file with a bad hash or status is filtered on load, not trusted.
            File.WriteAllText(Path.Combine(dir, ImportHistory.FileName),
                """{"v":1,"entries":[{"id":"1","jobId":"j","source":"s","sha256":"../../x","status":"completed"},{"id":"2","jobId":"j","source":"s","sha256":"%ABC%","status":"weird"}]}""".Replace("%ABC%", Abc));
            Assert.Equal(0, h.List("Test_unit", 10, 0).Total);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData(null, null, null, null, true)]
    [InlineData(4, 8, 5, 6, true)]
    [InlineData(4, 8, 4, 8, true)]
    [InlineData(4, 8, 3, 8, false)]
    [InlineData(4, 8, 4, 9, false)]
    [InlineData(4, 8, null, null, false)]
    [InlineData(null, 8, 0, 8, true)]
    [InlineData(null, 8, 2, null, false)]
    [InlineData(4, null, 5, null, true)]
    [InlineData(4, null, 5, 100, true)]
    public void Covers_Rules(int? pmin, int? pmax, int? min, int? max, bool expected)
    {
        var rec = Rec("j", "completed") with { Min = pmin, Max = pmax };
        Assert.Equal(expected, ImportHistory.Covers(rec, min, max));
    }

    // ---- backfill ----

    private static object Entry(string name, byte[] content, DateTimeOffset? at = null) =>
        new { name, sha256 = Sha(content), sizeBytes = content.Length, importedAtUtc = at ?? DateTimeOffset.UtcNow };

    [Fact]
    public async Task Backfill_HappyPath_RecordsBackfilledEntries_UnfilteredAndUnauthoritative()
    {
        using var f = new CrackDbWebFactory();
        var c = await f.NewLoggedInClientAsync();
        var a = Lines("bfa", 10); var b = Lines("bfb", 20);
        var at = new DateTimeOffset(2024, 1, 15, 9, 0, 0, TimeSpan.Zero);
        Assert.Equal(HttpStatusCode.Created, (await c.PostAsJsonAsync("/api/dbs", new { name = "Test_bf1" })).StatusCode);
        var r = await c.PostAsJsonAsync("/api/dbs/Test_bf1/imports/backfill", new[] { Entry("old-a.txt", a, at), Entry("old-b.txt", b) });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var body = await Body(r);
        Assert.Equal(2, body.GetProperty("written").GetInt32());
        Assert.Equal(2, body.GetProperty("total").GetInt32());
        Assert.Empty(body.GetProperty("skipped").EnumerateArray());

        var h = await History(c, "Test_bf1");
        Assert.Equal(2, h.GetProperty("total").GetInt32());
        var entries = h.GetProperty("entries").EnumerateArray().ToList();   // newest first
        Assert.All(entries, e => Assert.Equal("backfilled", e.GetProperty("status").GetString()));
        Assert.All(entries, e => Assert.Equal(JsonValueKind.Null, e.GetProperty("min").ValueKind));   // unfiltered
        Assert.All(entries, e => Assert.Equal(JsonValueKind.Null, e.GetProperty("max").ValueKind));
        var fromA = entries.First(e => e.GetProperty("sha256").GetString() == Sha(a));
        Assert.Equal("backfill:old-a.txt", fromA.GetProperty("source").GetString());
        Assert.Equal(at, fromA.GetProperty("importedAt").GetDateTimeOffset());

        // Unfiltered, so even a narrower later real import is seen as already-done (the plan's "unfiltered" decision).
        var (st, dup, _) = await Upload(c, "Test_bf1", a, min: "2", max: "4");
        Assert.Equal(HttpStatusCode.Conflict, st);
        Assert.Equal("backfill:old-a.txt", dup.GetProperty("duplicateOf").GetProperty("source").GetString());
    }

    [Fact]
    public async Task Backfill_DuplicateSha256_IsSkippedNotError_AgainstExistingAndWithinTheSameBatch()
    {
        using var f = new CrackDbWebFactory();
        var c = await f.NewLoggedInClientAsync();
        var data = Lines("bfdup", 15);
        await Upload(c, "Test_bf2", data, "real.txt");   // already a completed real import; this also creates the db

        var r = await c.PostAsJsonAsync("/api/dbs/Test_bf2/imports/backfill",
            new[] { Entry("dup1.txt", data), Entry("dup1-again.txt", data), Entry("new.txt", Lines("fresh", 5)) });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var body = await Body(r);
        Assert.Equal(1, body.GetProperty("written").GetInt32());
        Assert.Equal(3, body.GetProperty("total").GetInt32());
        var skipped = body.GetProperty("skipped").EnumerateArray().ToList();
        Assert.Equal(2, skipped.Count);
        Assert.All(skipped, s => Assert.Equal(Sha(data), s.GetProperty("sha256").GetString()));
        Assert.All(skipped, s => Assert.Contains("already imported", s.GetProperty("reason").GetString()));

        // Still just the one real import plus the one genuinely new backfilled entry.
        Assert.Equal(2, (await History(c, "Test_bf2")).GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Backfill_BadSha256_RejectsTheWholeBatch_NamingTheIndex()
    {
        using var f = new CrackDbWebFactory();
        var c = await f.NewLoggedInClientAsync();
        Assert.Equal(HttpStatusCode.Created, (await c.PostAsJsonAsync("/api/dbs", new { name = "Test_bf3" })).StatusCode);
var good = Entry("good.txt", Lines("good", 5));
        var r = await c.PostAsJsonAsync("/api/dbs/Test_bf3/imports/backfill",
            new[] { good, new { name = "bad.txt", sha256 = "not-hex", sizeBytes = 1, importedAtUtc = DateTimeOffset.UtcNow } });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Contains("entry 1", (await Body(r)).GetProperty("detail").GetString());
        Assert.Equal(0, (await History(c, "Test_bf3")).GetProperty("total").GetInt32());   // nothing written from a rejected batch
    }

    [Fact]
    public async Task Backfill_BatchExceedingTheCap_IsRejectedWhole_NotTruncated()
    {
        using var f = new CrackDbWebFactory();
        var c = await f.NewLoggedInClientAsync();
        Assert.Equal(HttpStatusCode.Created, (await c.PostAsJsonAsync("/api/dbs", new { name = "Test_bfcap" })).StatusCode);

        // Pre-fill the sidecar directly (an API round-trip per entry would be far slower than the cap itself).
        var dir = Path.Combine(f.Root, "Test_bfcap");
        var sb = new StringBuilder("{\"v\":1,\"entries\":[");
        for (int i = 0; i < ImportHistory.MaxEntries - 1; i++)
            sb.Append(i == 0 ? "" : ",").Append($"{{\"id\":\"i{i}\",\"jobId\":\"job{i}\",\"source\":\"s\",\"sha256\":\"{SeqHash(i)}\",\"status\":\"completed\"}}");
        File.WriteAllText(Path.Combine(dir, ImportHistory.FileName), sb.Append("]}").ToString());
        Assert.Equal(ImportHistory.MaxEntries - 1, (await History(c, "Test_bfcap")).GetProperty("total").GetInt32());

        var batch = Enumerable.Range(0, 3).Select(i => Entry("cap" + i + ".txt", Lines("cap" + i + "_", 3))).ToArray();
        var r = await c.PostAsJsonAsync("/api/dbs/Test_bfcap/imports/backfill", batch);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, r.StatusCode);
        Assert.Equal(ImportHistory.MaxEntries - 1, (await History(c, "Test_bfcap")).GetProperty("total").GetInt32());   // untouched

        // One entry fits exactly at the cap.
        var r2 = await c.PostAsJsonAsync("/api/dbs/Test_bfcap/imports/backfill", new[] { batch[0] });
        Assert.Equal(HttpStatusCode.OK, r2.StatusCode);
        Assert.Equal(ImportHistory.MaxEntries, (await History(c, "Test_bfcap")).GetProperty("total").GetInt32());
    }

    private static string SeqHash(int i) => Convert.ToHexStringLower(SHA256.HashData(BitConverter.GetBytes(i)));

    [Fact]
    public async Task Backfill_NameWithPathSeparators_IsStrippedToTheBaseName()
    {
        using var f = new CrackDbWebFactory();
        var c = await f.NewLoggedInClientAsync();
        var data = Lines("bfpath", 5);
        Assert.Equal(HttpStatusCode.Created, (await c.PostAsJsonAsync("/api/dbs", new { name = "Test_bf4" })).StatusCode);
        var r = await c.PostAsJsonAsync("/api/dbs/Test_bf4/imports/backfill",
            new[] { Entry("../../etc/sneaky.txt", data) });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var h = await History(c, "Test_bf4");
        var source = h.GetProperty("entries")[0].GetProperty("source").GetString()!;
        Assert.Equal("backfill:sneaky.txt", source);
        Assert.DoesNotContain("..", source);
        Assert.DoesNotContain("/", source.Replace("backfill:", ""));
    }

    [Fact]
    public async Task Backfill_RequiresAuth_404ForMissingDb()
    {
        using var f = new CrackDbWebFactory();
        var anon = f.NewClient();
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anon.PostAsJsonAsync($"/api/dbs/{CrackDbWebFactory.DbName}/imports/backfill", new[] { Entry("a.txt", Lines("a", 1)) })).StatusCode);

        var c = await f.NewLoggedInClientAsync();
        Assert.Equal(HttpStatusCode.NotFound,
            (await c.PostAsJsonAsync("/api/dbs/Test_nothere/imports/backfill", new[] { Entry("a.txt", Lines("a", 1)) })).StatusCode);
    }

    [Theory]
    [InlineData(null, null, true)]    // unbounded backfill covers an unbounded later import: duplicate
    [InlineData(2, 6, true)]          // ...and a narrower one: still a duplicate
    [InlineData(null, 3, true)]
    public void Backfill_IsUnfiltered_SoItCoversAnyLaterRange(int? min, int? max, bool expectDuplicate)
    {
        var (root, h, _) = Fixture();
        try
        {
            h.Append("Test_unit", Rec("bf", "backfilled") with { Min = null, Max = null });
            var c = h.Check("Test_unit", Abc, "upload:a.txt", min, max);
            Assert.Equal(expectDuplicate, c.Duplicate != null);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}

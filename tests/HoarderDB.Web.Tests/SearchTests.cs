using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HoarderDB.Web.Jobs;
using HoarderDB.Web.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace HoarderDB.Web.Tests;

/// <summary>Blocks a running search right after a chunk's lease is released, until released.</summary>
public sealed class BlockingSearchHook : ISearchProgressHook
{
    private volatile bool _armed;
    public ManualResetEventSlim Reached { get; private set; } = new(false);
    public ManualResetEventSlim Release { get; private set; } = new(false);

    public void Arm() { Reached = new(false); Release = new(false); _armed = true; }
    public void Disarm() { _armed = false; Release.Set(); }

    public void OnChunk(string dbName, long scannedCount)
    {
        if (!_armed) return;
        var (reached, release) = (Reached, Release);
        reached.Set();
        release.Wait(TimeSpan.FromSeconds(60));
    }
}

/// <summary>Fake free-space probe: reports a settable number of free bytes.</summary>
public sealed class FakeDisk : IDiskSpace
{
    public long? Free = null;
    public long RecheckIntervalBytes => 32L * 1024 * 1024;
    public long? FreeBytes(string dir) => Free;
}

public class SearchTests
{
    private static async Task<JsonElement> Body(HttpResponseMessage r) =>
        JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.Clone();

    private static Task<HttpResponseMessage> Search(HttpClient c, string db, string query,
        bool ic = false, bool contains = false) =>
        c.PostAsJsonAsync($"/api/dbs/{db}/search", new { query, ignoreCase = ic, contains });

    /// <summary>Starts a scanning search, expects 202, returns the job id.</summary>
    private static async Task<string> Start(HttpClient c, string db, string query,
        bool ic = false, bool contains = false)
    {
        var r = await Search(c, db, query, ic, contains);
        Assert.Equal(HttpStatusCode.Accepted, r.StatusCode);
        return (await Body(r)).GetProperty("id").GetString()!;
    }

    private static async Task<JsonElement> Get(HttpClient c, string id) =>
        await Body(await c.GetAsync("/api/jobs/" + id));

    /// <summary>All matches of a job, paged through GET /api/jobs/{id}/matches (the snapshot carries none).</summary>
    private static async Task<string[]> Matches(HttpClient c, string id, int limit = 5000)
    {
        var all = new List<string>();
        while (true)
        {
            var page = await Body(await c.GetAsync($"/api/jobs/{id}/matches?after={all.Count}&limit={limit}"));
            var items = page.GetProperty("items").EnumerateArray().Select(x => x.GetString()!).ToList();
            if (items.Count == 0) return all.ToArray();
            all.AddRange(items);
            Assert.Equal(all.Count, page.GetProperty("next").GetInt64());
        }
    }

    private static string[] Sorted(IEnumerable<string> x) => x.OrderBy(v => v, StringComparer.Ordinal).ToArray();

    private static async Task<JsonElement> WaitStatus(HttpClient c, string id, params string[] statuses)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            var j = await Get(c, id);
            if (statuses.Contains(j.GetProperty("status").GetString())) return j;
            await Task.Delay(25);
        }
        throw new TimeoutException($"job {id} did not reach {string.Join('|', statuses)}");
    }

    /// <summary>Forces a lease segment per stride (256 keys) and restores the knob on dispose.</summary>
    private static IDisposable ShortSegments() => new ShortSegmentsScope(TimeSpan.Zero);

    internal sealed class ShortSegmentsScope : IDisposable
    {
        private readonly TimeSpan _old = SearchRunner.MaxLeaseAge;
        public ShortSegmentsScope(TimeSpan age) { SearchRunner.MaxLeaseAge = age; }
        public void Dispose() => SearchRunner.MaxLeaseAge = _old;
    }

    private static string SpoolFile(HoarderDbWebFactory f, string id) => Path.Combine(f.Root, ".search-tmp", id + ".matches");

    private static string[] PasswordWords => new[]
    {
        "password1", "password123", "xpassword1", "PASSWORD1", "nomatch", "short", "pw", "Pass_word", "PassXword",
    };

    // ---- exact: GET /lookup (point read), never a search job ----

    [Fact]
    public async Task Exact_IsServedByLookup_AndPostWithNoFlagsIs400()
    {
        using var f = new HoarderDbWebFactory();
        HoarderDbWebFactory.Seed(Path.Combine(f.Root, "Test_exact"), PasswordWords);
        var c = await f.NewLoggedInClientAsync();

        var hit = await Body(await c.GetAsync("/api/dbs/Test_exact/lookup?word=password1"));
        Assert.True(hit.GetProperty("found").GetBoolean());
        var miss = await Body(await c.GetAsync("/api/dbs/Test_exact/lookup?word=Password1"));   // case-sensitive
        Assert.False(miss.GetProperty("found").GetBoolean());

        var r = await Search(c, "Test_exact", "password1");   // no flags
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    // ---- ignore case (exact): scans ONE length bucket only ----

    [Fact]
    public async Task IgnoreCaseExact_MatchesCaseVariantsOnly_AndScansOnlyItsLengthBucket()
    {
        using var f = new HoarderDbWebFactory();
        HoarderDbWebFactory.Seed(Path.Combine(f.Root, "Test_ic"), PasswordWords);
        var c = await f.NewLoggedInClientAsync();

        var resp = await Search(c, "Test_ic", "password1", ic: true);
        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
        var started = await Body(resp);
        Assert.Equal(9, started.GetProperty("min").GetInt32());   // the one length bucket scanned
        Assert.Equal(9, started.GetProperty("max").GetInt32());
        var id = started.GetProperty("id").GetString()!;
        var j = await WaitStatus(c, id, "completed");
        Assert.Equal(Sorted(new[] { "PASSWORD1", "password1" }), Sorted(await Matches(c, id)));
        // Length-9 words in the seed: password1, PASSWORD1, Pass_word, PassXword => the whole bucket, nothing else.
        Assert.Equal(4, j.GetProperty("scannedCount").GetInt64());
    }

    // ---- contains: case-sensitive and insensitive ----

    [Fact]
    public async Task Contains_CaseSensitive_VsIgnoreCase()
    {
        using var f = new HoarderDbWebFactory();
        HoarderDbWebFactory.Seed(Path.Combine(f.Root, "Test_cs"), PasswordWords);
        var c = await f.NewLoggedInClientAsync();

        var sens = await Start(c, "Test_cs", "assword1", contains: true);
        await WaitStatus(c, sens, "completed");
        Assert.Equal(Sorted(new[] { "password1", "password123", "xpassword1" }), Sorted(await Matches(c, sens)));

        var insens = await Start(c, "Test_cs", "assword1", ic: true, contains: true);
        var j = await WaitStatus(c, insens, "completed");
        Assert.Equal(Sorted(new[] { "PASSWORD1", "password1", "password123", "xpassword1" }), Sorted(await Matches(c, insens)));
        Assert.Equal(JsonValueKind.Null, j.GetProperty("matches").ValueKind);   // never inlined in the snapshot
        Assert.Equal(4, j.GetProperty("matchedCount").GetInt64());
    }

    [Fact]
    public async Task Contains_Negative_FindsNothing()
    {
        using var f = new HoarderDbWebFactory();
        HoarderDbWebFactory.Seed(Path.Combine(f.Root, "Test_neg"), PasswordWords);
        var c = await f.NewLoggedInClientAsync();

        var none = await Start(c, "Test_neg", "zzzznotfound", contains: true);
        var j = await WaitStatus(c, none, "completed");
        Assert.Empty(await Matches(c, none));
        Assert.Equal(0, j.GetProperty("matchedCount").GetInt64());
    }

    // ---- no wildcards: % _ \ are ordinary characters ----

    [Fact]
    public async Task PatternCharacters_AreLiteral_AndWildcardsFieldIsIgnored()
    {
        using var f = new HoarderDbWebFactory();
        HoarderDbWebFactory.Seed(Path.Combine(f.Root, "Test_wc"), PasswordWords.Concat(new[] { "50%_off\\x" }));
        var c = await f.NewLoggedInClientAsync();

        var id = await Start(c, "Test_wc", "Pass_word", contains: true);
        await WaitStatus(c, id, "completed");
        Assert.Equal(new[] { "Pass_word" }, await Matches(c, id));   // not PassXword

        id = await Start(c, "Test_wc", "50%_off\\", contains: true);
        await WaitStatus(c, id, "completed");
        Assert.Equal(new[] { "50%_off\\x" }, await Matches(c, id));

        // A leftover `wildcards` field from an old client is ignored, not interpreted.
        var r = await c.PostAsJsonAsync("/api/dbs/Test_wc/search", new { query = "Pass_word", contains = true, wildcards = true });
        Assert.Equal(HttpStatusCode.Accepted, r.StatusCode);
        id = (await Body(r)).GetProperty("id").GetString()!;
        await WaitStatus(c, id, "completed");
        Assert.Equal(new[] { "Pass_word" }, await Matches(c, id));
    }

    // ---- every 400 ----

    [Theory]
    [InlineData("abc", false, true)]        // below the 4-char minimum (contains)
    [InlineData("abc", true, false)]        // below the minimum (ignore-case exact)
    [InlineData("a_%", false, true)]        // 3 characters; % and _ count like any other
    [InlineData("", false, true)]           // empty
    [InlineData("password1", false, false)] // exact: use /lookup
    public async Task InvalidSearch_Is400(string query, bool ic, bool contains)
    {
        using var f = new HoarderDbWebFactory();
        var c = await f.NewLoggedInClientAsync();
        var r = await Search(c, HoarderDbWebFactory.DbName, query, ic, contains);
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task InvalidSearch_OtherFields_Are400()
    {
        using var f = new HoarderDbWebFactory();
        var c = await f.NewLoggedInClientAsync();
        var db = HoarderDbWebFactory.DbName;
        Assert.Equal(HttpStatusCode.BadRequest, (await Search(c, db, new string('a', 257), contains: true)).StatusCode);
        var bad = await c.PostAsync($"/api/dbs/{db}/search", new StringContent("{not json", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        var ok = await Search(c, db, "a_%b", contains: true);   // 4 characters of any kind is fine
        Assert.Equal(HttpStatusCode.Accepted, ok.StatusCode);
        await WaitStatus(c, (await Body(ok)).GetProperty("id").GetString()!, "completed");
    }

    [Fact]
    public async Task UnknownDb_Is404()
    {
        using var f = new HoarderDbWebFactory();
        var c = await f.NewLoggedInClientAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await Search(c, "Test_nope", "abcd", contains: true)).StatusCode);
    }

    [Fact]
    public async Task ExactlyMinimumLength_IsAccepted()
    {
        using var f = new HoarderDbWebFactory();
        var c = await f.NewLoggedInClientAsync();
        var id = await Start(c, HoarderDbWebFactory.DbName, "abcd", contains: true);   // exactly 4 chars
        await WaitStatus(c, id, "completed");
    }

    [Fact]
    public async Task EleventhStartInAMinute_Is429()
    {
        using var f = new HoarderDbWebFactory();
        var c = await f.NewLoggedInClientAsync();
        for (int i = 0; i < 10; i++)
        {
            var id = await Start(c, HoarderDbWebFactory.DbName, "abcd", contains: true);
            await WaitStatus(c, id, "completed");
        }
        Assert.Equal(HttpStatusCode.TooManyRequests, (await Search(c, HoarderDbWebFactory.DbName, "abcd", contains: true)).StatusCode);
    }

    // ---- no ceiling: many matches spooled to disk and paged by offset ----

    [Fact]
    public async Task ManyMatches_ArePagedByOffset_AcrossIndexBoundaries()
    {
        using var f = new HoarderDbWebFactory();
        var words = Enumerable.Range(0, 21000).Select(i => "needle" + i.ToString("D6")).ToArray();   // > old 1,000-line/500 caps, several chunks
        HoarderDbWebFactory.Seed(Path.Combine(f.Root, "Test_many"), words);
        var c = await f.NewLoggedInClientAsync();

        var id = await Start(c, "Test_many", "needle", contains: true);
        var j = await WaitStatus(c, id, "completed");
        Assert.Equal(21000, j.GetProperty("matchedCount").GetInt64());
        Assert.False(j.GetProperty("capped").GetBoolean());
        var all = await Matches(c, id, limit: 3333);
        Assert.Equal(Sorted(words), Sorted(all));

        // The scan order is stable, so any offset must return the same record as in the full list.
        foreach (var off in new[] { 0, 1022, 1023, 1024, 1025, 2047, 2048, 2049, 20999 })
        {
            var page = await Body(await c.GetAsync($"/api/jobs/{id}/matches?after={off}&limit=3"));
            var got = page.GetProperty("items").EnumerateArray().Select(x => x.GetString()!).ToArray();
            Assert.Equal(all.Skip(off).Take(3).ToArray(), got);
            Assert.Equal(21000, page.GetProperty("total").GetInt64());
            Assert.Equal(off + got.Length, page.GetProperty("next").GetInt64());
        }
        var past = await Body(await c.GetAsync($"/api/jobs/{id}/matches?after=21000"));
        Assert.Equal(0, past.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task MatchesPage_IsCappedByBytes_ButReturnsAtLeastOneItem()
    {
        using var f = new HoarderDbWebFactory();
        var words = Enumerable.Range(0, 6).Select(i => i + new string('x', 1_500_000)).ToArray();   // ~1.5 MB each
        HoarderDbWebFactory.Seed(Path.Combine(f.Root, "Test_big"), words);
        var c = await f.NewLoggedInClientAsync();
        var id = await Start(c, "Test_big", "xxxx", contains: true);
        await WaitStatus(c, id, "completed");
        var page = await Body(await c.GetAsync($"/api/jobs/{id}/matches?after=0&limit=10000"));
        var n = page.GetProperty("items").GetArrayLength();
        Assert.InRange(n, 1, 4);   // ~4 MiB cap, not all six
        Assert.Equal(n, page.GetProperty("next").GetInt64());
    }

    [Fact]
    public async Task MatchesEndpoint_UnknownJob_Is404_AndImportJobIs404()
    {
        using var f = new HoarderDbWebFactory();
        var c = await f.NewLoggedInClientAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/api/jobs/nope/matches")).StatusCode);
        File.WriteAllText(Path.Combine(f.Incoming, "x.txt"), "freshword1\n");
        var imp = await c.PostAsJsonAsync("/api/jobs/import", new { db = HoarderDbWebFactory.DbName, source = "x.txt" });
        var importId = (await Body(imp)).GetProperty("id").GetString()!;
        await WaitStatus(c, importId, "completed");
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync($"/api/jobs/{importId}/matches")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync($"/api/jobs/{importId}/matches/download")).StatusCode);
    }

    // ---- download ----

    [Fact]
    public async Task Download_StreamsAllMatchesAsFixedNameFile_AndNeedsAuth()
    {
        using var f = new HoarderDbWebFactory();
        var words = Enumerable.Range(0, 5000).Select(i => "needle" + i.ToString("D5")).ToArray();
        HoarderDbWebFactory.Seed(Path.Combine(f.Root, "Test_dl"), words);
        var c = await f.NewLoggedInClientAsync();
        var id = await Start(c, "Test_dl", "needle", contains: true);
        await WaitStatus(c, id, "completed");

        var r = await c.GetAsync($"/api/jobs/{id}/matches/download");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("text/plain", r.Content.Headers.ContentType!.MediaType);
        Assert.Contains("matches.txt", r.Content.Headers.ContentDisposition!.ToString());
        var lines = (await r.Content.ReadAsStringAsync()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(Sorted(words), Sorted(lines));

        Assert.Equal(HttpStatusCode.Unauthorized, (await f.NewClient().GetAsync($"/api/jobs/{id}/matches/download")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await f.NewClient().GetAsync($"/api/jobs/{id}/matches")).StatusCode);
    }

    [Fact]
    public async Task Download_WhileRunning_Is409()
    {
        var hook = new BlockingSearchHook();
        using var _seg = ShortSegments();   // many tiny lease segments so the hook sees a search that is still mid-scan
        using var f = new HoarderDbWebFactory(1000, null, null, null, hook);
        HoarderDbWebFactory.Seed(Path.Combine(f.Root, "Test_dlrun"), PasswordWords);
        var c = await f.NewLoggedInClientAsync();
        hook.Arm();
        var id = await Start(c, "Test_dlrun", "password", contains: true);
        Assert.True(hook.Reached.Wait(TimeSpan.FromSeconds(30)));
        Assert.Equal(HttpStatusCode.Conflict, (await c.GetAsync($"/api/jobs/{id}/matches/download")).StatusCode);
        hook.Disarm();
        await WaitStatus(c, id, "completed");
    }

    // ---- spool lifetime, location and permissions ----

    [Fact]
    public async Task Spool_LivesUnderDataRootDotDir_NotListedAsDatabase_AndIsOwnerOnly()
    {
        using var f = new HoarderDbWebFactory();
        var c = await f.NewLoggedInClientAsync();
        var id = await Start(c, HoarderDbWebFactory.DbName, "abcd", contains: true);
        await WaitStatus(c, id, "completed");
        var file = SpoolFile(f, id);
        Assert.True(File.Exists(file));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                File.GetUnixFileMode(Path.Combine(f.Root, ".search-tmp")));
        }
        var dbs = await Body(await c.GetAsync("/api/dbs"));
        Assert.DoesNotContain(".search-tmp", dbs.GetProperty("databases").EnumerateArray().Select(x => x.GetString()));
    }

    [Fact]
    public async Task NewSearch_DeletesThePreviousResults_And410() 
    {
        using var f = new HoarderDbWebFactory();
        var c = await f.NewLoggedInClientAsync();
        var first = await Start(c, HoarderDbWebFactory.DbName, "abcd", contains: true);
        await WaitStatus(c, first, "completed");
        Assert.True(File.Exists(SpoolFile(f, first)));
        var second = await Start(c, HoarderDbWebFactory.DbName, "abcd", contains: true);
        await WaitStatus(c, second, "completed");
        Assert.False(File.Exists(SpoolFile(f, first)));
        Assert.True(File.Exists(SpoolFile(f, second)));
        Assert.Equal(HttpStatusCode.Gone, (await c.GetAsync($"/api/jobs/{first}/matches")).StatusCode);
        Assert.Equal(HttpStatusCode.Gone, (await c.GetAsync($"/api/jobs/{first}/matches/download")).StatusCode);
    }

    [Fact]
    public async Task Cancel_DeletesTheSpoolFile()
    {
        var hook = new BlockingSearchHook();
        using var _seg = ShortSegments();   // many tiny lease segments so the hook sees a search that is still mid-scan
        using var f = new HoarderDbWebFactory(1000, null, null, null, hook);
        HoarderDbWebFactory.Seed(Path.Combine(f.Root, "Test_cancel"), PasswordWords);
        var c = await f.NewLoggedInClientAsync();

        hook.Arm();
        var id = await Start(c, "Test_cancel", "password", contains: true);
        Assert.True(hook.Reached.Wait(TimeSpan.FromSeconds(30)));
        Assert.True(File.Exists(SpoolFile(f, id)));
        // Results are readable while the scan is still running.
        Assert.NotEmpty(await Matches(c, id));

        var cancelResp = await c.PostAsync($"/api/jobs/{id}/cancel", null);
        Assert.Equal(HttpStatusCode.Accepted, cancelResp.StatusCode);
        hook.Disarm();
        var j = await WaitStatus(c, id, "cancelled", "completed", "failed");
        Assert.Equal("cancelled", j.GetProperty("status").GetString());
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (File.Exists(SpoolFile(f, id)) && DateTime.UtcNow < deadline) await Task.Delay(25);
        Assert.False(File.Exists(SpoolFile(f, id)));
        Assert.Equal(HttpStatusCode.Gone, (await c.GetAsync($"/api/jobs/{id}/matches")).StatusCode);
    }

    [Fact]
    public async Task Janitor_ExpiresFinishedResultsAfterTtl_AndRemovesOrphans()
    {
        using var f = new HoarderDbWebFactory();
        var c = await f.NewLoggedInClientAsync();
        var spool = f.Services.GetRequiredService<SearchSpool>();
        var id = await Start(c, HoarderDbWebFactory.DbName, "abcd", contains: true);
        await WaitStatus(c, id, "completed");

        var orphan = Path.Combine(f.Root, ".search-tmp", new string('a', 32) + ".matches");
        File.WriteAllText(orphan, "x");
        var keep = Path.Combine(f.Root, ".search-tmp", "notes.txt");   // not a *.matches file: never touched
        File.WriteAllText(keep, "x");

        spool.Sweep(DateTimeOffset.UtcNow.AddMinutes(30));   // within the TTL
        Assert.True(File.Exists(SpoolFile(f, id)));
        Assert.False(File.Exists(orphan));                    // no owning job: removed
        Assert.True(File.Exists(keep));

        spool.Sweep(DateTimeOffset.UtcNow.AddHours(2));       // past the TTL
        Assert.False(File.Exists(SpoolFile(f, id)));
        Assert.Equal(HttpStatusCode.Gone, (await c.GetAsync($"/api/jobs/{id}/matches")).StatusCode);
    }

    [Fact]
    public async Task Startup_WipesOrphanedResultFiles()
    {
        using var f = new HoarderDbWebFactory();
        var dir = Path.Combine(f.Root, ".search-tmp");
        Directory.CreateDirectory(dir);
        var orphan = Path.Combine(dir, new string('b', 32) + ".matches");
        File.WriteAllText(orphan, "left over from a previous run");
        await f.NewLoggedInClientAsync();   // starts the host
        Assert.False(File.Exists(orphan));
    }

    [Fact]
    public async Task SymlinkedSpoolDir_IsRefused()
    {
        if (OperatingSystem.IsWindows()) return;
        using var f = new HoarderDbWebFactory();
        var elsewhere = Path.Combine(Path.GetTempPath(), "Test_" + Guid.NewGuid());
        Directory.CreateDirectory(elsewhere);
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(f.Root, ".search-tmp"), elsewhere);
            var c = await f.NewLoggedInClientAsync();
            var r = await Search(c, HoarderDbWebFactory.DbName, "abcd", contains: true);
            Assert.Equal(HttpStatusCode.InternalServerError, r.StatusCode);
            Assert.Empty(Directory.GetFileSystemEntries(elsewhere));
            // the refusal must not leak the single-search slot
            File.Delete(Path.Combine(f.Root, ".search-tmp"));
            var ok = await Search(c, HoarderDbWebFactory.DbName, "abcd", contains: true);
            Assert.Equal(HttpStatusCode.Accepted, ok.StatusCode);
            await WaitStatus(c, (await Body(ok)).GetProperty("id").GetString()!, "completed");
        }
        finally { Directory.Delete(elsewhere, recursive: true); }
    }

    // ---- disk guard ----

    [Fact]
    public async Task LowDiskAtStart_Is507()
    {
        var disk = new FakeDisk { Free = 1024 };
        using var f = new HoarderDbWebFactory { ExtraServices = s => s.AddSingleton<IDiskSpace>(disk) };
        var c = await f.NewLoggedInClientAsync();
        Assert.Equal(HttpStatusCode.InsufficientStorage, (await Search(c, HoarderDbWebFactory.DbName, "abcd", contains: true)).StatusCode);
        disk.Free = null;   // unknown free space = continue
        var id = await Start(c, HoarderDbWebFactory.DbName, "abcd", contains: true);
        await WaitStatus(c, id, "completed");
    }

    [Fact]
    public async Task DiskDropsBelowReserveMidScan_StopsCappedWithNote_PartialResultsReadable()
    {
        var disk = new FakeDisk { Free = long.MaxValue };
        var hook = new BlockingSearchHook();
        using var _seg = ShortSegments();   // many tiny lease segments so the hook sees a search that is still mid-scan
        using var f = new HoarderDbWebFactory(1000, null, null, null, hook) { ExtraServices = s => s.AddSingleton<IDiskSpace>(disk) };
        HoarderDbWebFactory.Seed(Path.Combine(f.Root, "Test_disk"), Enumerable.Range(0, 12000).Select(i => "needle" + i.ToString("D5")));
        var c = await f.NewLoggedInClientAsync();
        hook.Arm();
        var id = await Start(c, "Test_disk", "needle", contains: true);
        Assert.True(hook.Reached.Wait(TimeSpan.FromSeconds(30)));   // first chunk committed
        disk.Free = SearchSpool.ReserveBytes - 1;
        hook.Disarm();
        var j = await WaitStatus(c, id, "completed");
        Assert.True(j.GetProperty("capped").GetBoolean());
        Assert.Contains("low disk space", j.GetProperty("note").GetString());
        var kept = j.GetProperty("matchedCount").GetInt64();
        Assert.InRange(kept, 1, 11999);
        Assert.Equal(kept, (await Matches(c, id)).Length);
    }

    // ---- one search at a time, globally ----

    [Fact]
    public async Task SecondConcurrentSearch_Is409_ThenSucceedsAfterTheFirstFinishes()
    {
        var hook = new BlockingSearchHook();
        using var _seg = ShortSegments();   // many tiny lease segments so the hook sees a search that is still mid-scan
        using var f = new HoarderDbWebFactory(1000, null, null, null, hook);
        HoarderDbWebFactory.Seed(Path.Combine(f.Root, "Test_s1"), PasswordWords);
        HoarderDbWebFactory.Seed(Path.Combine(f.Root, "Test_s2"), PasswordWords);
        var c = await f.NewLoggedInClientAsync();

        hook.Arm();
        var first = await Start(c, "Test_s1", "password", contains: true);
        Assert.True(hook.Reached.Wait(TimeSpan.FromSeconds(30)));   // the first search is paused mid-scan

        var secondResp = await Search(c, "Test_s2", "password", contains: true);   // a different db: still refused - the gate is global
        Assert.Equal(HttpStatusCode.Conflict, secondResp.StatusCode);
        Assert.True(File.Exists(SpoolFile(f, first)));   // a refused start must not wipe the running search's results

        hook.Disarm();
        await WaitStatus(c, first, "completed");

        var third = await Start(c, "Test_s2", "password", contains: true);   // the gate is free again
        await WaitStatus(c, third, "completed");
    }

    // ---- concurrency: a paused search on db A doesn't starve a same-db import beyond ~one chunk,
    // and never blocks a job on a different db at all ----

    [Fact]
    public async Task PausedSearch_ReleasesItsLeaseBetweenChunks_SameDbImportProceedsPromptly()
    {
        var hook = new BlockingSearchHook();
        using var _seg = ShortSegments();   // many tiny lease segments so the hook sees a search that is still mid-scan
        using var f = new HoarderDbWebFactory(1000, null, null, null, hook);
        HoarderDbWebFactory.Seed(Path.Combine(f.Root, "Test_conc"), PasswordWords);
        var c = await f.NewLoggedInClientAsync();
        File.WriteAllText(Path.Combine(f.Incoming, "more.txt"), "freshword1\nfreshword2\n");

        hook.Arm();
        var searchId = await Start(c, "Test_conc", "password", contains: true);
        Assert.True(hook.Reached.Wait(TimeSpan.FromSeconds(30)));

        var importResp = await c.PostAsJsonAsync("/api/jobs/import", new { db = "Test_conc", source = "more.txt" });
        Assert.Equal(HttpStatusCode.Accepted, importResp.StatusCode);
        var importId = (await Body(importResp)).GetProperty("id").GetString()!;
        var importJob = await WaitStatus(c, importId, "completed", "failed");
        Assert.Equal("completed", importJob.GetProperty("status").GetString());

        Assert.Equal("running", (await Get(c, searchId)).GetProperty("status").GetString());

        hook.Disarm();
        await WaitStatus(c, searchId, "completed");
    }

    [Fact]
    public async Task PausedSearch_NeverBlocksAJobOnADifferentDatabase()
    {
        var hook = new BlockingSearchHook();
        using var _seg = ShortSegments();   // many tiny lease segments so the hook sees a search that is still mid-scan
        using var f = new HoarderDbWebFactory(1000, null, null, null, hook);
        HoarderDbWebFactory.Seed(Path.Combine(f.Root, "Test_a"), PasswordWords);
        var c = await f.NewLoggedInClientAsync();

        hook.Arm();
        var searchId = await Start(c, "Test_a", "password", contains: true);
        Assert.True(hook.Reached.Wait(TimeSpan.FromSeconds(30)));

        var compactResp = await c.PostAsJsonAsync("/api/jobs/compact", new { db = HoarderDbWebFactory.DbName });
        Assert.Equal(HttpStatusCode.Accepted, compactResp.StatusCode);
        await WaitStatus(c, (await Body(compactResp)).GetProperty("id").GetString()!, "completed");

        hook.Disarm();
        await WaitStatus(c, searchId, "completed");
    }

    [Fact]
    public async Task BusyDb_ShowsPausedNote_ThenResumesAndCompletes()
    {
        var oldRetry = SearchRunner.BusyRetry;
        SearchRunner.BusyRetry = TimeSpan.FromMilliseconds(20);
        try
        {
            var shook = new BlockingSearchHook();
            using var _seg = ShortSegments();   // many tiny lease segments so the hook sees a search that is still mid-scan
            var ihook = new BlockingHook();
            using var f = new HoarderDbWebFactory(1000, null, null, ihook, shook);
            var words = Enumerable.Range(0, 9000).Select(i => "needle" + i.ToString("D5")).ToArray();
            HoarderDbWebFactory.Seed(Path.Combine(f.Root, "Test_busy"), words);
            var c = await f.NewLoggedInClientAsync();
            File.WriteAllText(Path.Combine(f.Incoming, "imp.txt"), string.Join('\n', Enumerable.Range(0, 3000).Select(i => "fresh" + i)) + "\n");

            shook.Arm();
            var id = await Start(c, "Test_busy", "needle", contains: true);
            Assert.True(shook.Reached.Wait(TimeSpan.FromSeconds(30)));      // chunk 1 done, lease released
            ihook.Arm();
            var imp = await c.PostAsJsonAsync("/api/jobs/import", new { db = "Test_busy", source = "imp.txt" });
            Assert.Equal(HttpStatusCode.Accepted, imp.StatusCode);
            Assert.True(ihook.Reached.Wait(TimeSpan.FromSeconds(30)));      // the import now holds the write window
            shook.Disarm();                                                  // the search tries its next chunk: db is busy

            var deadline = DateTime.UtcNow.AddSeconds(20);
            string? note = null;
            while (DateTime.UtcNow < deadline && !(note ?? "").StartsWith("Paused"))
            { note = (await Get(c, id)).GetProperty("note").GetString(); await Task.Delay(25); }
            Assert.StartsWith("Paused", note);

            ihook.Disarm();
            var j = await WaitStatus(c, id, "completed", "failed");
            Assert.Equal("completed", j.GetProperty("status").GetString());
            Assert.Equal(JsonValueKind.Null, j.GetProperty("note").ValueKind);   // cleared on resume
            Assert.Equal(9000, j.GetProperty("matchedCount").GetInt64());
        }
        finally { SearchRunner.BusyRetry = oldRetry; }
    }

    [Fact]
    public async Task BusyDbBeyondTimeout_FailsTheSearch_AndFreesTheSlot()
    {
        var (oldRetry, oldTimeout) = (SearchRunner.BusyRetry, SearchRunner.BusyTimeout);
        SearchRunner.BusyRetry = TimeSpan.FromMilliseconds(20);
        SearchRunner.BusyTimeout = TimeSpan.FromMilliseconds(300);
        try
        {
            var shook = new BlockingSearchHook();
            using var _seg = ShortSegments();   // many tiny lease segments so the hook sees a search that is still mid-scan
            var ihook = new BlockingHook();
            using var f = new HoarderDbWebFactory(1000, null, null, ihook, shook);
            HoarderDbWebFactory.Seed(Path.Combine(f.Root, "Test_busy2"), Enumerable.Range(0, 9000).Select(i => "needle" + i.ToString("D5")));
            var c = await f.NewLoggedInClientAsync();
            File.WriteAllText(Path.Combine(f.Incoming, "imp2.txt"), string.Join('\n', Enumerable.Range(0, 3000).Select(i => "fresh" + i)) + "\n");

            shook.Arm();
            var id = await Start(c, "Test_busy2", "needle", contains: true);
            Assert.True(shook.Reached.Wait(TimeSpan.FromSeconds(30)));
            ihook.Arm();
            await c.PostAsJsonAsync("/api/jobs/import", new { db = "Test_busy2", source = "imp2.txt" });
            Assert.True(ihook.Reached.Wait(TimeSpan.FromSeconds(30)));
            shook.Disarm();

            var j = await WaitStatus(c, id, "failed", "completed");
            Assert.Equal("failed", j.GetProperty("status").GetString());
            Assert.Contains("busy", j.GetProperty("error").GetString());
            ihook.Disarm();
            await Task.Delay(200);
            var again = await Start(c, HoarderDbWebFactory.DbName, "abcd", contains: true);   // slot is free again
            await WaitStatus(c, again, "completed");
        }
        finally { (SearchRunner.BusyRetry, SearchRunner.BusyTimeout) = (oldRetry, oldTimeout); }
    }

    // ---- auth ----

    [Fact]
    public async Task SearchRoute_WithoutCredentials_Is401()
    {
        using var f = new HoarderDbWebFactory();
        var r = await f.NewClient().PostAsJsonAsync($"/api/dbs/{HoarderDbWebFactory.DbName}/search", new { query = "abcd", contains = true });
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
    }
}

public class SearchMatcherTests
{
    private static Func<string, bool> M(string q, bool ic = false, bool contains = true) =>
        SearchMatcher.Compile(new SearchSpec(q, ic, contains));

    [Fact]
    public void Exact_IgnoreCase_ComparesWholeWordOnly()
    {
        var m = M("Hunter2", ic: true, contains: false);
        Assert.True(m("hunter2")); Assert.True(m("HUNTER2"));
        Assert.False(m("hunter22")); Assert.False(m("xhunter2"));
    }

    [Fact]
    public void Contains_CaseSensitiveAndInsensitive()
    {
        Assert.True(M("unte")("hunter2")); Assert.False(M("UNTE")("hunter2"));
        Assert.True(M("UNTE", ic: true)("hunter2"));
    }

    [Fact]
    public void PatternCharacters_AreLiteral()
    {
        Assert.True(M("a_c")("xa_cx")); Assert.False(M("a_c")("abc"));
        Assert.True(M("a%c")("a%c")); Assert.False(M("a%c")("abc"));
        Assert.True(M(@"a\c")(@"a\c"));
    }

    [Fact]
    public void EffectiveRange_PerMode()
    {
        Assert.Equal((9, (int?)9), SearchMatcher.EffectiveRange(new SearchSpec("password1", true, false)));
        Assert.Equal((4, (int?)null), SearchMatcher.EffectiveRange(new SearchSpec("abcd", false, true)));
    }
}

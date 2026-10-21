using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using HoarderDB.Web.Jobs;
using HoarderDB.Web.Options;
using HoarderDB.Web.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HoarderDB.Web.Tests;

/// <summary>Pauses (and optionally fails) a breakdown scan at its first tick, so a test can hold it "running".</summary>
public sealed class BreakdownHook : IBreakdownProgressHook
{
    private volatile bool _armed, _fail;
    public ManualResetEventSlim Reached { get; private set; } = new(false);
    public ManualResetEventSlim Release { get; private set; } = new(false);
    public void Arm(bool fail = false) { Reached = new(false); Release = new(false); _fail = fail; _armed = true; }
    public void Disarm() { _armed = false; Release.Set(); }
    public void OnProgress(string dbName, long scannedCount)
    {
        if (!_armed) return;
        var (reached, release) = (Reached, Release);
        reached.Set();
        release.Wait(TimeSpan.FromSeconds(60));
        if (_fail) throw new InvalidOperationException("test failure");
    }
}

/// <summary>Store tests run on a Test_&lt;guid&gt; data root under the OS temp dir.</summary>
public sealed class LengthBreakdownStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Test_" + Guid.NewGuid());
    private readonly DateTimeOffset _now = new(2026, 10, 21, 0, 0, 0, TimeSpan.Zero);
    private readonly LengthBreakdownStore _store;

    public LengthBreakdownStoreTests()
    {
        HoarderDbWebFactory.EnsureThrowaway(_root);
        Directory.CreateDirectory(_root);
        _store = new LengthBreakdownStore(new DataRootResolver(_root), NullLogger<LengthBreakdownStore>.Instance, new FixedTime(_now));
    }

    public void Dispose()
    {
        HoarderDbWebFactory.EnsureThrowaway(_root);
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider { public override DateTimeOffset GetUtcNow() => now; }

    // A db dir is recognised by its CURRENT file; the store never opens RocksDB.
    private string MakeDb(string name)
    {
        var d = Path.Combine(_root, name);
        Directory.CreateDirectory(d);
        File.WriteAllText(Path.Combine(d, "CURRENT"), "x");
        return d;
    }

    private static string FilePath(string dir) => Path.Combine(dir, LengthBreakdownStore.FileName);
    private static List<LengthCount> Sample() => [new(1, 2), new(5, 10)];

    [Fact]
    public void RoundTrip_Is0600_AndReplacesAtomically()
    {
        var dir = MakeDb("Test_a");
        Assert.Null(_store.Load("Test_a"));
        _store.Save("Test_a", _now.AddHours(-1), Sample());
        var b = _store.Load("Test_a")!;
        Assert.Equal(_now.AddHours(-1), b.AsOf);
        Assert.Equal(Sample(), b.Lengths);
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(FilePath(dir)));
        _store.Save("Test_a", _now, [new(3, 4)]);
        Assert.Equal([new LengthCount(3, 4)], _store.Load("Test_a")!.Lengths);
        Assert.Empty(Directory.GetFiles(dir, "*.tmp-*"));
    }

    [Fact]
    public void EmptyList_IsValid_AndIsDistinctFromNone()
    {
        MakeDb("Test_e");
        _store.Save("Test_e", _now, []);
        var b = _store.Load("Test_e");
        Assert.NotNull(b);
        Assert.Empty(b!.Lengths);
    }

    [Fact]
    public void Save_ToMissingDb_Throws_AndCreatesNothing()
    {
        Assert.Throws<DbNotFoundException>(() => _store.Save("Test_gone", _now, Sample()));
        Assert.False(Directory.Exists(Path.Combine(_root, "Test_gone")));
        Directory.CreateDirectory(Path.Combine(_root, "Test_nocurrent"));   // a dir that is not a database
        Assert.Throws<DbNotFoundException>(() => _store.Save("Test_nocurrent", _now, Sample()));
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "Test_nocurrent")));
    }

    [Fact]
    public void BadName_Throws_InvalidDbName()
    {
        Assert.Throws<InvalidDbNameException>(() => _store.Load("../x"));
        Assert.Throws<InvalidDbNameException>(() => _store.Save("..", _now, Sample()));
    }

    [Fact]
    public void Symlink_IsNeitherReadNorWritten()
    {
        if (OperatingSystem.IsWindows()) return;
        var dir = MakeDb("Test_l");
        var real = Path.Combine(_root, "elsewhere.json");
        File.WriteAllText(real, "{\"v\":1,\"asOf\":\"2026-10-20T00:00:00Z\",\"lengths\":[{\"length\":1,\"count\":1}]}");
        File.CreateSymbolicLink(FilePath(dir), real);
        Assert.Null(_store.Load("Test_l"));
        Assert.ThrowsAny<Exception>(() => _store.Save("Test_l", _now, Sample()));
        Assert.Contains("\"count\":1}", File.ReadAllText(real));          // target untouched
        Assert.NotNull(new FileInfo(FilePath(dir)).LinkTarget);            // link not replaced
    }

    [Fact]
    public void DirectoryAtTarget_IsNeitherReadNorWritten()
    {
        var dir = MakeDb("Test_d");
        Directory.CreateDirectory(FilePath(dir));
        Assert.Null(_store.Load("Test_d"));
        Assert.ThrowsAny<Exception>(() => _store.Save("Test_d", _now, Sample()));
        Assert.True(Directory.Exists(FilePath(dir)));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"v\":2,\"asOf\":\"2026-10-20T00:00:00Z\",\"lengths\":[]}")]
    [InlineData("{\"v\":1,\"asOf\":\"2030-01-01T00:00:00Z\",\"lengths\":[]}")]                                      // future asOf
    [InlineData("{\"v\":1,\"asOf\":\"2026-10-20T00:00:00Z\",\"lengths\":[{\"length\":5,\"count\":1},{\"length\":3,\"count\":1}]}")]   // unsorted
    [InlineData("{\"v\":1,\"asOf\":\"2026-10-20T00:00:00Z\",\"lengths\":[{\"length\":3,\"count\":1},{\"length\":3,\"count\":1}]}")]   // duplicate
    [InlineData("{\"v\":1,\"asOf\":\"2026-10-20T00:00:00Z\",\"lengths\":[{\"length\":-1,\"count\":1}]}")]            // negative length
    [InlineData("{\"v\":1,\"asOf\":\"2026-10-20T00:00:00Z\",\"lengths\":[{\"length\":1,\"count\":0}]}")]             // zero count
    [InlineData("{\"v\":1,\"asOf\":\"yesterday\",\"lengths\":[]}")]
    [InlineData("{\"v\":1,\"asOf\":\"2026-10-20T00:00:00Z\"}")]                                                      // missing lengths
    public void Invalid_IsMovedAside_AndLoadsAsNone(string content)
    {
        var dir = MakeDb("Test_c");
        File.WriteAllText(FilePath(dir), content);
        Assert.Null(_store.Load("Test_c"));
        Assert.False(File.Exists(FilePath(dir)));
        Assert.Single(Directory.GetFiles(dir, LengthBreakdownStore.FileName + ".corrupt-*"));
    }

    [Fact]
    public void Oversize_AndTooManyEntries_AreMovedAside()
    {
        var dir = MakeDb("Test_o");
        File.WriteAllText(FilePath(dir), new string(' ', (int)LengthBreakdownStore.MaxFileBytes + 1));
        Assert.Null(_store.Load("Test_o"));
        Assert.False(File.Exists(FilePath(dir)));

        var sb = new StringBuilder("{\"v\":1,\"asOf\":\"2026-10-20T00:00:00Z\",\"lengths\":[");
        for (int i = 0; i <= LengthBreakdownStore.MaxEntries; i++) sb.Append(i > 0 ? "," : "").Append("{\"length\":").Append(i).Append(",\"count\":1}");
        File.WriteAllText(FilePath(dir), sb.Append("]}").ToString());
        Assert.Null(_store.Load("Test_o"));
        Assert.False(File.Exists(FilePath(dir)));
    }

    [Fact]
    public void Save_RemovesStaleTmpFiles()
    {
        var dir = MakeDb("Test_t");
        var stale = FilePath(dir) + ".tmp-deadbeef";
        File.WriteAllText(stale, "x");
        _store.Save("Test_t", _now, Sample());
        Assert.False(File.Exists(stale));
    }
}

public class LengthBreakdownApiTests
{
    private const string Db = HoarderDbWebFactory.DbName;

    private static async Task<JsonElement> Body(HttpResponseMessage r) =>
        JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.Clone();

    private static async Task<string> Post(HttpClient c, string db)
    {
        var r = await c.PostAsJsonAsync("/api/jobs/length-breakdown", new { db });
        Assert.Equal(HttpStatusCode.Accepted, r.StatusCode);
        return (await Body(r)).GetProperty("id").GetString()!;
    }

    private static async Task<JsonElement> WaitStatus(HttpClient c, string id, params string[] statuses)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            var j = await Body(await c.GetAsync("/api/jobs/" + id));
            if (statuses.Contains(j.GetProperty("status").GetString())) return j;
            await Task.Delay(25);
        }
        throw new TimeoutException($"job {id} did not reach {string.Join('|', statuses)}");
    }

    private static HoarderDbWebFactory Factory(BreakdownHook? bh = null, BlockingHook? ih = null) =>
        new(1000, null, null, ih, null, null, null) { ExtraServices = bh is null ? null : s => s.AddSingleton<IBreakdownProgressHook>(bh) };

    private static Dictionary<int, long> Expected() =>
        HoarderDbWebFactory.Words.GroupBy(w => w.Length).ToDictionary(g => g.Key, g => (long)g.Count());

    private static Dictionary<int, long> Lengths(JsonElement body) =>
        body.GetProperty("lengths").EnumerateArray().ToDictionary(e => e.GetProperty("length").GetInt32(), e => e.GetProperty("count").GetInt64());

    [Fact]
    public async Task Get_NoCache_IsOkWithNulls()
    {
        using var f = Factory();
        var c = await f.NewLoggedInClientAsync();
        var r = await c.GetAsync($"/api/dbs/{Db}/length-breakdown");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var b = await Body(r);
        Assert.Equal(JsonValueKind.Null, b.GetProperty("asOf").ValueKind);
        Assert.Equal(JsonValueKind.Null, b.GetProperty("lengths").ValueKind);
    }

    [Fact]
    public async Task Get_And_Post_Validation_AndAuth()
    {
        using var f = Factory();
        var c = await f.NewLoggedInClientAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/api/dbs/Test_nope/length-breakdown")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.GetAsync("/api/dbs/bad..%2Fname/length-breakdown")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.PostAsJsonAsync("/api/jobs/length-breakdown", new { db = "Test_nope" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/jobs/length-breakdown", new { db = "../x" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/jobs/length-breakdown", new { })).StatusCode);

        var anon = f.NewClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync($"/api/dbs/{Db}/length-breakdown")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsJsonAsync("/api/jobs/length-breakdown", new { db = Db })).StatusCode);
        var k = f.NewApiKeyClient();
        Assert.Equal(HttpStatusCode.OK, (await k.GetAsync($"/api/dbs/{Db}/length-breakdown")).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await k.PostAsJsonAsync("/api/jobs/length-breakdown", new { db = Db })).StatusCode);
    }

    [Fact]
    public async Task Post_ThenGet_ReturnsExactCounts_AndSurvivesRestart()
    {
        using var f = Factory();
        var c = await f.NewLoggedInClientAsync();
        var id = await Post(c, Db);
        var job = await WaitStatus(c, id, "completed", "failed", "cancelled");
        Assert.Equal("completed", job.GetProperty("status").GetString());
        Assert.Equal("breakdown", job.GetProperty("kind").GetString());

        var b = await Body(await c.GetAsync($"/api/dbs/{Db}/length-breakdown"));
        Assert.Equal(Expected(), Lengths(b));
        Assert.True(b.GetProperty("asOf").GetDateTimeOffset() > DateTimeOffset.UtcNow.AddMinutes(-5));
        Assert.False(b.TryGetProperty("path", out _));

        using var f2 = new HoarderDbWebFactory(1000, null, null, null, null, f.Root, f.EnvHash);
        var c2 = await f2.NewLoggedInClientAsync();
        Assert.Equal(Expected(), Lengths(await Body(await c2.GetAsync($"/api/dbs/{Db}/length-breakdown"))));
    }

    [Fact]
    public async Task Duplicate_Post_ReturnsTheSameQueuedJob()
    {
        var bh = new BreakdownHook(); var ih = new BlockingHook();
        using var f = Factory(bh, ih);
        var c = await f.NewLoggedInClientAsync();
        File.WriteAllText(Path.Combine(f.Incoming, "a.txt"), string.Join('\n', Enumerable.Range(0, 5000).Select(i => "w" + i.ToString("D8"))));
        ih.Arm();
        try
        {
            await c.PostAsJsonAsync("/api/jobs/import", new { db = "Test_imp", source = "a.txt" });
            Assert.True(ih.Reached.Wait(TimeSpan.FromSeconds(30)));
            var a = await Post(c, Db);
            var b = await Post(c, Db);
            Assert.Equal(a, b);
            Assert.Equal("queued", (await Body(await c.GetAsync("/api/jobs/" + a))).GetProperty("status").GetString());
            // Stored result is readable (200) while the import holds the write window.
            Assert.Equal(HttpStatusCode.OK, (await c.GetAsync($"/api/dbs/{Db}/length-breakdown")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/dbs/Test_imp/length-breakdown")).StatusCode);
            // Delete is refused while a breakdown is queued for the db.
            Assert.Equal(HttpStatusCode.Conflict, (await c.DeleteAsync($"/api/dbs/{Db}")).StatusCode);
        }
        finally { ih.Disarm(); }
    }

    [Fact]
    public async Task Post_DuringImport_StaysQueued_UntilImportEnds()
    {
        var ih = new BlockingHook();
        using var f = Factory(null, ih);
        var c = await f.NewLoggedInClientAsync();
        File.WriteAllText(Path.Combine(f.Incoming, "a.txt"), string.Join('\n', Enumerable.Range(0, 5000).Select(i => "w" + i.ToString("D8"))));
        ih.Arm();
        string id;
        try
        {
            await c.PostAsJsonAsync("/api/jobs/import", new { db = Db, source = "a.txt" });
            Assert.True(ih.Reached.Wait(TimeSpan.FromSeconds(30)));
            id = await Post(c, Db);
            await Task.Delay(300);
            Assert.Equal("queued", (await Body(await c.GetAsync("/api/jobs/" + id))).GetProperty("status").GetString());
        }
        finally { ih.Disarm(); }
        Assert.Equal("completed", (await WaitStatus(c, id, "completed", "failed", "cancelled")).GetProperty("status").GetString());
        var counts = Lengths(await Body(await c.GetAsync($"/api/dbs/{Db}/length-breakdown")));
        Assert.Equal(HoarderDbWebFactory.Words.Length + 5000, counts.Values.Sum());   // scan saw the imported words too
        Assert.Equal(5000, counts[9]);
    }

    [Fact]
    public async Task Cancel_Queued_LeavesCacheUntouched()
    {
        var ih = new BlockingHook();
        using var f = Factory(null, ih);
        var c = await f.NewLoggedInClientAsync();
        File.WriteAllText(Path.Combine(f.Incoming, "a.txt"), string.Join('\n', Enumerable.Range(0, 5000).Select(i => "w" + i.ToString("D8"))));
        ih.Arm();
        try
        {
            await c.PostAsJsonAsync("/api/jobs/import", new { db = "Test_imp", source = "a.txt" });
            Assert.True(ih.Reached.Wait(TimeSpan.FromSeconds(30)));
            var id = await Post(c, Db);
            await c.PostAsync($"/api/jobs/{id}/cancel", null);
            Assert.Equal("cancelled", (await Body(await c.GetAsync("/api/jobs/" + id))).GetProperty("status").GetString());
        }
        finally { ih.Disarm(); }
        var b = await Body(await c.GetAsync($"/api/dbs/{Db}/length-breakdown"));
        Assert.Equal(JsonValueKind.Null, b.GetProperty("asOf").ValueKind);
    }

    [Fact]
    public async Task Cancel_Running_EndsCancelled_AndKeepsPreviousResult()
    {
        var bh = new BreakdownHook();
        using var f = Factory(bh);
        var c = await f.NewLoggedInClientAsync();
        await WaitStatus(c, await Post(c, Db), "completed");                 // a previous result exists
        var before = (await Body(await c.GetAsync($"/api/dbs/{Db}/length-breakdown"))).GetProperty("asOf").GetDateTimeOffset();
        await Task.Delay(20);

        bh.Arm();
        try
        {
            var id = await Post(c, Db);
            Assert.True(bh.Reached.Wait(TimeSpan.FromSeconds(30)));
            Assert.Equal("running", (await Body(await c.GetAsync("/api/jobs/" + id))).GetProperty("status").GetString());
            await c.PostAsync($"/api/jobs/{id}/cancel", null);
            bh.Release.Set();
            Assert.Equal("cancelled", (await WaitStatus(c, id, "cancelled", "completed", "failed")).GetProperty("status").GetString());
        }
        finally { bh.Disarm(); }
        var after = (await Body(await c.GetAsync($"/api/dbs/{Db}/length-breakdown"))).GetProperty("asOf").GetDateTimeOffset();
        Assert.Equal(before, after);
    }

    // Forces the parallel scan path on the 9-word fixture (normally it needs >= 1M keys) with ScanThreads = 3.
    private static HoarderDbWebFactory ParallelFactory(BreakdownHook? bh = null) =>
        new(1000, new Dictionary<string, string?> { ["Storage:ScanThreads"] = "3" }, null, null, null, null, null)
        { ExtraServices = bh is null ? null : s => s.AddSingleton<IBreakdownProgressHook>(bh) };

    [Fact]
    public async Task ParallelPath_ReturnsSameCounts_StatsAndCountEndpoints()
    {
        var saved = WordlistDatabase.ParallelMinKeys;
        WordlistDatabase.ParallelMinKeys = 0;
        try
        {
            using var f = ParallelFactory();
            var c = await f.NewLoggedInClientAsync();
            var dbs = f.Services.GetRequiredService<IDbManager>();
            using (var lease = dbs.AcquireRead(Db)) Assert.Equal(3, lease.Db.ScanThreads);

            var job = await WaitStatus(c, await Post(c, Db), "completed", "failed", "cancelled");
            Assert.Equal("completed", job.GetProperty("status").GetString());
            Assert.Equal(Expected(), Lengths(await Body(await c.GetAsync($"/api/dbs/{Db}/length-breakdown"))));

            var stats = await Body(await c.GetAsync($"/api/dbs/{Db}/stats?full=true"));
            Assert.Equal(Expected(), Lengths(stats));
            var cnt = await Body(await c.GetAsync($"/api/dbs/{Db}/count?min=3&max=4"));
            Assert.Equal(HoarderDbWebFactory.Words.Count(w => w.Length is >= 3 and <= 4), cnt.GetProperty("count").GetInt64());
        }
        finally { WordlistDatabase.ParallelMinKeys = saved; }
    }

    [Fact]
    public async Task ParallelPath_Cancel_KeepsPreviousResult_AndReleasesTheLease()
    {
        var saved = WordlistDatabase.ParallelMinKeys;
        WordlistDatabase.ParallelMinKeys = 0;
        var bh = new BreakdownHook();
        try
        {
            using var f = ParallelFactory(bh);
            var c = await f.NewLoggedInClientAsync();
            await WaitStatus(c, await Post(c, Db), "completed");
            var before = (await Body(await c.GetAsync($"/api/dbs/{Db}/length-breakdown"))).GetProperty("asOf").GetDateTimeOffset();
            await Task.Delay(20);

            bh.Arm();
            var id = await Post(c, Db);
            Assert.True(bh.Reached.Wait(TimeSpan.FromSeconds(30)));      // running, lease held, scan not started yet
            await c.PostAsync($"/api/jobs/{id}/cancel", null);
            bh.Release.Set();
            Assert.Equal("cancelled", (await WaitStatus(c, id, "cancelled", "completed", "failed")).GetProperty("status").GetString());
            bh.Disarm();

            var after = (await Body(await c.GetAsync($"/api/dbs/{Db}/length-breakdown"))).GetProperty("asOf").GetDateTimeOffset();
            Assert.Equal(before, after);
            // Lease released: the db is readable and writable-window capable again (a new scan completes).
            Assert.Equal("completed", (await WaitStatus(c, await Post(c, Db), "completed", "failed", "cancelled")).GetProperty("status").GetString());
        }
        finally { bh.Disarm(); WordlistDatabase.ParallelMinKeys = saved; }
    }

    [Fact]
    public async Task FailedScan_KeepsPreviousResult_WithFixedMessage()
    {
        var bh = new BreakdownHook();
        using var f = Factory(bh);
        var c = await f.NewLoggedInClientAsync();
        await WaitStatus(c, await Post(c, Db), "completed");
        var before = (await Body(await c.GetAsync($"/api/dbs/{Db}/length-breakdown"))).GetProperty("asOf").GetDateTimeOffset();

        bh.Arm(fail: true);
        bh.Release.Set();
        var job = await WaitStatus(c, await Post(c, Db), "failed", "completed");
        bh.Disarm();
        Assert.Equal("failed", job.GetProperty("status").GetString());
        Assert.DoesNotContain("test failure", job.GetProperty("error").GetString());
        var after = (await Body(await c.GetAsync($"/api/dbs/{Db}/length-breakdown"))).GetProperty("asOf").GetDateTimeOffset();
        Assert.Equal(before, after);
    }

    [Fact]
    public async Task ScheduledCompact_QueuesOneBreakdownPerDb_ButManualCompactQueuesNone()
    {
        using var f = Factory();
        HoarderDbWebFactory.Seed(Path.Combine(f.Root, "Test_b"), ["one", "two"]);
        var c = await f.NewLoggedInClientAsync();
        var dbs = f.Services.GetRequiredService<IDbManager>();
        var queue = f.Services.GetRequiredService<JobQueue>();
        var reg = f.Services.GetRequiredService<JobRegistry>();

        // Manual compact (no RunId): nothing follows it.
        var r = await c.PostAsJsonAsync("/api/jobs/compact", new { db = Db });
        await WaitStatus(c, (await Body(r)).GetProperty("id").GetString()!, "completed", "failed");
        await Task.Delay(300);
        Assert.DoesNotContain(reg.List(), j => j.Kind == "breakdown");

        Assert.Equal(2, WeeklyCompactionService.EnqueueAll(dbs, queue, NullLogger.Instance, reg, "run1"));
        // Poll for the breakdown jobs themselves, not for "queue empty" (they are queued after each compact).
        for (int i = 0; i < 2400 && reg.List().Count(j => j.Kind == "breakdown" && j.Status == "completed") < 2; i++)
            await Task.Delay(25);
        var bd = reg.List().Where(j => j.Kind == "breakdown").ToList();
        Assert.Equal(2, bd.Count);
        Assert.All(bd, j => Assert.Equal("completed", j.Status));
        Assert.Equal(new[] { Db, "Test_b" }.OrderBy(x => x), bd.Select(j => j.Db).OrderBy(x => x));
        Assert.NotNull(f.Services.GetRequiredService<ILengthBreakdownStore>().Load("Test_b"));
    }

    [Fact]
    public async Task FreshDb_Create_Import_Read_Breakdown()
    {
        using var f = Factory();
        var c = await f.NewLoggedInClientAsync();
        Assert.Equal(HttpStatusCode.Created, (await c.PostAsJsonAsync("/api/dbs", new { name = "Test_fresh" })).StatusCode);
        File.WriteAllText(Path.Combine(f.Incoming, "w.txt"), "abc\nab\nzz\nhunter2\n");
        var imp = await c.PostAsJsonAsync("/api/jobs/import", new { db = "Test_fresh", source = "w.txt" });
        await WaitStatus(c, (await Body(imp)).GetProperty("id").GetString()!, "completed");
        Assert.True((await Body(await c.GetAsync("/api/dbs/Test_fresh/lookup?word=abc"))).GetProperty("found").GetBoolean());
        await WaitStatus(c, await Post(c, "Test_fresh"), "completed");
        var b = Lengths(await Body(await c.GetAsync("/api/dbs/Test_fresh/length-breakdown")));
        Assert.Equal(new Dictionary<int, long> { [2] = 2, [3] = 1, [7] = 1 }, b);
    }
}

/// <summary>Memory guard: a breakdown refuses to scan a db with more than 64 uncompacted L0 files.</summary>
public class BreakdownL0GuardTests
{
    private sealed class RecordingStore : ILengthBreakdownStore
    {
        public int Saves;
        public LengthBreakdown? Load(string db) => null;
        public void Save(string db, DateTimeOffset asOf, IReadOnlyList<LengthCount> lengths) => Saves++;
    }

    private static async Task<(RecordingStore store, Exception? ex)> RunWithL0(string l0)
    {
        var root = Path.Combine(Path.GetTempPath(), "Test_" + Guid.NewGuid());
        HoarderDbWebFactory.EnsureThrowaway(root);
        Directory.CreateDirectory(root);
        try
        {
            var mgr = new DbManager(new DataRootResolver(root));
            await mgr.CreateAsync("Test_guard", CancellationToken.None);
            await mgr.WithWriteAsync("Test_guard", DbOpenMode.Bulk, (db, _) =>
            { db.Import(new[] { "abc", "defg" }, null, null, threads: 1); return Task.FromResult(0); }, CancellationToken.None);
            using (var lease = mgr.AcquireRead("Test_guard")) lease.Db.L0PropertyHook = () => l0;
            var store = new RecordingStore();
            var job = new Job(JobKind.Breakdown, "Test_guard", null);
            Exception? ex = null;
            try { await BreakdownRunner.RunAsync(job, mgr, store, new NoopBreakdownProgressHook(), CancellationToken.None); }
            catch (Exception e) { ex = e; }
            await mgr.StopAsync(CancellationToken.None);
            return (store, ex);
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("65")]
    [InlineData("garbage")]   // unreadable count fails closed
    public async Task AboveThreshold_Fails_AndSavesNothing(string l0)
    {
        var (store, ex) = await RunWithL0(l0);
        Assert.IsType<BreakdownNeedsCompactionException>(ex);
        Assert.Equal(0, store.Saves);
    }

    [Theory]
    [InlineData("64")]
    [InlineData("0")]
    public async Task AtOrBelowThreshold_RunsAndSaves(string l0)
    {
        var (store, ex) = await RunWithL0(l0);
        Assert.Null(ex);
        Assert.Equal(1, store.Saves);
    }

    [Fact]
    public void JobQueue_MapsGuardToFixedMessage()
    {
        var src = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "../../../../../src/HoarderDB.Web/Jobs/JobQueue.cs"));
        Assert.Contains("BreakdownNeedsCompactionException => \"Compact the database first.\"", src);
    }
}

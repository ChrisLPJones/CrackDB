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

/// <summary>Minimal manual clock: timers fire only when Advance() passes their due time.</summary>
internal sealed class FakeTime(DateTimeOffset start) : TimeProvider
{
    private readonly object _gate = new();
    private DateTimeOffset _now = start;
    private readonly List<FakeTimer> _timers = [];
    public override DateTimeOffset GetUtcNow() { lock (_gate) return _now; }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var t = new FakeTimer(this, callback, state);
        t.Change(dueTime, period);
        lock (_gate) _timers.Add(t);
        return t;
    }

    public void Advance(TimeSpan by)
    {
        List<FakeTimer> due;
        lock (_gate)
        {
            _now += by;
            due = _timers.Where(t => t.Due is { } d && d <= _now).ToList();
            foreach (var t in due) t.Due = null;
        }
        foreach (var t in due) t.Fire();
    }

    private sealed class FakeTimer(FakeTime owner, TimerCallback cb, object? state) : ITimer
    {
        public DateTimeOffset? Due;
        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._gate) Due = dueTime == Timeout.InfiniteTimeSpan ? null : owner._now + dueTime;
            return true;
        }
        public void Fire() => cb(state);
        public void Dispose() { lock (owner._gate) { Due = null; owner._timers.Remove(this); } }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}

public class NextRunGeneralTests
{
    private static DateTimeOffset Utc(string s) =>
        DateTimeOffset.Parse(s, null, System.Globalization.DateTimeStyles.AssumeUniversal);

    [Fact]
    public void SundayMidnightOverload_EqualsGeneral()
    {
        var tz = TimeZoneInfo.FindSystemTimeZoneById("Australia/Sydney");
        foreach (var n in new[] { "2026-10-13T10:30:00Z", "2026-10-17T13:00:00Z", "2026-10-03T16:00:00Z" })
            Assert.Equal(WeeklyCompactionService.NextRun(Utc(n), tz),
                WeeklyCompactionService.NextRun(Utc(n), tz, DayOfWeek.Sunday, 0, 0));
    }

    [Theory]
    [InlineData("2026-10-13T10:30:00Z", DayOfWeek.Wednesday, 14, 45, "2026-10-14T14:45:00Z")]
    [InlineData("2026-10-14T14:45:00Z", DayOfWeek.Wednesday, 14, 45, "2026-10-21T14:45:00Z")]   // exact match -> strictly after
    [InlineData("2026-10-14T14:44:59Z", DayOfWeek.Wednesday, 14, 45, "2026-10-14T14:45:00Z")]
    [InlineData("2026-10-18T23:59:00Z", DayOfWeek.Saturday, 0, 1, "2026-10-24T00:01:00Z")]
    public void Utc_ArbitraryDayHourMinute(string now, DayOfWeek day, int h, int m, string expected) =>
        Assert.Equal(Utc(expected), WeeklyCompactionService.NextRun(Utc(now), TimeZoneInfo.Utc, day, h, m));

    [Fact]
    public void Sydney_GapWithMinutes_MovesToFirstValidStep()
    {
        // Sunday 2026-10-04 02:10 does not exist in Sydney (clocks go 02:00 -> 03:00): 15-min steps land on 03:10 (+11 = 16:10Z Sat).
        var tz = TimeZoneInfo.FindSystemTimeZoneById("Australia/Sydney");
        var next = WeeklyCompactionService.NextRun(Utc("2026-09-30T00:00:00Z"), tz, DayOfWeek.Sunday, 2, 10);
        Assert.Equal(Utc("2026-10-03T16:10:00Z"), next);
    }

    [Fact]
    public void Sydney_Overlap_UsesFirstOccurrence()
    {
        // Sunday 2027-04-04 02:30 happens twice in Sydney; first is +11 (15:30Z Sat).
        var tz = TimeZoneInfo.FindSystemTimeZoneById("Australia/Sydney");
        var next = WeeklyCompactionService.NextRun(Utc("2027-03-30T00:00:00Z"), tz, DayOfWeek.Sunday, 2, 30);
        Assert.Equal(Utc("2027-04-03T15:30:00Z"), next);
    }
}

public class AppSettingsStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Test_" + Guid.NewGuid());
    private string KeyDir => Path.Combine(_root, "keys");
    private string File_ => Path.Combine(KeyDir, AppSettingsStore.FileName);

    public AppSettingsStoreTests() { HoarderDbWebFactory.EnsureThrowaway(_root); Directory.CreateDirectory(_root); }
    public void Dispose() { HoarderDbWebFactory.EnsureThrowaway(_root); if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    private AppSettingsStore Open() =>
        new(Microsoft.Extensions.Options.Options.Create(new StorageOptions { KeyPath = KeyDir }), NullLogger<AppSettingsStore>.Instance);

    [Fact]
    public void Absent_GivesDefaults_NotRecovered()
    {
        var s = Open();
        Assert.Equal(CompactionSettings.Default, s.Initial);
        Assert.False(s.Recovered);
        Assert.Equal((true, 0, 0, 0), (s.Initial.Enabled, s.Initial.Day, s.Initial.Hour, s.Initial.Minute));
    }

    [Fact]
    public void RoundTrip_Mode0600_NoTmpLeft()
    {
        var s = Open();
        var want = new CompactionSettings(false, 3, 22, 15);
        s.Save(want);
        Assert.Equal(want, Open().Initial);
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(File_));
        Assert.Empty(Directory.GetFiles(KeyDir, "*.tmp-*"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"v\":2,\"compaction\":{\"enabled\":true,\"day\":0,\"hour\":0,\"minute\":0}}")]
    [InlineData("{\"v\":1,\"compaction\":{\"enabled\":true,\"day\":7,\"hour\":0,\"minute\":0}}")]
    [InlineData("{\"v\":1,\"compaction\":{\"enabled\":true,\"day\":0,\"hour\":24,\"minute\":0}}")]
    [InlineData("{\"v\":1,\"compaction\":{\"enabled\":true,\"day\":0,\"hour\":0,\"minute\":60}}")]
    [InlineData("{\"v\":1,\"compaction\":{\"enabled\":\"yes\",\"day\":0,\"hour\":0,\"minute\":0}}")]
    [InlineData("{\"v\":1,\"compaction\":{\"enabled\":true,\"day\":1.5,\"hour\":0,\"minute\":0}}")]
    [InlineData("{\"v\":1,\"compaction\":{\"enabled\":true,\"day\":0,\"hour\":0}}")]
    public void Invalid_MovedAside_DefaultsAndRecovered(string content)
    {
        Directory.CreateDirectory(KeyDir);
        File.WriteAllText(File_, content);
        var s = Open();
        Assert.Equal(CompactionSettings.Default, s.Initial);
        Assert.True(s.Recovered);
        Assert.False(File.Exists(File_));
        Assert.Single(Directory.GetFiles(KeyDir, AppSettingsStore.FileName + ".corrupt-*"));
    }

    [Fact]
    public void Symlink_IsNeitherReadNorWritten()
    {
        if (OperatingSystem.IsWindows()) return;
        Directory.CreateDirectory(KeyDir);
        var target = Path.Combine(_root, "target.json");
        File.WriteAllText(target, "{\"v\":1,\"compaction\":{\"enabled\":false,\"day\":2,\"hour\":1,\"minute\":1}}");
        File.CreateSymbolicLink(File_, target);
        var s = Open();
        Assert.Equal(CompactionSettings.Default, s.Initial);   // not read
        Assert.True(s.Recovered);
        Assert.ThrowsAny<IOException>(() => s.Save(new CompactionSettings(true, 1, 1, 1)));
        Assert.Contains("\"day\":2", File.ReadAllText(target));   // target untouched
        Assert.Empty(Directory.GetFiles(KeyDir, "*.tmp-*"));
    }

    [Fact]
    public void FailedSave_LeavesNoTmp_AndScheduleMemoryUnchanged()
    {
        if (OperatingSystem.IsWindows() || Environment.UserName == "root") return;
        var store = Open();
        var sched = new CompactionSchedule(store);
        File.SetUnixFileMode(KeyDir, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            Assert.ThrowsAny<Exception>(() => sched.Save(new CompactionSettings(false, 4, 4, 4)));
        }
        finally { File.SetUnixFileMode(KeyDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
        Assert.Equal(CompactionSettings.Default, sched.Current);
        Assert.Empty(Directory.GetFiles(KeyDir, "*.tmp-*"));
    }
}

public class SettingsEndpointsTests
{
    private const string Url = "/api/settings";
    private const string PutUrl = "/api/settings/compaction";

    private static StringContent Json(string s) => new(s, Encoding.UTF8, "application/json");

    [Fact]
    public async Task Get_Defaults_And_ShapeHasNoPaths()
    {
        using var f = new HoarderDbWebFactory();
        var c = await f.NewLoggedInClientAsync();
        var r = await c.GetAsync(Url);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var text = await r.Content.ReadAsStringAsync();
        Assert.DoesNotContain(f.Root, text);
        Assert.DoesNotContain("keys-not-a-db", text);
        using var doc = JsonDocument.Parse(text);
        var comp = doc.RootElement.GetProperty("compaction");
        Assert.True(comp.GetProperty("enabled").GetBoolean());
        Assert.Equal(0, comp.GetProperty("day").GetInt32());
        Assert.Equal("Australia/Sydney", comp.GetProperty("timeZone").GetString());
        Assert.True(comp.GetProperty("isDefault").GetBoolean());
        Assert.False(comp.GetProperty("recovered").GetBoolean());
        Assert.NotEqual(JsonValueKind.Null, comp.GetProperty("nextRunUtc").ValueKind);
        Assert.Matches(@"^[+-]\d\d:\d\d$", comp.GetProperty("utcOffset").GetString());
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("status").GetProperty("lastCompaction").ValueKind);
    }

    [Fact]
    public async Task Put_SavesAndReturns_DisabledHasNoNextRun_PersistsAcrossRestart()
    {
        using var f1 = new HoarderDbWebFactory();
        var c = await f1.NewLoggedInClientAsync();
        var r = await c.PutAsync(PutUrl, Json("{\"enabled\":true,\"day\":3,\"hour\":22,\"minute\":15}"));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        using (var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync()))
        {
            Assert.Equal(3, doc.RootElement.GetProperty("day").GetInt32());
            Assert.False(doc.RootElement.GetProperty("isDefault").GetBoolean());
            Assert.Equal("22:15", doc.RootElement.GetProperty("nextRunLocal").GetString()![^5..]);
        }
        var off = await c.PutAsync(PutUrl, Json("{\"enabled\":false,\"day\":3,\"hour\":22,\"minute\":15}"));
        using (var doc = JsonDocument.Parse(await off.Content.ReadAsStringAsync()))
            Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("nextRunUtc").ValueKind);
        await c.PutAsync(PutUrl, Json("{\"enabled\":true,\"day\":5,\"hour\":1,\"minute\":2}"));

        using var f2 = new HoarderDbWebFactory(1000, null, null, null, null, f1.Root, f1.EnvHash);
        var c2 = await f2.NewLoggedInClientAsync();
        using var after = JsonDocument.Parse(await (await c2.GetAsync(Url)).Content.ReadAsStringAsync());
        var comp = after.RootElement.GetProperty("compaction");
        Assert.Equal((5, 1, 2), (comp.GetProperty("day").GetInt32(), comp.GetProperty("hour").GetInt32(), comp.GetProperty("minute").GetInt32()));
    }

    [Fact]
    public async Task ApiKey_Gets403_OnBothRoutes()
    {
        using var f = new HoarderDbWebFactory();
        var k = f.NewApiKeyClient();
        Assert.Equal(HttpStatusCode.Forbidden, (await k.GetAsync(Url)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await k.PutAsync(PutUrl, Json("{\"enabled\":true,\"day\":0,\"hour\":0,\"minute\":0}"))).StatusCode);
    }

    [Fact]
    public async Task Anonymous_Gets401()
    {
        using var f = new HoarderDbWebFactory();
        var c = f.NewClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync(Url)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.PutAsync(PutUrl, Json("{}"))).StatusCode);
    }

    [Fact]
    public async Task NonJson_Gets415()
    {
        using var f = new HoarderDbWebFactory();
        var c = await f.NewLoggedInClientAsync();
        var r = await c.PutAsync(PutUrl, new StringContent("enabled=true", Encoding.UTF8, "application/x-www-form-urlencoded"));
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, r.StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"enabled\":true,\"day\":0,\"hour\":0}")]
    [InlineData("{\"enabled\":\"true\",\"day\":0,\"hour\":0,\"minute\":0}")]
    [InlineData("{\"enabled\":true,\"day\":7,\"hour\":0,\"minute\":0}")]
    [InlineData("{\"enabled\":true,\"day\":-1,\"hour\":0,\"minute\":0}")]
    [InlineData("{\"enabled\":true,\"day\":0,\"hour\":24,\"minute\":0}")]
    [InlineData("{\"enabled\":true,\"day\":0,\"hour\":0,\"minute\":60}")]
    [InlineData("{\"enabled\":true,\"day\":\"0\",\"hour\":0,\"minute\":0}")]
    [InlineData("{\"enabled\":true,\"day\":0.5,\"hour\":0,\"minute\":0}")]
    [InlineData("{\"enabled\":true,\"day\":99999999999,\"hour\":0,\"minute\":0}")]
    public async Task BadBody_Gets400_AndEchoesNothing(string body)
    {
        using var f = new HoarderDbWebFactory();
        var c = await f.NewLoggedInClientAsync();
        var r = await c.PutAsync(PutUrl, Json(body));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        var text = await r.Content.ReadAsStringAsync();
        Assert.Contains("Invalid schedule.", text);
        if (body.Length > 8) Assert.DoesNotContain(body, text);
        Assert.False(File.Exists(Path.Combine(f.Root, "keys-not-a-db", AppSettingsStore.FileName)));
    }

    [Fact]
    public async Task RateLimited_After20()
    {
        using var f = new HoarderDbWebFactory(1000, null, "10.9.8.7");
        var c = await f.NewLoggedInClientAsync();
        HttpStatusCode last = 0;
        for (var i = 0; i < 21; i++)
            last = (await c.PutAsync(PutUrl, Json("{\"enabled\":true,\"day\":0,\"hour\":0,\"minute\":0}"))).StatusCode;
        Assert.Equal(HttpStatusCode.TooManyRequests, last);
    }

    [Fact]
    public async Task Unwritable_KeyDir_Gets500_AndMemoryUnchanged()
    {
        if (OperatingSystem.IsWindows() || Environment.UserName == "root") return;
        using var f = new HoarderDbWebFactory();
        var c = await f.NewLoggedInClientAsync();
        var dir = Path.Combine(f.Root, "keys-not-a-db");
        File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            var r = await c.PutAsync(PutUrl, Json("{\"enabled\":false,\"day\":2,\"hour\":2,\"minute\":2}"));
            Assert.Equal(HttpStatusCode.InternalServerError, r.StatusCode);
            Assert.Contains("Could not save the settings.", await r.Content.ReadAsStringAsync());
        }
        finally { File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
        using var doc = JsonDocument.Parse(await (await c.GetAsync(Url)).Content.ReadAsStringAsync());
        Assert.True(doc.RootElement.GetProperty("compaction").GetProperty("isDefault").GetBoolean());
    }

    [Fact]
    public async Task CorruptFile_AtStartup_Recovered()
    {
        using var f1 = new HoarderDbWebFactory();
        await f1.NewLoggedInClientAsync();
        File.WriteAllText(Path.Combine(f1.Root, "keys-not-a-db", AppSettingsStore.FileName), "garbage");
        using var f2 = new HoarderDbWebFactory(1000, null, null, null, null, f1.Root, f1.EnvHash);
        var c = await f2.NewLoggedInClientAsync();
        using var doc = JsonDocument.Parse(await (await c.GetAsync(Url)).Content.ReadAsStringAsync());
        Assert.True(doc.RootElement.GetProperty("compaction").GetProperty("recovered").GetBoolean());
    }

    private sealed class FakeDisk(string? spoolKey) : IDiskSpace
    {
        public long RecheckIntervalBytes => 1 << 20;
        public long? FreeBytes(string dir) => 123;
        public DiskVolume? Volume(string dir) => dir.EndsWith(".search-tmp") && spoolKey != null
            ? new DiskVolume(5L << 30, 10L << 30, spoolKey) : new DiskVolume(100L << 30, 200L << 30, "data-vol");
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("data-vol", false)]
    [InlineData("other-vol", true)]
    public async Task Disk_SpoolShownOnlyOnDifferentVolume(string? key, bool expectSpool)
    {
        using var f = new HoarderDbWebFactory { ExtraServices = s => { s.AddSingleton<IDiskSpace>(new FakeDisk(key)); } };
        // The spool dir only exists after startup prepares it; the endpoint falls back to the data root otherwise.
        var c = await f.NewLoggedInClientAsync();
        Directory.CreateDirectory(Path.Combine(f.Root, ".search-tmp"));
        var text = await (await c.GetAsync(Url)).Content.ReadAsStringAsync();
        Assert.DoesNotContain(f.Root, text);
        Assert.DoesNotContain("vol", text);   // mount keys are never returned
        using var doc = JsonDocument.Parse(text);
        var disk = doc.RootElement.GetProperty("status").GetProperty("disk");
        Assert.Equal(100L << 30, disk.GetProperty("data").GetProperty("freeBytes").GetInt64());
        Assert.Equal(expectSpool, disk.GetProperty("spool").ValueKind == JsonValueKind.Object);
    }

    [Fact]
    public async Task ManualCompact_AppearsInStatus_AsManual()
    {
        using var f = new HoarderDbWebFactory();
        var c = await f.NewLoggedInClientAsync();
        var r = await c.PostAsJsonAsync("/api/jobs/compact", new { db = HoarderDbWebFactory.DbName });
        Assert.True(r.IsSuccessStatusCode);
        var reg = f.Services.GetRequiredService<JobRegistry>();
        for (var i = 0; i < 2400 && reg.List().Any(j => j.Status is "queued" or "running"); i++) await Task.Delay(25);
        using var doc = JsonDocument.Parse(await (await c.GetAsync(Url)).Content.ReadAsStringAsync());
        var last = doc.RootElement.GetProperty("status").GetProperty("lastCompaction");
        Assert.Equal("manual", last.GetProperty("trigger").GetString());
        Assert.Equal("completed", last.GetProperty("result").GetString());
        Assert.Equal(HoarderDbWebFactory.DbName, last.GetProperty("databases")[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task Theme_IsAnonymous_AndScriptsLoadIt()
    {
        using var f = new HoarderDbWebFactory();
        var c = f.NewClient();
        var r = await c.GetAsync("/theme.js");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Contains("hoarderdb.theme", await r.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/login.html")).StatusCode);
        Assert.Contains("/theme.js", await (await c.GetAsync("/login.html")).Content.ReadAsStringAsync());
    }
}

public class CompactionHistoryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Test_" + Guid.NewGuid());
    public CompactionHistoryTests() { HoarderDbWebFactory.EnsureThrowaway(_root); Directory.CreateDirectory(_root); }
    public void Dispose() { HoarderDbWebFactory.EnsureThrowaway(_root); if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    private CompactionHistory Open() =>
        new(Microsoft.Extensions.Options.Options.Create(new StorageOptions { KeyPath = _root }), NullLogger<CompactionHistory>.Instance);

    private static CompactionEntry E(string? run, string db, string res, int startSec, int endSec) =>
        new(run, db, res, DateTimeOffset.UnixEpoch.AddSeconds(startSec), DateTimeOffset.UnixEpoch.AddSeconds(endSec));

    [Fact]
    public void LastRun_GroupsByRunId_AndAggregates()
    {
        var h = Open();
        var run = new string('a', 32);
        h.Append(E(null, "old", "completed", 0, 5));
        h.Append(E(run, "a", "completed", 100, 110));
        h.Append(E(run, "b", "failed", 110, 112));
        var last = h.LastRun()!;
        Assert.Equal("scheduled", last.Trigger);
        Assert.Equal("partial", last.Result);
        Assert.Equal(2, last.Databases.Count);
        Assert.Equal(12, last.DurationSeconds);
    }

    [Fact]
    public void Manual_IsSingleEntry_AndEmptyIsNull()
    {
        var h = Open();
        Assert.Null(h.LastRun());
        h.Append(E(new string('b', 32), "a", "completed", 0, 1));
        h.Append(E(null, "m", "completed", 10, 20));
        var last = h.LastRun()!;
        Assert.Equal("manual", last.Trigger);
        Assert.Single(last.Databases);
    }

    [Fact]
    public void Cap50_And_SurvivesReopen()
    {
        var h = Open();
        for (var i = 0; i < 60; i++) h.Append(E(null, "d" + i, "completed", i, i + 1));
        var again = Open();
        Assert.Equal("d59", again.LastRun()!.Databases[0].Name);
        var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(_root, CompactionHistory.FileName)));
        Assert.Equal(50, json.RootElement.GetProperty("entries").GetArrayLength());
        Assert.Empty(Directory.GetFiles(_root, "*.tmp-*"));
    }

    [Fact]
    public void Corrupt_MovedAside_StartsEmpty()
    {
        File.WriteAllText(Path.Combine(_root, CompactionHistory.FileName), "{\"v\":1,\"entries\":[{\"db\":\"x\",\"result\":\"weird\"}]}");
        var h = Open();
        Assert.Null(h.LastRun());
        Assert.Single(Directory.GetFiles(_root, CompactionHistory.FileName + ".corrupt-*"));
    }
}

public class SchedulerTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    private sealed class Rig : IAsyncDisposable
    {
        public HoarderDbWebFactory F { get; } = new();
        public FakeTime Time { get; }
        public CompactionSchedule Schedule { get; private set; } = null!;
        public JobRegistry Registry { get; private set; } = null!;
        private WeeklyCompactionService _svc = null!;
        private CancellationTokenSource _cts = new();

        public Rig(DateTimeOffset now) { Time = new FakeTime(now); }

        public async Task StartAsync(CompactionSettings s)
        {
            await F.NewLoggedInClientAsync();   // host up (hosted service in the app is real; ours is separate)
            Schedule = F.Services.GetRequiredService<CompactionSchedule>();
            Schedule.Save(s);
            Registry = F.Services.GetRequiredService<JobRegistry>();
            _svc = new WeeklyCompactionService(
                F.Services.GetRequiredService<IDbManager>(), F.Services.GetRequiredService<JobQueue>(), Registry, Schedule,
                Microsoft.Extensions.Options.Options.Create(new CompactionOptions { TimeZone = "UTC" }),
                NullLogger<WeeklyCompactionService>.Instance, Time);
            await _svc.StartAsync(_cts.Token);
        }

        public int CompactJobs => Registry.List().Count(j => j.Kind == "compact");
        public async Task SettleAsync() { for (var i = 0; i < 40; i++) await Task.Delay(10); }
        public async Task DrainAsync() { for (var i = 0; i < 2400 && Registry.List().Any(j => j.Status is "queued" or "running"); i++) await Task.Delay(25); }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            try { await _svc.StopAsync(CancellationToken.None); } catch { }
            await DrainAsync();
            F.Dispose();
        }
    }

    // 2026-10-14 is a Wednesday.
    private static DateTimeOffset T(string s) => DateTimeOffset.Parse(s, null, System.Globalization.DateTimeStyles.AssumeUniversal);

    [Fact]
    public async Task Fires_AtScheduledTime_OnceWithSharedRunId_AndNeverAgainSameMinute()
    {
        await using var r = new Rig(T("2026-10-14T09:00:00Z"));
        await r.StartAsync(new CompactionSettings(true, 3, 10, 0));   // Wed 10:00 UTC
        await r.SettleAsync();
        Assert.Equal(0, r.CompactJobs);
        r.Time.Advance(TimeSpan.FromMinutes(59));
        await r.SettleAsync();
        Assert.Equal(0, r.CompactJobs);
        r.Time.Advance(TimeSpan.FromMinutes(1));
        await r.SettleAsync();
        await r.DrainAsync();
        Assert.Equal(1, r.CompactJobs);            // one db in the throwaway root
        r.Time.Advance(TimeSpan.FromMinutes(30));  // same hour: no double fire
        await r.SettleAsync();
        Assert.Equal(1, r.CompactJobs);
        var hist = r.F.Services.GetRequiredService<CompactionHistory>().LastRun()!;
        Assert.Equal("scheduled", hist.Trigger);
    }

    [Fact]
    public async Task Save_WakesLoop_WithoutRestart()
    {
        await using var r = new Rig(T("2026-10-14T09:00:00Z"));
        await r.StartAsync(new CompactionSettings(true, 0, 0, 0));    // next Sunday
        await r.SettleAsync();
        r.Schedule.Save(new CompactionSettings(true, 3, 9, 30));      // today 09:30
        await r.SettleAsync();
        r.Time.Advance(TimeSpan.FromMinutes(30));
        await r.SettleAsync();
        await r.DrainAsync();
        Assert.Equal(1, r.CompactJobs);
    }

    [Fact]
    public async Task Disabled_NeverFires_AndEnablingResumes()
    {
        await using var r = new Rig(T("2026-10-14T09:00:00Z"));
        await r.StartAsync(new CompactionSettings(false, 3, 10, 0));
        await r.SettleAsync();
        r.Time.Advance(TimeSpan.FromHours(3));
        await r.SettleAsync();
        Assert.Equal(0, r.CompactJobs);
        r.Schedule.Save(new CompactionSettings(true, 3, 13, 0));      // now is 12:00
        await r.SettleAsync();
        r.Time.Advance(TimeSpan.FromHours(1));
        await r.SettleAsync();
        await r.DrainAsync();
        Assert.Equal(1, r.CompactJobs);
    }

    [Fact]
    public async Task Save_NeverFiresImmediately_EvenForAPastTimeToday()
    {
        await using var r = new Rig(T("2026-10-14T09:00:00Z"));
        await r.StartAsync(new CompactionSettings(false, 0, 0, 0));
        r.Schedule.Save(new CompactionSettings(true, 3, 8, 0));       // earlier today: next is next week
        await r.SettleAsync();
        Assert.Equal(0, r.CompactJobs);
    }

    [Fact]
    public void EnqueueAll_SkipsDatabaseWithCompactAlreadyQueued_AndTagsRunId()
    {
        using var f = new HoarderDbWebFactory();
        f.NewClient();
        var dbs = f.Services.GetRequiredService<IDbManager>();
        var reg = new JobRegistry();
        var queue = f.Services.GetRequiredService<JobQueue>();
        var other = new JobRegistry();
        reg.Add(new Job(JobKind.Compact, HoarderDbWebFactory.DbName, null));   // queued, never run
        Assert.Equal(0, WeeklyCompactionService.EnqueueAll(dbs, queue, NullLogger.Instance, reg, new string('c', 32)));
        Assert.Equal(1, WeeklyCompactionService.EnqueueAll(dbs, queue, NullLogger.Instance, other, new string('c', 32)));
        Assert.All(f.Services.GetRequiredService<JobRegistry>().List().Where(j => j.Kind == "compact"), j => Assert.Equal("compact", j.Kind));
    }
}

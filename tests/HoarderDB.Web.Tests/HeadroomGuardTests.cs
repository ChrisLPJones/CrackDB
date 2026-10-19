using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HoarderDB;
using HoarderDB.Web.Jobs;

namespace HoarderDB.Web.Tests;

/// <summary>
/// The headroom backstop (fix C): a queued job must be refused rather than started when free
/// memory would leave less than the safety margin above the configured import budget, and it must
/// fail closed (refuse) when the memory reading itself is unavailable/ambiguous.
/// </summary>
public class HeadroomGuardTests
{
    private static async Task<JsonElement> Body(HttpResponseMessage r) =>
        JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.Clone();

    private static async Task<string> Import(HttpClient c, string db, string source)
    {
        var r = await c.PostAsJsonAsync("/api/jobs/import", new { db, source });
        Assert.Equal(HttpStatusCode.Accepted, r.StatusCode);
        return (await Body(r)).GetProperty("id").GetString()!;
    }

    private static async Task<JsonElement> WaitTerminal(HttpClient c, string id)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            var j = await Body(await c.GetAsync("/api/jobs/" + id));
            var status = j.GetProperty("status").GetString();
            if (status is "completed" or "failed" or "cancelled") return j;
            await Task.Delay(25);
        }
        throw new TimeoutException($"job {id} never reached a terminal state");
    }

    // Pure-logic coverage of the guard itself, independent of the job pipeline.

    [Fact]
    public void HasHeadroom_RefusesWhenFreeMemoryBelowBudgetPlusMargin()
    {
        var saved = HeadroomGuard.AvailableBytesReader;
        try
        {
            const long budget = 4096L * 1024 * 1024;
            HeadroomGuard.AvailableBytesReader = () => budget;   // exactly the budget, no margin left
            Assert.False(HeadroomGuard.HasHeadroom(budget, out var reason));
            Assert.NotNull(reason);
            Assert.Contains("memory", reason, StringComparison.OrdinalIgnoreCase);

            HeadroomGuard.AvailableBytesReader = () => budget + HeadroomGuard.DefaultMarginBytes;   // exactly enough
            Assert.True(HeadroomGuard.HasHeadroom(budget, out var ok));
            Assert.Null(ok);
        }
        finally { HeadroomGuard.AvailableBytesReader = saved; }
    }

    [Fact]
    public void HasHeadroom_FailsClosed_WhenReadingUnavailable()
    {
        var saved = HeadroomGuard.AvailableBytesReader;
        try
        {
            HeadroomGuard.AvailableBytesReader = () => null;   // ambiguous/unreadable
            Assert.False(HeadroomGuard.HasHeadroom(1024 * 1024 * 1024, out var reason));
            Assert.NotNull(reason);
        }
        finally { HeadroomGuard.AvailableBytesReader = saved; }
    }

    [Fact]
    public void HasHeadroom_FailsClosed_WhenLoadReadingIsImplausible()
    {
        var saved = HeadroomGuard.AvailableBytesReader;
        try
        {
            // Simulate the GC-info-derived reader hitting an implausible value by driving the
            // public seam directly with the same ambiguity rule it documents (never fail open).
            HeadroomGuard.AvailableBytesReader = () => -1;
            Assert.False(HeadroomGuard.HasHeadroom(0, out var reason));
            Assert.NotNull(reason);
        }
        finally { HeadroomGuard.AvailableBytesReader = saved; }
    }

    // End-to-end: a queued import job is actually refused (Failed, clear message), not started,
    // when the backstop says there isn't enough headroom - exercised through the real job pipeline.

    [Fact]
    public async Task QueuedImport_RefusedAsFailed_WhenHeadroomInsufficient()
    {
        var saved = HeadroomGuard.AvailableBytesReader;
        using var f = new HoarderDbWebFactory();
        var c = await f.NewLoggedInClientAsync();
        File.WriteAllText(Path.Combine(f.Incoming, "tiny.txt"), "a\nb\nc\n");

        try
        {
            HeadroomGuard.AvailableBytesReader = () => 1;   // essentially no free memory
            var id = await Import(c, "Test_headroom", "tiny.txt");
            var j = await WaitTerminal(c, id);

            Assert.Equal("failed", j.GetProperty("status").GetString());
            var error = j.GetProperty("error").GetString();
            Assert.NotNull(error);
            Assert.Contains("memory", error, StringComparison.OrdinalIgnoreCase);

            // Refused before doing any work: the db was never created.
            var dbs = await Body(await c.GetAsync("/api/dbs"));
            Assert.DoesNotContain("Test_headroom", dbs.GetProperty("databases").EnumerateArray().Select(x => x.GetString()));
        }
        finally { HeadroomGuard.AvailableBytesReader = saved; }
    }

    [Fact]
    public async Task QueuedImport_Refused_WhenHeadroomReadingUnavailable_FailsClosed()
    {
        var saved = HeadroomGuard.AvailableBytesReader;
        using var f = new HoarderDbWebFactory();
        var c = await f.NewLoggedInClientAsync();
        File.WriteAllText(Path.Combine(f.Incoming, "tiny2.txt"), "a\nb\n");

        try
        {
            HeadroomGuard.AvailableBytesReader = () => null;   // ambiguous reading
            var id = await Import(c, "Test_headroom2", "tiny2.txt");
            var j = await WaitTerminal(c, id);

            Assert.Equal("failed", j.GetProperty("status").GetString());
            Assert.Contains("memory", j.GetProperty("error").GetString()!, StringComparison.OrdinalIgnoreCase);
        }
        finally { HeadroomGuard.AvailableBytesReader = saved; }
    }

    // Regression coverage for the budget/headroom mismatch bug: MemoryPlan.DefaultBudgetBytes() and
    // HeadroomGuard.HasHeadroom must be computed from the same cross-checked number (HoarderDB.SystemMemory),
    // so they never disagree the way they could when MemoryPlan read raw GC totals and HeadroomGuard
    // read the cross-checked figure. This drives the shared primitive's inputs directly (never
    // overriding HeadroomGuard.AvailableBytesReader itself), so both consumers go through their real,
    // unmodified default wiring onto SystemMemory.
    [Fact]
    public void BudgetAndHeadroom_AgreeOnTheSameCrossCheckedAvailableFigure()
    {
        var savedGc = SystemMemory.GcReader;
        var savedProc = SystemMemory.ProcMeminfoReader;
        try
        {
            const long MiB = 1024 * 1024;
            const long GiB = 1024 * MiB;

            // Reproduces the real-world scenario: an unconstrained cgroup makes the GC report the
            // outer host's full 32 GiB, while the real, cross-checked figure (/proc/meminfo on a 9 GiB
            // LXC) is only 6 GiB. Before the fix, MemoryPlan.DefaultBudgetBytes() would have sized a
            // budget off the inflated 32 GiB figure (~16 GiB), which HeadroomGuard's correctly
            // cross-checked ~6 GiB "available" could never satisfy - every import would fail.
            SystemMemory.GcReader = () => 32L * GiB;
            SystemMemory.ProcMeminfoReader = () => 6L * GiB;

            long budget = MemoryPlan.DefaultBudgetBytes();
            Assert.Equal(3L * GiB, budget);   // 50% of the lower (6 GiB), not the inflated 32 GiB

            // The same cross-checked 6 GiB figure is what HeadroomGuard sees as "available", so a
            // budget sized off it plus the ~1 GB margin fits comfortably - no more mismatch.
            Assert.True(HeadroomGuard.HasHeadroom(budget, out var reason));
            Assert.Null(reason);
        }
        finally
        {
            SystemMemory.GcReader = savedGc;
            SystemMemory.ProcMeminfoReader = savedProc;
        }
    }

    [Fact]
    public async Task QueuedImport_Proceeds_WhenHeadroomSufficient()
    {
        var saved = HeadroomGuard.AvailableBytesReader;
        using var f = new HoarderDbWebFactory();
        var c = await f.NewLoggedInClientAsync();
        File.WriteAllText(Path.Combine(f.Incoming, "ok.txt"), "a\nb\nc\n");

        try
        {
            HeadroomGuard.AvailableBytesReader = () => long.MaxValue / 2;   // plenty of headroom
            var id = await Import(c, "Test_headroom_ok", "ok.txt");
            var j = await WaitTerminal(c, id);
            Assert.Equal("completed", j.GetProperty("status").GetString());
        }
        finally { HeadroomGuard.AvailableBytesReader = saved; }
    }
}

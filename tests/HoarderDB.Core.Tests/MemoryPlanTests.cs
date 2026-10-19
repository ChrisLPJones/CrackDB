using System.Text;
using HoarderDB;
using Xunit;

namespace HoarderDB.Core.Tests;

public class MemoryPlanTests
{
    private const long MiB = 1024 * 1024;
    private const long GiB = 1024 * MiB;

    [Theory]
    [InlineData(256 * MiB)]
    [InlineData(1 * GiB)]
    [InlineData(2 * GiB)]
    [InlineData(4 * GiB)]
    [InlineData(64 * GiB)]
    public void Invariants_HoldForAllBudgets(long budget)
    {
        foreach (var procs in new[] { 1, 2, 4, 16, 64 })
        {
            var p = MemoryPlan.Compute(budget, procs);
            Assert.True(p.MaxBuffers >= 2 && p.MaxBuffers <= 8);
            Assert.True(p.WriteBuffer >= 16ul * MiB && p.WriteBuffer <= 512ul * MiB);
            Assert.True((double)p.MaxBuffers * p.WriteBuffer <= 0.6 * budget + 1 || p.MaxBuffers == 2);
            Assert.True(p.DbWriteBufferCap <= (ulong)budget);
            Assert.True(p.Workers >= 1);
            Assert.True(p.QueueCapacity >= 2);
            Assert.True(p.Flushes >= 1);
            Assert.True(p.ChunkChars > 0);
        }
    }

    [Fact]
    public void TwoGbMachine_GetsBoundedBuffers()
    {
        var p = MemoryPlan.Compute(1 * GiB, 4);   // 2 GB host -> 1 GB budget
        Assert.Equal(4, p.MaxBuffers);
        Assert.InRange(p.WriteBuffer, 128ul * MiB, 160ul * MiB);   // 0.6 * 1 GB / 4, not rounded
        Assert.True(p.DbWriteBufferCap < 700ul * MiB);
        Assert.True(p.Workers <= 3);
    }

    [Fact]
    public void LargeBudget_ReproducesLegacyTuning()
    {
        var p = MemoryPlan.Compute(64 * GiB, 16);
        Assert.Equal(512ul * MiB, p.WriteBuffer);
        Assert.Equal(8, p.MaxBuffers);
        Assert.Equal(8, p.Workers);
        Assert.Equal(24, p.QueueCapacity);
    }

    [Fact]
    public void RequestedThreads_HonoredButClamped()
    {
        Assert.Equal(3, MemoryPlan.Compute(64 * GiB, 16, requestedThreads: 3).Workers);
        Assert.Equal(8, MemoryPlan.Compute(64 * GiB, 64, requestedThreads: 100).Workers);
        Assert.Equal(1, MemoryPlan.Compute(64 * GiB, 1, requestedThreads: 4).Workers);
    }

    [Fact]
    public void DefaultBudget_AtLeast256Mb() => Assert.True(MemoryPlan.DefaultBudgetBytes() >= 256 * MiB);

    // Cross-check coverage for the shared SystemMemory primitive (fix for the budget/headroom
    // mismatch bug): MemoryPlan.DefaultBudgetBytes() must size itself off the same cross-checked
    // "available" number HeadroomGuard uses, not off the GC's raw total-memory reading alone.

    [Fact]
    public void DefaultBudget_UsesLowerOfDivergentGcAndProcReadings()
    {
        var savedGc = SystemMemory.GcReader;
        var savedProc = SystemMemory.ProcMeminfoReader;
        try
        {
            // Simulate the reproduced bug: GC reports a huge host-level reading (e.g. 31.2 GiB on an
            // unconstrained cgroup) while the real, cross-checked figure (/proc/meminfo) is far smaller.
            SystemMemory.GcReader = () => 32L * GiB;
            SystemMemory.ProcMeminfoReader = () => 6L * GiB;

            long budget = MemoryPlan.DefaultBudgetBytes();

            // Must be 50% of the LOWER reading (6 GiB), never 50% of the inflated GC-only reading.
            Assert.Equal(3L * GiB, budget);
        }
        finally
        {
            SystemMemory.GcReader = savedGc;
            SystemMemory.ProcMeminfoReader = savedProc;
        }
    }

    [Fact]
    public void DefaultBudget_FallsBackTo256MbFloor_WhenReadingAmbiguous()
    {
        var saved = SystemMemory.AvailableBytesReader;
        try
        {
            SystemMemory.AvailableBytesReader = () => null;
            Assert.Equal(256 * MiB, MemoryPlan.DefaultBudgetBytes());
        }
        finally { SystemMemory.AvailableBytesReader = saved; }
    }

    private static IEnumerable<string> LongLines(int count, int len)
    {
        for (int i = 0; i < count; i++)
        {
            var head = i.ToString("D8");
            yield return head + new string('x', len - head.Length);
        }
    }

    [Fact]
    public void Import_TinyBudget_StoresDedupsAndSplitsLongLines()
    {
        using var t = new TempDb();
        using (var db = new WordlistDatabase(t.Path, new UTF8Encoding(false), bulkLoad: true, memoryBudgetBytes: 256 * MiB))
        {
            // 1500 x 8000 chars = 12M chars -> several size-capped chunks.
            var lines = LongLines(1500, 8000).Concat(LongLines(1500, 8000)).Concat(new[] { "abc", "abc", "zz" });
            long read = db.Import(lines, null, null);
            Assert.Equal(3003, read);
            Assert.Equal(1502, db.CountRange(null, null));
            Assert.Equal(1500, db.CountRange(8000, 8000));
            Assert.True(db.Contains("abc"));
            Assert.False(db.Contains("nope"));
        }
    }

    [Fact]
    public void FreshDb_DefaultAndSmallBudget_CreateImportRead()
    {
        using var t = new TempDb();
        using (var db = new WordlistDatabase(t.Path, new UTF8Encoding(false), bulkLoad: true, memoryBudgetBytes: 256 * MiB))
            db.Import(new[] { "alpha", "beta", "alpha" }, null, null);
        using (var db = new WordlistDatabase(t.Path, new UTF8Encoding(false), bulkLoad: true))   // default budget
            db.Import(new[] { "gamma", "beta" }, null, null);
        using var read = t.Open();
        Assert.Equal(3, read.CountRange(null, null));
        Assert.True(read.Contains("gamma"));
        Assert.Equal(new[] { "beta", "alpha", "gamma" }.OrderBy(x => x.Length).ThenBy(x => x, StringComparer.Ordinal), read.Range(null, null, null));
    }
}

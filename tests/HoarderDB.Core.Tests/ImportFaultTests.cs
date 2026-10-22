using System.Text;
using Xunit;

namespace HoarderDB.Core.Tests;

/// <summary>Import must end (and rethrow) when the line source or every writer faults, never hang.</summary>
public class ImportFaultTests
{
    private sealed class SourceFault : Exception { public SourceFault() : base("source") { } }
    private sealed class WriteFault : Exception { public WriteFault() : base("write") { } }

    private static IEnumerable<string> Lines(long count, long throwAt = -1)
    {
        for (long i = 0; i < count; i++)
        {
            if (i == throwAt) throw new SourceFault();
            yield return "w" + i;
        }
    }

    private static Exception? RunWithTimeout(Func<long> import)
    {
        var task = Task.Run(import);
        try { Assert.True(task.Wait(TimeSpan.FromSeconds(20)), "Import hung"); }
        catch (AggregateException ae) { return ae.InnerException; }
        return null;
    }

    [Fact]
    public void Import_SourceThrows_RethrowsAndDoesNotHang()
    {
        using var t = new TempDb();
        using var db = t.Open();
        var ex = RunWithTimeout(() => db.Import(Lines(120_000, throwAt: 60_000), null, null, threads: 2));
        Assert.IsType<SourceFault>(ex);
    }

    [Fact]
    public void Import_EveryWorkerWriteFaults_RethrowsAndDoesNotHang()
    {
        using var t = new TempDb();
        using var db = t.Open();
        db.WriteFaultHook = () => throw new WriteFault();
        // Far more chunks than queue capacity + workers, so a dead consumer set blocks the producer.
        long total = 50_000L * (db.Plan.QueueCapacity + db.Plan.Workers + 4);
        var ex = RunWithTimeout(() => db.Import(Lines(total), null, null, threads: 2));
        Assert.IsType<WriteFault>(ex);
    }

    [Fact]
    public void Import_AfterFault_SucceedsOnRerun()
    {
        using var t = new TempDb();
        using var db = t.Open();
        var ex = RunWithTimeout(() => db.Import(Lines(120_000, throwAt: 60_000), null, null, threads: 2));
        Assert.NotNull(ex);
        Assert.Equal(1000, db.Import(Lines(1000), null, null, threads: 2));
        Assert.True(db.Contains("w999"));
    }

    [Fact]
    public void FreshInstall_CreateImportReadCountCompactCount()
    {
        using var t = new TempDb();
        Assert.False(Directory.Exists(t.Path));
        using var db = t.Open();
        Assert.Equal(5000, db.Import(Lines(5000), null, null, threads: 2));
        Assert.True(db.Contains("w4999"));
        Assert.False(db.Contains("nope"));
        Assert.Equal(5000, db.CountRange(null, null));
        db.Compact();
        Assert.Equal(5000, db.CountRange(null, null));
    }
}

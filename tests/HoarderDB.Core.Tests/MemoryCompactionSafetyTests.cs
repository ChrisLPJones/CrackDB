using System.Text;
using HoarderDB;
using Xunit;

#pragma warning disable xUnit1031

namespace HoarderDB.Core.Tests;

/// <summary>Read opens never auto-compact, the parallel-scan L0 gate, MaxSubcompactions, and the diagnostics parsers.</summary>
public class MemoryCompactionSafetyTests
{
    private static IEnumerable<string> Words(int from, int count)
    {
        for (int i = from; i < from + count; i++) yield return "w" + i.ToString("D8");
    }

    private static void ImportRounds(string path, int rounds, int perRound = 300)
    {
        for (int r = 0; r < rounds; r++)
        {
            using var db = new WordlistDatabase(path, new UTF8Encoding(false), bulkLoad: true);
            db.Import(Words(r * perRound, perRound), null, null, threads: 2);
        }
    }

    [Fact]
    public void ReadOpen_AutoOff_DoesNotDropL0_AndCompactStillShrinksIt()
    {
        using var t = new TempDb();
        ImportRounds(t.Path, 8);
        using var db = new WordlistDatabase(t.Path, new UTF8Encoding(false), enableAutoCompaction: false);
        long before = db.NumFilesAtLevel0();
        Assert.True(before >= 8, $"expected >= 8 L0 files, got {before}");
        Thread.Sleep(500);   // an armed auto-compaction would have fired by now
        Assert.Equal(before, db.NumFilesAtLevel0());
        db.Compact();
        Assert.True(db.NumFilesAtLevel0() <= 1, $"manual compact must work with auto off, got {db.NumFilesAtLevel0()}");
        Assert.Equal(2400, db.CountRange(null, null));
    }

    [Fact]
    public void Gate_Above16L0Files_UsesSinglePath_ResultsEqualReference()
    {
        using var t = new TempDb();
        ImportRounds(t.Path, WordlistDatabase.ParallelMaxL0Files + 4);
        using var db = t.Open(new ScanLimiter(4));
        Assert.True(db.NumFilesAtLevel0() > WordlistDatabase.ParallelMaxL0Files);
        int tiles = 0;
        db.TileHook = _ => Interlocked.Increment(ref tiles);
        var dist = db.LengthDistributionCore(threads: 4, minKeys: 1, splitBytesOverride: null, default, null).ToArray();
        long count = db.CountRangeCore(null, null, threads: 4, minKeys: 1, splitBytesOverride: null, default);
        Assert.Equal(0, tiles);   // gate held: no tile iterators
        Assert.Equal(db.LengthDistributionReference().ToArray(), dist);
        Assert.Equal(db.CountRangeReference(null, null), count);
    }

    [Fact]
    public void Gate_AfterCompact_GoesParallelAgain_ResultsEqualReference()
    {
        using var t = new TempDb();
        ImportRounds(t.Path, WordlistDatabase.ParallelMaxL0Files + 4);
        using var db = t.Open(new ScanLimiter(4));
        db.Compact();
        Assert.True(db.NumFilesAtLevel0() <= WordlistDatabase.ParallelMaxL0Files);
        int tiles = 0;
        db.TileHook = _ => Interlocked.Increment(ref tiles);
        var dist = db.LengthDistributionCore(threads: 4, minKeys: 1, splitBytesOverride: null, default, null).ToArray();
        Assert.True(tiles > 0);
        Assert.Equal(db.LengthDistributionReference().ToArray(), dist);
    }

    [Fact]
    public void MaxSubcompactions_DefaultIs1_ExplicitHonored_Clamped()
    {
        using var t = new TempDb();
        using (var db = t.Open(new ScanLimiter(3))) Assert.Equal(1, db.MaxSubcompactions);   // not ScanThreads
        using (var db = new WordlistDatabase(t.Path, new UTF8Encoding(false), maxSubcompactions: 4)) Assert.Equal(4, db.MaxSubcompactions);
        using (var db = new WordlistDatabase(t.Path, new UTF8Encoding(false), maxSubcompactions: 1000)) Assert.Equal(ScanLimiter.MaxCap, db.MaxSubcompactions);
        using (var db = new WordlistDatabase(t.Path, new UTF8Encoding(false), maxSubcompactions: -3)) Assert.Equal(1, db.MaxSubcompactions);
    }

    [Fact]
    public void FreshInstall_Create_Import_Read_Compact_Read()
    {
        using var t = new TempDb();
        using (var db = new WordlistDatabase(t.Path, new UTF8Encoding(false), bulkLoad: true))
            db.Import(Words(0, 500), null, null, threads: 2);
        using var read = new WordlistDatabase(t.Path, new UTF8Encoding(false), enableAutoCompaction: false, maxSubcompactions: 1);
        Assert.True(read.Contains("w00000042"));
        Assert.Equal(500, read.CountRange(null, null));
        read.Compact();
        Assert.True(read.Contains("w00000042"));
        Assert.Equal(500, read.CountRange(null, null));
        Assert.True(read.TotalSstFilesBytes() > 0);
        Assert.NotNull(MemoryDiagnostics.ParseIndexBytes(read.AggregatedTableProperties()));
    }

    // ---- parsers -----------------------------------------------------------------------------

    [Fact]
    public void ProcStatus_Parses_RssAnon_And_VmHwm()
    {
        const string s = "Name:\thoarderdb\nVmHWM:\t  2048 kB\nVmRSS:\t 1000 kB\nRssAnon:\t    1536 kB\nRssFile:\t 10 kB\n";
        Assert.Equal(1536L * 1024, MemoryDiagnostics.ParseProcStatusKb(s, "RssAnon"));
        Assert.Equal(2048L * 1024, MemoryDiagnostics.ParseProcStatusKb(s, "VmHWM"));
        Assert.Null(MemoryDiagnostics.ParseProcStatusKb(s, "Missing"));
        Assert.Null(MemoryDiagnostics.ParseProcStatusKb("RssAnon: abc kB", "RssAnon"));
        Assert.Null(MemoryDiagnostics.ParseProcStatusKb(null, "RssAnon"));
        Assert.Null(MemoryDiagnostics.ParseProcStatusKb("RssAnon: 99999999999999999999 kB", "RssAnon"));
    }

    [Theory]
    [InlineData("8589934592\n", 8589934592L)]
    [InlineData("max\n", null)]
    [InlineData("", null)]
    [InlineData("garbage", null)]
    [InlineData("-1", null)]
    [InlineData("9223372036854771712", null)]   // cgroup v1 "no limit"
    public void CgroupLimit_Parses(string text, long? expected) => Assert.Equal(expected, MemoryDiagnostics.ParseCgroupLimit(text));

    [Fact]
    public void IndexBytes_ParsedFromSampleAndDefensive()
    {
        const string sample = "# data blocks=100 # entries=3 # deletions=0 raw key size=12 data block size=4096 index block size (user-key? 0, delta-value? 0)=9437184 filter block size=0 (estimated) table size=2000 filter policy name=N/A";
        Assert.Equal(9437184L, MemoryDiagnostics.ParseIndexBytes(sample));
        Assert.Equal(77L, MemoryDiagnostics.ParseIndexBytes("a=1 index_size=77 b=2"));
        Assert.Null(MemoryDiagnostics.ParseIndexBytes("no such field=5"));
        Assert.Null(MemoryDiagnostics.ParseIndexBytes("index block size=abc"));
        Assert.Null(MemoryDiagnostics.ParseIndexBytes("index block size"));
        Assert.Null(MemoryDiagnostics.ParseIndexBytes(null));
    }

    [Fact]
    public void IndexWarning_UsesHalfOfCgroupLimit_SkippedWithoutLimit()
    {
        long gb = 1L << 30;
        Assert.True(MemoryDiagnostics.IndexExceedsHalfLimit(2 * gb, 3, 8 * gb));    // 8 GB > 4 GB
        Assert.False(MemoryDiagnostics.IndexExceedsHalfLimit(1 * gb, 3, 8 * gb));   // 4 GB == half, not over
        Assert.False(MemoryDiagnostics.IndexExceedsHalfLimit(100 * gb, 3, null));
        Assert.False(MemoryDiagnostics.IndexExceedsHalfLimit(null, 3, 8 * gb));
    }

    private sealed class FakeReader(Func<string, string?> f) : ITextFileReader
    {
        public string? TryReadAllText(string path) => f(path);
    }

    [Fact]
    public void Readers_UseFixedPaths_AndNeverThrow()
    {
        var ok = new FakeReader(p => p switch
        {
            MemoryDiagnostics.ProcStatusPath => "RssAnon: 10 kB\nVmHWM: 20 kB\n",
            MemoryDiagnostics.CgroupV2MemoryMax => "max\n",
            MemoryDiagnostics.CgroupV1MemoryLimit => "4294967296",
            MemoryDiagnostics.CgroupV2MemoryHigh => "1073741824",
            _ => null
        });
        var m = MemoryDiagnostics.ReadProcessMemory(ok);
        Assert.Equal(10 * 1024, m.RssAnonBytes);
        Assert.Equal(20 * 1024, m.VmHwmBytes);
        Assert.Equal(4294967296L, MemoryDiagnostics.ReadCgroupMemoryMax(ok));   // v2 "max" falls to v1
        Assert.Equal(1073741824L, MemoryDiagnostics.ReadCgroupMemoryHigh(ok));

        var boom = new FakeReader(_ => throw new IOException("denied"));
        Assert.Null(MemoryDiagnostics.ReadProcessMemory(boom).RssAnonBytes);
        Assert.Null(MemoryDiagnostics.ReadCgroupMemoryMax(boom));
        Assert.Null(MemoryDiagnostics.ReadCgroupMemoryHigh(boom));
        Assert.Null(MemoryDiagnostics.ReadCgroupMemoryMax(new FakeReader(_ => null)));
        Assert.Null(new FileTextReader().TryReadAllText("/nonexistent/path/for/test"));
    }
}

using System.Text;
using HoarderDB;
using Xunit;

namespace HoarderDB.Core.Tests;

/// <summary>
/// Regression coverage for the sequential-import memory fix: auto-compaction suppression (A),
/// the fixed block-cache/max-open-files bounds (B). The headroom backstop (C) lives in
/// HoarderDB.Web.Tests since it's wired into JobQueue, not WordlistDatabase.
/// </summary>
public class WordlistDatabaseMemoryFixTests
{
    private static IEnumerable<string> Words(int from, int count)
    {
        for (int i = from; i < from + count; i++) yield return "w" + i.ToString("D8");
    }

    [Fact]
    public void AutoCompactionSuppressed_RepeatedImports_KeepL0FilesFlat_NoExplicitCompact()
    {
        using var t = new TempDb();
        int after;
        using (var db = new WordlistDatabase(t.Path, new UTF8Encoding(false), bulkLoad: true))
            db.Import(Words(0, 2000), null, null, threads: 2);

        // Reopen repeatedly the way DbManager's post-job reopen does: enableAutoCompaction:false,
        // interleaved with more bulk imports and no explicit Compact() call.
        for (int round = 1; round <= 6; round++)
        {
            using (var reopened = new WordlistDatabase(t.Path, new UTF8Encoding(false), enableAutoCompaction: false)) { }
            using var db = new WordlistDatabase(t.Path, new UTF8Encoding(false), bulkLoad: true);
            db.Import(Words(round * 2000, 2000), null, null, threads: 2);
        }

        using (var final = new WordlistDatabase(t.Path, new UTF8Encoding(false), enableAutoCompaction: false))
        {
            after = (int)final.NumFilesAtLevel0();
            Assert.True(after >= 6, $"expected several uncompacted L0 files to have piled up, got {after}");
        }
    }

    [Fact]
    public void AutoCompactionEnabled_OrExplicitCompact_CollapsesL0Files()
    {
        using var t = new TempDb();
        for (int round = 0; round < 6; round++)
        {
            using var db = new WordlistDatabase(t.Path, new UTF8Encoding(false), bulkLoad: true);
            db.Import(Words(round * 2000, 2000), null, null, threads: 2);
        }

        // An explicit compact collapses the piled-up L0 files, same as opening with
        // enableAutoCompaction:true does (today's CompactRunner behaviour).
        using var read = new WordlistDatabase(t.Path, new UTF8Encoding(false), enableAutoCompaction: true);
        read.Compact();
        Assert.True(read.NumFilesAtLevel0() <= 1, $"expected compact to collapse L0 files, got {read.NumFilesAtLevel0()}");
        Assert.Equal(12000, read.CountRange(null, null));
    }

    [Fact]
    public void BlockCache_IsFiniteAndMatchesFixedDefault()
    {
        using var t = new TempDb();
        using var db = t.Open();
        long capacity = db.BlockCacheCapacityBytes();
        Assert.Equal(WordlistDatabase.BlockCacheBytes, capacity);
        Assert.True(capacity > 0 && capacity < 10L * 1024 * 1024 * 1024, "block cache must be a small, finite cap, not unbounded");
    }

    [Fact]
    public void MaxOpenFiles_FixedDefault_IsFiniteNotUnlimited()
    {
        // RocksDB's own "unlimited" sentinel is -1; our fixed default must be a real, finite cap.
        Assert.True(WordlistDatabase.MaxOpenFilesCap > 0);
        Assert.True(WordlistDatabase.MaxOpenFilesCap < 1_000_000, "max open files must be a modest, finite cap");
    }

    [Fact]
    public void FromScratch_SequentialImportsNoExplicitCompact_DataCorrectAndVisible()
    {
        // Required from-scratch check: a brand-new empty Test_ db, several small sequential
        // imports with no explicit compact, reading before/after each - confirms the
        // auto-compaction suppression (A) doesn't affect read visibility or correctness.
        using var t = new TempDb();

        using (var db = new WordlistDatabase(t.Path, new UTF8Encoding(false), bulkLoad: true))
            db.Import(new[] { "alpha", "beta", "gamma" }, null, null, threads: 2);

        using (var read1 = new WordlistDatabase(t.Path, new UTF8Encoding(false), enableAutoCompaction: false))
        {
            Assert.True(read1.Contains("alpha"));
            Assert.True(read1.Contains("beta"));
            Assert.False(read1.Contains("delta"));
            Assert.Equal(3, read1.CountRange(null, null));
        }

        using (var db = new WordlistDatabase(t.Path, new UTF8Encoding(false), bulkLoad: true))
            db.Import(new[] { "delta", "beta" /* dup */, "epsilon" }, null, null, threads: 2);   // no compact in between

        using (var read2 = new WordlistDatabase(t.Path, new UTF8Encoding(false), enableAutoCompaction: false))
        {
            Assert.True(read2.Contains("alpha"));   // still visible from the first import
            Assert.True(read2.Contains("delta"));   // newly imported, visible despite no compact
            Assert.True(read2.Contains("epsilon"));
            Assert.Equal(5, read2.CountRange(null, null));   // beta deduped, not double-counted
            Assert.Equal(new[] { "beta", "delta", "alpha", "gamma", "epsilon" }
                    .OrderBy(x => x.Length).ThenBy(x => x, StringComparer.Ordinal),
                read2.Range(null, null, null));
        }
    }
}

using System.Diagnostics;
using System.Text;
using HoarderDB;
using Xunit;
using Xunit.Abstractions;

namespace HoarderDB.Core.Tests;

/// <summary>The held-iterator cursor must walk exactly what the old <c>Scan</c> walks. Throwaway Test_&lt;guid&gt; dbs only.</summary>
public sealed class ScanCursorTests(ITestOutputHelper output)
{
    private static List<string> Collect(WordlistDatabase.ScanCursor c, byte[]? after, int limit = int.MaxValue)
    {
        var l = new List<string>();
        c.SeekTo(after);
        while (l.Count < limit && c.MoveNext()) l.Add(c.Word);
        return l;
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(8, 9)]
    [InlineData(4, null)]
    [InlineData(25, 25)]
    [InlineData(1, 3)]
    public void Cursor_EqualsScan_ForRanges(int? min, int? max)
    {
        using var t = new TempDb();
        using var db = t.Open();
        db.Import(ParallelScanTests.SkewedWords(), null, null, threads: 2);
        var expected = db.Scan(min, max, null).ToList();
        using var cur = db.OpenScan(min, max);
        Assert.Equal(expected, Collect(cur, null));
    }

    [Fact]
    public void Cursor_ResumesExactlyFromRawKey_AcrossManyYields_NoKeyLostOrRepeated()
    {
        using var t = new TempDb();
        using var db = t.Open();
        db.Import(ParallelScanTests.SkewedWords(), null, null, threads: 2);
        var expected = db.Scan(null, null, null).ToList();

        var got = new List<string>();
        byte[]? resume = null;
        int segments = 0;
        long before = WordlistDatabase.CursorsOpened;
        while (true)
        {
            using var cur = db.OpenScan(null, null);   // a fresh cursor per "lease segment"
            cur.SeekTo(resume);
            int n = 0;
            bool more = false;
            while (cur.MoveNext())
            {
                got.Add(cur.Word);
                if (++n == 7919) { resume = cur.KeyCopy(); more = true; break; }
            }
            segments++;
            if (!more) break;
        }
        Assert.True(segments >= 3);
        Assert.Equal(segments, WordlistDatabase.CursorsOpened - before);
        Assert.Equal(expected, got);
    }

    [Fact]
    public void Cursor_SeekToExistingAndMissingKey_StartsStrictlyAfter()
    {
        using var t = new TempDb();
        using var db = t.Open();
        db.Import(new[] { "aaaa", "aaab", "aaac", "bbbb" }, null, null, threads: 1);
        using var cur = db.OpenScan(null, null);
        cur.SeekTo(null);
        Assert.True(cur.MoveNext());
        var key = cur.KeyCopy();
        Assert.Equal("aaaa", cur.Word);
        Assert.Equal(new[] { "aaab", "aaac", "bbbb" }, Collect(cur, key));
        // A resume key that is gone (never happens in the app) still lands on the next greater key.
        var missing = key.Concat(new byte[] { 0x21 }).ToArray();   // [4]"aaaa!" sorts between aaaa and aaab
        Assert.Equal(new[] { "aaab", "aaac", "bbbb" }, Collect(cur, missing));
    }

    [Fact]
    public void Cursor_SingleIterator_ForBigSstPlusManySmallL0Files()
    {
        using var t = new TempDb();
        using var db = new WordlistDatabase(t.Path, new UTF8Encoding(false), enableAutoCompaction: false);   // as the web app: L0 files pile up
        var rnd = new Random(7);
        string W(int i) => "big" + i.ToString("D7");
        db.Import(Enumerable.Range(0, 200_000).Select(W), null, null, threads: 2);
        db.Compact();                                   // one big file below
        for (int f = 0; f < 20; f++)                    // ~20 small L0 files above
            db.Import(Enumerable.Range(0, 300).Select(i => "s" + f.ToString("D2") + "w" + rnd.Next(1_000_000).ToString("D7")), null, null, threads: 1);
        Assert.True(db.NumFilesAtLevel0() >= 10, "fixture should have many small L0 files");
        var expected = db.Scan(null, null, null).ToList();

        long before = WordlistDatabase.CursorsOpened;
        using var cur = db.OpenScan(null, null);
        var got = Collect(cur, null);
        Assert.Equal(1, WordlistDatabase.CursorsOpened - before);   // one iterator for the whole segment
        Assert.Equal(expected, got);
    }

    // ---- search tiles ---------------------------------------------------------------------------

    private static List<string> WalkTiles(WordlistDatabase db, int? min, int? max, List<WordlistDatabase.Tile> tiles)
    {
        var got = new List<string>();
        using var cur = db.OpenScan(min, max);       // ONE iterator reseeked per tile, as a search worker does
        foreach (var t in tiles)
        {
            cur.SeekToLower(t.Lower);
            while (cur.MoveNext() && (t.Upper == null || cur.Key.SequenceCompareTo(t.Upper) < 0)) got.Add(cur.Word);
        }
        return got;
    }

    [Theory]
    [InlineData(false, null, null)]
    [InlineData(true, null, null)]
    [InlineData(true, 8, 9)]
    [InlineData(true, 4, null)]
    [InlineData(true, 25, 25)]
    public void PlanSearchTiles_CoverTheRangeOnce_InKeyOrder_WithNoProbes(bool compacted, int? min, int? max)
    {
        using var t = new TempDb();
        using var db = new WordlistDatabase(t.Path, new UTF8Encoding(false), enableAutoCompaction: false);
        var words = ParallelScanTests.SkewedWords();
        int n = words.Count / 5 + 1;
        for (int i = 0; i < words.Count; i += n) db.Import(words.Skip(i).Take(n), null, null, threads: 1);
        if (compacted) db.Compact();
        var expected = db.Scan(min, max, null).ToList();
        foreach (long? target in new long?[] { null, 1 })
        {
            var tiles = db.PlanSearchTiles(min, max, 3, default, target);
            Assert.Equal(0, db.LastPlanProbeRanges);            // metadata only: a probe would re-read a huge file's index
            if (target == 1 && min == null && max == null) Assert.True(tiles.Count >= 5, "tiles: " + tiles.Count);
            Assert.Equal(expected, WalkTiles(db, min, max, tiles));
        }
    }

    [Fact]
    public void PlanSearchTiles_SingleTile_WhenOneThread_OrTooManyL0Files()
    {
        using var t = new TempDb();
        using var db = new WordlistDatabase(t.Path, new UTF8Encoding(false), enableAutoCompaction: false);
        for (int f = 0; f < 20; f++) db.Import(Enumerable.Range(0, 200).Select(i => "w" + f.ToString("D2") + i.ToString("D4")), null, null, threads: 1);
        Assert.True(db.NumFilesAtLevel0() > WordlistDatabase.ParallelMaxL0Files);
        Assert.False(db.ParallelScanAllowed);
        Assert.Single(db.PlanSearchTiles(null, null, 3, default, 1));
        Assert.Single(db.PlanSearchTiles(null, null, 1, default, 1));
    }

    [Fact]
    public void SeekToLower_IsInclusive_WhereSeekToIsStrictlyAfter()
    {
        using var t = new TempDb();
        using var db = t.Open();
        db.Import(new[] { "aaaa", "aaab", "aaac" }, null, null, threads: 1);
        using var cur = db.OpenScan(null, null);
        cur.SeekTo(null); Assert.True(cur.MoveNext());
        var first = cur.KeyCopy();
        cur.SeekToLower(first);
        Assert.True(cur.MoveNext());
        Assert.Equal("aaaa", cur.Word);
        cur.SeekTo(first);
        Assert.True(cur.MoveNext());
        Assert.Equal("aaab", cur.Word);
    }

    [Fact]
    public void ScanLimiter_TryAcquireUpTo_NeverBlocks_AndNeverExceedsTheCap()
    {
        var l = new ScanLimiter(3);
        Assert.Equal(3, l.TryAcquireUpTo(8));
        Assert.Equal(0, l.TryAcquireUpTo(1));
        l.Release(1);
        Assert.Equal(1, l.TryAcquireUpTo(3));
        Assert.Equal(0, l.Free);
        l.Release(3);
        Assert.Equal(3, l.Free);
    }

    /// <summary>
    /// Timing spike for the plan (not a regression test): 20 x (new iterator + Seek) vs 20 x Seek on a held iterator,
    /// on one large compacted SST. Skipped unless HOARDERDB_SPIKE_MB is set (e.g. 250). Numbers go to the test output.
    /// </summary>
    [Fact]
    public void Spike_NewIteratorPlusSeek_VsHeldIteratorSeek()
    {
        if (!int.TryParse(Environment.GetEnvironmentVariable("HOARDERDB_SPIKE_MB"), out int mb) || mb <= 0) return;
        using var t = new TempDb();
        using var db = t.Open();
        var rnd = new Random(99);
        long target = mb * 1024L * 1024;
        const int batch = 2_000_000;
        int n = 0;
        while (true)
        {
            db.Import(Enumerable.Range(0, batch).Select(_ => RandWord(rnd)), null, null, threads: 2);
            n++;
            long size = Dir(t.Path);
            if (size >= target) break;
        }
        db.Compact();
        output.WriteLine($"fixture: {Dir(t.Path) / 1024 / 1024} MB on disk, files at L0 {db.NumFilesAtLevel0()}");
        var seeks = Enumerable.Range(0, 20).Select(_ => rnd.Next(8, 12)).ToArray();
        byte[] KeyFor(int i) { var w = RandWord(new Random(i)); return Key(w); }
        // new iterator + seek each time
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 20; i++)
        {
            using var c = db.OpenScan(null, null);
            c.SeekTo(KeyFor(i));
            c.MoveNext();
        }
        double fresh = sw.Elapsed.TotalMilliseconds;
        using (var held = db.OpenScan(null, null))
        {
            sw.Restart();
            for (int i = 0; i < 20; i++) { held.SeekTo(KeyFor(i)); held.MoveNext(); }
            output.WriteLine($"20 x new iterator+Seek: {fresh:F1} ms ({fresh / 20:F2} ms each); 20 x Seek on held iterator: {sw.Elapsed.TotalMilliseconds:F1} ms ({sw.Elapsed.TotalMilliseconds / 20:F2} ms each)");
        }
    }

    private static string RandWord(Random r)
    {
        const string a = "abcdefghijklmnopqrstuvwxyz0123456789";
        int len = 8 + r.Next(5);
        return string.Create(len, r, (s, rr) => { for (int i = 0; i < s.Length; i++) s[i] = a[rr.Next(a.Length)]; });
    }

    private static byte[] Key(string w)
    {
        var b = Encoding.UTF8.GetBytes(w);
        var k = new byte[4 + b.Length];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(k, w.Length);
        b.CopyTo(k, 4);
        return k;
    }

    private static long Dir(string p) => new DirectoryInfo(p).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
}

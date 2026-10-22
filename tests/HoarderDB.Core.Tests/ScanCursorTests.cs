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

using System.Text;
using CrackDB;
using Xunit;

namespace CrackDB.Core.Tests;

/// <summary>
/// Owns a throwaway RocksDB directory named Test_&lt;guid&gt; under the system temp
/// folder. Refuses to run on any other path so a real database is never touched.
/// </summary>
public sealed class TempDb : IDisposable
{
    public string Path { get; }

    public TempDb()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Test_" + Guid.NewGuid());
        EnsureThrowaway(Path);
    }

    public static void EnsureThrowaway(string path)
    {
        var full = System.IO.Path.GetFullPath(path);
        var parent = System.IO.Path.GetFullPath(System.IO.Path.GetTempPath()).TrimEnd(System.IO.Path.DirectorySeparatorChar);
        var dir = System.IO.Path.GetDirectoryName(full)!.TrimEnd(System.IO.Path.DirectorySeparatorChar);
        if (!System.IO.Path.GetFileName(full).StartsWith("Test_", StringComparison.Ordinal) || dir != parent)
            throw new InvalidOperationException($"Refusing to use non-throwaway path: {path}");
    }

    public WordlistDatabase Open() => new(Path, new UTF8Encoding(false));

    public void Dispose()
    {
        EnsureThrowaway(Path);
        if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
    }
}

public class WordlistDatabaseTests
{
    private static readonly string[] Sample = { "abc", "ab", "zz", "hunter2", "abcd", "b", "longerword" };

    private static WordlistDatabase Seed(TempDb t, IEnumerable<string>? words = null)
    {
        var db = t.Open();
        db.Import(words ?? Sample, null, null, threads: 2);
        return db;
    }

    [Fact]
    public void Import_DedupsWithinFile()
    {
        using var t = new TempDb();
        using var db = Seed(t, new[] { "abc", "abc", "abc", "xyz" });
        Assert.Equal(2, db.CountRange(null, null));
    }

    [Fact]
    public void Import_DedupsAcrossImports()
    {
        using var t = new TempDb();
        using var db = t.Open();
        db.Import(new[] { "abc", "def" }, null, null, threads: 2);
        db.Import(new[] { "def", "ghi" }, null, null, threads: 2);
        Assert.Equal(3, db.CountRange(null, null));
    }

    [Fact]
    public void Import_SkipsBlankLinesAndTrimsCarriageReturn()
    {
        using var t = new TempDb();
        using var db = Seed(t, new[] { "", "abc\r", "abc", "\r" });
        Assert.Equal(new[] { "abc" }, db.Range(null, null, null).ToArray());
    }

    [Fact]
    public void Contains_TrueForStored_FalseForMissing()
    {
        using var t = new TempDb();
        using var db = Seed(t);
        Assert.True(db.Contains("hunter2"));
        Assert.False(db.Contains("nope"));
        Assert.False(db.Contains("hunter"));   // prefix of a stored word is not a match
    }

    [Fact]
    public void CountRange_RespectsBounds()
    {
        using var t = new TempDb();
        using var db = Seed(t);
        Assert.Equal(7, db.CountRange(null, null));
        Assert.Equal(2, db.CountRange(2, 2));       // ab, zz
        Assert.Equal(2, db.CountRange(3, 4));       // abc, abcd
    }

    [Fact]
    public void Range_RespectsMinMax()
    {
        using var t = new TempDb();
        using var db = Seed(t);
        Assert.Equal(new[] { "ab", "zz", "abc" }, db.Range(2, 3, null).ToArray());
        Assert.Equal(new[] { "hunter2", "longerword" }, db.Range(7, null, null).ToArray());
        Assert.Equal(new[] { "b" }, db.Range(null, 1, null).ToArray());
        Assert.Empty(db.Range(50, 60, null));
    }

    [Fact]
    public void Range_OrderedByLengthThenAscending()
    {
        using var t = new TempDb();
        using var db = Seed(t);
        var expected = new[] { "b", "ab", "zz", "abc", "abcd", "hunter2", "longerword" };
        Assert.Equal(expected, db.Range(null, null, null).ToArray());
    }

    [Fact]
    public void Range_HonorsLimit()
    {
        using var t = new TempDb();
        using var db = Seed(t);
        Assert.Equal(new[] { "b", "ab" }, db.Range(null, null, 2).ToArray());
    }

    [Fact]
    public void Import_MinMaxLengthFiltering()
    {
        using var t = new TempDb();
        using var db = t.Open();
        db.Import(Sample, 3, 4, threads: 2);
        Assert.Equal(new[] { "abc", "abcd" }, db.Range(null, null, null).ToArray());
        Assert.False(db.Contains("ab"));
        Assert.False(db.Contains("hunter2"));
    }

    [Fact]
    public void LengthDistribution_ReportsExactCounts()
    {
        using var t = new TempDb();
        using var db = Seed(t);
        var dist = db.LengthDistribution().ToArray();
        Assert.Equal((1, 1L), dist[0]);
        Assert.Contains((2, 2L), dist);
        Assert.Equal(7L, dist.Sum(d => d.count));
    }

    [Fact]
    public void EstimatedCount_IncreasesAfterImport()
    {
        using var t = new TempDb();
        using var db = t.Open();
        long before = db.EstimatedCount();
        db.Import(Sample, null, null, threads: 2);
        long after = db.EstimatedCount();
        Assert.True(after > before, $"before={before} after={after}");
    }

    [Fact]
    public void Data_PersistsAcrossReopen()
    {
        using var t = new TempDb();
        using (var db = Seed(t)) { }
        using var db2 = t.Open();
        Assert.True(db2.Contains("abc"));
    }

    [Fact]
    public void TempDb_RefusesNonThrowawayPath()
    {
        Assert.Throws<InvalidOperationException>(() => TempDb.EnsureThrowaway("crackdb"));
        Assert.Throws<InvalidOperationException>(() => TempDb.EnsureThrowaway(Path.Combine(Path.GetTempPath(), "realdb")));
        Assert.Throws<InvalidOperationException>(() => TempDb.EnsureThrowaway("/data/Test_x"));
    }

    // ---- Scan (substring-search support) ----

    private static readonly string[] SearchWords =
        { "password1", "password123", "xpassword1", "PASSWORD1", "nomatch", "short", "pw" };

    private static List<string> ContainsSubstring(IEnumerable<string> words, string query, bool ignoreCase) =>
        words.Where(w => w.Contains(query, ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)).ToList();

    [Fact]
    public void Scan_NoResume_ReturnsEverythingInLengthOrder_LikeRange()
    {
        using var t = new TempDb();
        using var db = Seed(t, SearchWords);
        Assert.Equal(db.Range(null, null, null).ToArray(), db.Scan(null, null, null).ToArray());
    }

    [Fact]
    public void Scan_SubstringMatch_StartMiddleEnd_CaseInsensitive()
    {
        using var t = new TempDb();
        using var db = Seed(t, SearchWords);
        // query length narrows the scan the same way the search endpoint will: min = query length.
        var all = db.Scan(min: "password1".Length, max: null, resumeAfterWord: null).ToList();
        var matches = ContainsSubstring(all, "password1", ignoreCase: true);
        Assert.Equal(
            new[] { "password1", "password123", "xpassword1", "PASSWORD1" }.OrderBy(w => w, StringComparer.Ordinal),
            matches.OrderBy(w => w, StringComparer.Ordinal));
    }

    [Fact]
    public void Scan_SubstringMatch_CaseSensitive_ExcludesDifferentCasing()
    {
        using var t = new TempDb();
        using var db = Seed(t, SearchWords);
        var all = db.Scan(min: "password1".Length, max: null, resumeAfterWord: null).ToList();
        var matches = ContainsSubstring(all, "password1", ignoreCase: false);
        Assert.DoesNotContain("PASSWORD1", matches);
        Assert.Contains("password1", matches);
        Assert.Contains("password123", matches);
        Assert.Contains("xpassword1", matches);
    }

    [Fact]
    public void Scan_SubstringMatch_NegativeQuery_FindsNothing()
    {
        using var t = new TempDb();
        using var db = Seed(t, SearchWords);
        var all = db.Scan(min: "zzzznotfound".Length, max: null, resumeAfterWord: null).ToList();
        Assert.Empty(ContainsSubstring(all, "zzzznotfound", ignoreCase: true));
    }

    [Fact]
    public void Scan_LengthNarrowing_SkipsEntriesShorterThanTheQuery()
    {
        using var t = new TempDb();
        using var db = Seed(t, SearchWords);
        // "pw" (length 2) is shorter than the query length (9); it must never be scanned or returned,
        // even though a naive substring check on it would just fail anyway - the point is Seek skips it.
        var scanned = db.Scan(min: 9, max: null, resumeAfterWord: null).ToList();
        Assert.DoesNotContain("pw", scanned);
        Assert.DoesNotContain("short", scanned);
        Assert.All(scanned, w => Assert.True(w.Length >= 9));
    }

    [Fact]
    public void Scan_Resume_ContinuesAfterTheGivenWord_NoDuplicatesNoGaps()
    {
        using var t = new TempDb();
        using var db = Seed(t, SearchWords);
        var full = db.Scan(null, null, null).ToList();
        Assert.True(full.Count > 2);
        var cursor = full[1];
        var resumed = db.Scan(null, null, cursor).ToList();
        Assert.Equal(full.Skip(2), resumed);
    }

    [Fact]
    public void Scan_MaxBound_StopsAtFirstLongerLength()
    {
        using var t = new TempDb();
        using var db = Seed(t, SearchWords);
        var scanned = db.Scan(min: null, max: 5, resumeAfterWord: null).ToList();
        Assert.All(scanned, w => Assert.True(w.Length <= 5));
        Assert.Contains("short", scanned);
        Assert.Contains("pw", scanned);
    }
}

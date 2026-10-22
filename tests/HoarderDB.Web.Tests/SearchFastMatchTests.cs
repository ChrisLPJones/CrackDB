using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using HoarderDB.Web.Jobs;
using Xunit.Abstractions;

namespace HoarderDB.Web.Tests;

/// <summary>
/// The byte-level (UTF-8) matcher must give exactly the string matcher's answer on every stored key, including
/// invalid UTF-8, NUL and 0xFF bytes. The old string matcher stays here as the reference.
/// </summary>
public sealed class SearchFastMatchTests(ITestOutputHelper output)
{
    private static byte[] Raw(params int[] b) => b.Select(x => (byte)x).ToArray();

    private static List<byte[]> Words()
    {
        var u = new UTF8Encoding(false);
        var w = new List<byte[]>();
        foreach (var s in new[]
        {
            "needle", "NEEDLE", "Needle", "xxneedlexx", "needl", "nee\0dle", "needle\0", "\0\0needle", "pässwörd", "日本needle語",
            "😀needle😀", "needle￿", "￿needle", "needle�", "ne�edle", "abcd", "ä", "Ä", "wörd", "WÖRD", "😀", "a😀b",
        }) w.Add(u.GetBytes(s));
        w.Add(Raw(0xFF, 0xFF, 0xFF, 0xFF));
        w.Add(Raw(0xFF, 'n', 'e', 'e', 'd', 'l', 'e', 0xFF));
        w.Add(Raw('n', 'e', 'e', 'd', 'l', 'e', 0xC3));                 // truncated 2-byte lead after a hit
        w.Add(Raw(0xE6, 0x97, 'n', 'e', 'e', 'd', 'l', 'e'));            // truncated 3-byte lead before a hit
        w.Add(Raw(0xF0, 0x9F, 0x98, 'n', 'e', 'e', 'd', 'l', 'e'));      // truncated 4-byte
        w.Add(Raw(0xC0, 0x80, 'n', 'e', 'e', 'd', 'l', 'e'));            // overlong NUL
        w.Add(Raw(0xED, 0xA0, 0x80, 'n', 'e', 'e', 'd', 'l', 'e'));      // encoded surrogate
        w.Add(Raw(0x80, 0x80, 'n', 'e', 'e', 'd', 'l', 'e'));            // stray continuation bytes
        w.Add(Raw('p', 0xC3, 0xA4, 's', 's'));                           // valid "päss"
        w.Add(Raw('p', 0xC3, 's', 's'));                                 // broken "pä"
        w.Add(Raw(0xC3));
        w.Add(Raw(0xA4));
        w.Add(Array.Empty<byte>());
        var rnd = new Random(7);                                         // random bytes, heavy in edge values
        int[] pool = { 0, 0xFF, 0xC3, 0xA4, 0x80, 0xE6, 0x97, 0xA5, 0xF0, 0x9F, 0x98, 0x80, 'n', 'e', 'd', 'l', 'a', 'ä' & 0xFF };
        for (int i = 0; i < 20000; i++)
            w.Add(Enumerable.Range(0, rnd.Next(0, 14)).Select(_ => (byte)pool[rnd.Next(pool.Length)]).ToArray());
        return w;
    }

    public static IEnumerable<object[]> Queries() =>
        new[] { "needle", "NEEDLE", "Needle", "need", "eedl", "nee\0d", "\0\0ne", "pässwörd", "päss", "wörd", "WÖRD", "日本", "本needle", "😀needle", "😀",
                "ä", "￿", "needle￿", "�", "ne�e", "needle�", "zzzz", "\ud83d" /* lone high surrogate */, "a\udc00b" /* lone low */ }
            .SelectMany(q => new[] { new object[] { q, false }, new object[] { q, true } });

    [Theory]
    [MemberData(nameof(Queries))]
    public void Contains_BytePathEqualsStringPath_OnEveryKey(string query, bool ignoreCase)
    {
        var spec = new SearchSpec(query, ignoreCase, Contains: true);
        var oldMatch = SearchMatcher.Compile(spec);
        var fast = SearchMatcher.CompileUtf8(spec);
        bool lone = new UTF8Encoding(false, false).GetString(Encoding.UTF8.GetBytes(query)) != query;   // lossy round-trip changed it
        Assert.Equal(ignoreCase || query.Contains('\uFFFD') || lone, fast is null);
        if (fast is null) return;   // falls back to the string matcher: identical by construction
        int hits = 0;
        foreach (var word in Words())
        {
            var key = new byte[4 + word.Length];
            BinaryPrimitives.WriteInt32BigEndian(key, 0);
            word.CopyTo(key, 4);
            bool expected = oldMatch(Encoding.UTF8.GetString(word));   // what cur.Word does (lossy decode)
            Assert.Equal(expected, fast(key.AsSpan(4)));
            if (expected) hits++;
        }
        if (query is "needle" or "need" or "ä") Assert.True(hits > 0);
    }

    [Fact]
    public void FastPath_OnlyForCaseSensitiveContains()
    {
        Assert.NotNull(SearchMatcher.CompileUtf8(new SearchSpec("needle", false, true)));
        Assert.Null(SearchMatcher.CompileUtf8(new SearchSpec("needle", true, true)));
        Assert.Null(SearchMatcher.CompileUtf8(new SearchSpec("needle", true, false)));
        Assert.Null(SearchMatcher.CompileUtf8(new SearchSpec("needle", false, false)));
        Assert.Null(SearchMatcher.CompileUtf8(new SearchSpec("a�b", false, true)));
        Assert.Null(SearchMatcher.CompileUtf8(new SearchSpec("a\ud800b", false, true)));
    }

    // Timing only (numbers go into the change report); enable with HOARDERDB_MATCH_BENCH=1. Asserts nothing about speed.
    [Fact]
    public void Bench_KeysPerSecond_OldVsNew()
    {
        if (Environment.GetEnvironmentVariable("HOARDERDB_MATCH_BENCH") != "1") return;
        var rnd = new Random(1);
        var keys = new byte[1_000_000][];
        for (int i = 0; i < keys.Length; i++)
        {
            var s = string.Create(8 + rnd.Next(12), rnd, (sp, r) => { for (int k = 0; k < sp.Length; k++) sp[k] = (char)('a' + r.Next(26)); });
            var b = Encoding.UTF8.GetBytes(s);
            var key = new byte[4 + b.Length];
            BinaryPrimitives.WriteInt32BigEndian(key, s.Length);
            b.CopyTo(key, 4);
            keys[i] = key;
        }
        var spec = new SearchSpec("needle", false, true);
        var oldMatch = SearchMatcher.Compile(spec);
        var fast = SearchMatcher.CompileUtf8(spec)!;
        for (int round = 0; round < 3; round++)
        {
            int a = 0, c = 0;
            var sw = Stopwatch.StartNew();
            foreach (var k in keys) { var w = Encoding.UTF8.GetString(k.AsSpan(4)); if (oldMatch(w)) a++; }
            double oldMs = sw.Elapsed.TotalMilliseconds; sw.Restart();
            foreach (var k in keys) if (fast(k.AsSpan(4))) c++;
            double newMs = sw.Elapsed.TotalMilliseconds;
            Assert.Equal(a, c);
            output.WriteLine($"round {round}: old {keys.Length / oldMs / 1000:F1}M keys/s, new {keys.Length / newMs / 1000:F1}M keys/s");
        }
    }
}

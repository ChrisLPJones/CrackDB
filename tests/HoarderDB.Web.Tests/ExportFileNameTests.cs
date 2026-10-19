using System.Net;
using System.Net.Http.Json;
using System.Text;

namespace HoarderDB.Web.Tests;

/// <summary>Export output file name: sanitising and the Content-Disposition header (security-sensitive).</summary>
public class ExportFileNameTests : IClassFixture<HoarderDbWebFactory>
{
    private readonly HoarderDbWebFactory _f;
    public ExportFileNameTests(HoarderDbWebFactory f) => _f = f;

    private static string Raw(HttpResponseMessage r) =>
        string.Join("|", r.Content.Headers.NonValidated["Content-Disposition"]);

    private async Task<HttpResponseMessage> Export(string? filename)
    {
        var c = _f.NewApiKeyClient();
        var r = await c.PostAsJsonAsync("/api/dbs/Test_words/export", new { min = 2, max = 7, filename });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        await r.Content.ReadAsStringAsync();
        return r;
    }

    // Sanitised name as the client sees it: the filename* form when present, else the quoted fallback.
    private async Task<string> Name(string input)
    {
        var h = Raw(await Export(input));
        var star = h.IndexOf("filename*=UTF-8''", StringComparison.Ordinal);
        if (star >= 0) return Uri.UnescapeDataString(h[(star + "filename*=UTF-8''".Length)..]);
        var a = h.IndexOf('"') + 1;
        return h[a..h.LastIndexOf('"')];
    }

    private static void AssertSafeHeader(HttpResponseMessage r)
    {
        var h = Raw(r);
        Assert.DoesNotContain('\r', h);
        Assert.DoesNotContain('\n', h);
        Assert.DoesNotContain("|", h);   // exactly one header value
        Assert.False(r.Headers.Contains("Set-Cookie"));
        Assert.StartsWith("attachment; filename=\"", h);
        // Exactly the two quotes around the fallback name, none inside.
        var q = h.Substring("attachment; filename=\"".Length);
        var end = q.IndexOf('"');
        Assert.True(end > 0);
        Assert.DoesNotContain('"', q[..end]);
        Assert.DoesNotContain(';', q[..end]);
    }

    [Theory]
    [InlineData("wordlist.lst", "wordlist.lst")]
    [InlineData("wordlist-5-12.lst", "wordlist-5-12.lst")]
    [InlineData("foo", "foo.lst")]
    [InlineData("foo.txt", "foo.lst")]
    [InlineData("FOO.LST", "FOO.LST")]
    [InlineData("a b.lst", "a b.lst")]
    [InlineData("", "wordlist.lst")]
    [InlineData("   ", "wordlist.lst")]
    [InlineData("...", "wordlist.lst")]
    public async Task Names_AreExact(string input, string expected)
    {
        var r = await Export(input);
        Assert.Equal($"attachment; filename=\"{expected}\"", Raw(r));
        Assert.Equal(expected, await Name(input));
    }

    [Theory]
    [InlineData("x\r\nSet-Cookie: a=b")]
    [InlineData("x\"; filename=\"evil.exe")]
    [InlineData("a;b")]
    [InlineData("%0d%0a")]
    [InlineData("a\u0000b\u0007c")]
    public async Task Injection_IsNeutralised(string input)
    {
        var r = await Export(input);
        AssertSafeHeader(r);
    }

    [Fact]
    public async Task Injection_ExactResults()
    {
        Assert.Equal("attachment; filename=\"x__Set-Cookie_ a_b.lst\"", Raw(await Export("x\r\nSet-Cookie: a=b")));
        Assert.Equal("attachment; filename=\"x__ filename__evil.exe.lst\"", Raw(await Export("x\"; filename=\"evil.exe")));
        Assert.Equal("attachment; filename=\"a_b.lst\"", Raw(await Export("a;b")));
        Assert.Equal("attachment; filename=\"_0d_0a.lst\"", Raw(await Export("%0d%0a")));
    }

    [Theory]
    [InlineData("../../etc/passwd", "passwd.lst")]
    [InlineData("..\\..\\x", "x.lst")]
    [InlineData("/abs/path", "path.lst")]
    [InlineData("a/../b", "b.lst")]
    [InlineData("....//x", "x.lst")]
    [InlineData("a/", "wordlist.lst")]
    public async Task Traversal_KeepsLastSegmentOnly(string input, string expected)
    {
        var r = await Export(input);
        AssertSafeHeader(r);
        var h = Raw(r);
        Assert.DoesNotContain("/", h);
        Assert.DoesNotContain("\\", h);
        Assert.DoesNotContain("..", h);
        Assert.Equal($"attachment; filename=\"{expected}\"", h);
    }

    [Fact]
    public async Task Unicode_AsciiFallbackAndStarForm()
    {
        var r = await Export("wörds-日本.lst");
        Assert.Equal("attachment; filename=\"w_rds-__.lst\"; filename*=UTF-8''w%C3%B6rds-%E6%97%A5%E6%9C%AC.lst", Raw(r));
    }

    [Fact]
    public async Task Unicode_BidiAndEmoji_BecomeUnderscore_AndNfdEqualsNfc()
    {
        Assert.Equal("a_txt.lst", await Name("a‮txt"));
        Assert.Equal("a_b.lst", await Name("a😀b"));
        // Normalisation depends on ICU (a no-op under invariant globalization), so only assert when available.
        var nfc = "w\u00F6rds";
        var nfd = "wo\u0308rds";
        if (nfd.Normalize(NormalizationForm.FormC) == nfc)
            Assert.Equal(await Name(nfc), await Name(nfd));
        Assert.Equal("a_b.lst", await Name("a\uD800b"));   // lone surrogate
    }

    [Fact]
    public async Task Overlong_IsCapped_AndNeverSplitsSurrogates()
    {
        var r = await Export(new string('a', 5000));
        Assert.Equal($"attachment; filename=\"{new string('a', 100)}.lst\"", Raw(r));
        Assert.Equal(new string('b', 100) + ".lst", await Name(new string('b', 101)));
        // Emoji straddling the cut: surrogates never survive, so nothing can be half-cut.
        var s = await Name(new string('c', 99) + "😀" + "tail");
        Assert.Equal(new string('c', 99) + "_.lst", s);
        Assert.All(s, ch => Assert.False(char.IsSurrogate(ch)));
    }

    [Theory]
    [InlineData("CON", "_CON.lst")]
    [InlineData("nul.lst", "_nul.lst")]
    [InlineData("com1.txt", "_com1.lst")]
    [InlineData("console", "console.lst")]
    public async Task ReservedDeviceNames_GetPrefix(string input, string expected) =>
        Assert.Equal(expected, await Name(input));

    [Fact]
    public async Task Form_AbsentFilename_IsLegacyName_PresentIsSanitised()
    {
        var c = await _f.NewLoggedInClientAsync();
        var legacy = await c.PostAsync("/api/dbs/Test_words/export",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["min"] = "2", ["max"] = "7" }));
        Assert.StartsWith("attachment; filename=\"Test_words_2-7_", Raw(legacy));
        Assert.EndsWith(".txt\"", Raw(legacy));

        var named = await c.PostAsync("/api/dbs/Test_words/export",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["min"] = "2", ["max"] = "7", ["filename"] = "../my list.txt" }));
        Assert.Equal("attachment; filename=\"my list.lst\"", Raw(named));

        var empty = await c.PostAsync("/api/dbs/Test_words/export",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["filename"] = "" }));
        Assert.Equal("attachment; filename=\"wordlist.lst\"", Raw(empty));
    }

    [Fact]
    public async Task Json_AbsentFilename_IsLegacyName()
    {
        var r = await _f.NewApiKeyClient().PostAsJsonAsync("/api/dbs/Test_words/export", new { min = 2, max = 7 });
        Assert.StartsWith("attachment; filename=\"Test_words_2-7_", Raw(r));
    }

    [Fact]
    public async Task Unauthenticated_Is401()
    {
        var r = await _f.NewClient().PostAsJsonAsync("/api/dbs/Test_words/export", new { filename = "x" });
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
    }
}

using HoarderDB.Web.Jobs;

namespace HoarderDB.Web.Tests;

public class BoundedLineReaderTests
{
    private static List<string?> ReadAll(string text, int max, out int overlong)
    {
        var r = new BoundedLineReader(new StringReader(text), max);
        var lines = new List<string?>(); overlong = 0;
        while (r.TryReadLine(out var l, out var o)) { if (o) overlong++; else lines.Add(l); }
        return lines;
    }

    [Theory]
    [InlineData("a\nb\nc", new[] { "a", "b", "c" })]
    [InlineData("a\r\nb\rc\n", new[] { "a", "b", "c" })]
    [InlineData("a\n\nb\n", new[] { "a", "", "b" })]
    [InlineData("", new string[0])]
    public void MatchesReadLineSemantics(string text, string[] expected)
    {
        Assert.Equal(expected, ReadAll(text, 100, out var over));
        Assert.Equal(0, over);
        var sr = new StringReader(text); var viaStd = new List<string>(); string? s;
        while ((s = sr.ReadLine()) != null) viaStd.Add(s);
        Assert.Equal(viaStd, expected);
    }

    [Fact]
    public void OverlongLine_IsSkipped_AndReadingContinues()
    {
        var lines = ReadAll("ok\n" + new string('x', 101) + "\r\nfine\n" + new string('y', 100) + "\n", 100, out var over);
        Assert.Equal(new[] { "ok", "fine", new string('y', 100) }, lines);
        Assert.Equal(1, over);
    }

    [Fact]
    public void LineSpanningInternalChunks_IsHandled_AndCrLfSplitAcrossChunksIsOneBreak()
    {
        var text = new string('a', 65535) + "\r\nb\n";
        var lines = ReadAll(text, 70000, out _);
        Assert.Equal(new[] { new string('a', 65535), "b" }, lines);
    }

    [Fact]
    public void HugeNewlineFreeInput_IsConsumedWithoutBuffering()
    {
        var src = new EndlessReader(300_000_000);
        var r = new BoundedLineReader(src, 8192);
        Assert.True(r.TryReadLine(out var line, out var over));
        Assert.True(over); Assert.Null(line);
        Assert.False(r.TryReadLine(out _, out _));
        Assert.Equal(300_000_000, src.Produced);
    }

    private sealed class EndlessReader(long total) : TextReader
    {
        public long Produced;
        public override int Read(char[] buffer, int index, int count)
        {
            var n = (int)Math.Min(count, total - Produced);
            Array.Fill(buffer, 'z', index, n);
            Produced += n;
            return n;
        }
    }
}

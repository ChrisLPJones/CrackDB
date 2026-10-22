using HoarderDB.Web.Jobs;

#pragma warning disable xUnit1031

namespace HoarderDB.Web.Tests;

/// <summary>The ordering core of the parallel search, driven directly (no database, no files).</summary>
public sealed class OrderedTileBuffersTests
{
    private static readonly TimeSpan Hard = TimeSpan.FromSeconds(30);

    [Theory]
    [InlineData(4, 8, 5)]     // tiny cap: workers stall constantly
    [InlineData(3, 6, 1000)]
    [InlineData(1, 2, 3)]
    public void ManyWorkers_DrainStrictlyInTileOrder_NoLossNoDuplicate_NoDeadlock(int workers, int window, int cap)
    {
        const int tiles = 40, strides = 6, perStride = 4;
        var buf = new OrderedTileBuffers(tiles, cap, 1 << 20);
        buf.SetWindow(window);
        using var cts = new CancellationTokenSource(Hard);
        var threads = new List<Thread>();
        for (int w = 0; w < workers; w++)
        {
            var rnd = new Random(w);
            var t = new Thread(() =>
            {
                while (true)
                {
                    int idx = buf.Claim(cts.Token, out _);
                    if (idx < 0) return;
                    for (int s = 0; s < strides; s++)
                    {
                        var l = Enumerable.Range(0, perStride).Select(k => $"{idx:D3}-{s}-{k}").ToList();
                        if (rnd.Next(4) == 0) Thread.Sleep(rnd.Next(3));
                        buf.Commit(idx, l, l.Sum(x => x.Length), new[] { (byte)s }, 10, s == strides - 1, cts.Token);
                    }
                }
            }) { IsBackground = true };
            t.Start();
            threads.Add(t);
        }
        var got = new List<string>();
        var batch = new List<string>();
        while (!buf.AllDrained)
        {
            Assert.False(cts.IsCancellationRequested, "deadlock: hard timeout");
            buf.WaitActivity(10);
            batch.Clear();
            buf.Drain(batch);
            Assert.True(batch.Count <= cap + Math.Max(cap, perStride), "one drain stays bounded");
            got.AddRange(batch);
        }
        foreach (var t in threads) Assert.True(t.Join(Hard));
        var expected = Enumerable.Range(0, tiles).SelectMany(i => Enumerable.Range(0, strides).SelectMany(s => Enumerable.Range(0, perStride).Select(k => $"{i:D3}-{s}-{k}"))).ToList();
        Assert.Equal(expected, got);
        Assert.Equal(tiles * strides * 10L, buf.Scanned);
        Assert.Equal(tiles, buf.DoneTiles);
    }

    [Fact]
    public void Claim_NeverHandsOutBeyondTheWindow_UntilTheHeadAdvances()
    {
        var buf = new OrderedTileBuffers(6, 100, 1 << 20);
        buf.SetWindow(2);
        Assert.Equal(0, buf.Claim(default, out _));
        Assert.Equal(1, buf.Claim(default, out _));
        var third = Task.Run(() => buf.Claim(default, out _));
        Assert.False(third.Wait(250), "tile 2 is outside the window of head 0");
        buf.Commit(0, new List<string> { "a" }, 1, null, 1, true, default);
        buf.Drain(new List<string>());                       // head -> 1
        Assert.True(third.Wait(Hard));
        Assert.Equal(2, third.Result);
    }

    [Fact]
    public void CancelledCommit_ChangesNothing_AndTheStrideIsRedoneFromTheOldCursor()
    {
        var buf = new OrderedTileBuffers(2, 2, 1 << 20);
        buf.SetWindow(2);
        int idx = buf.Claim(default, out _);
        buf.Commit(idx, new List<string> { "m1", "m2" }, 4, new byte[] { 1 }, 100, false, default);   // buffer now at the cap
        using var cts = new CancellationTokenSource();
        var blocked = Task.Run(() => buf.Commit(idx, new List<string> { "m3" }, 2, new byte[] { 2 }, 50, false, cts.Token));
        Assert.False(blocked.Wait(150));
        cts.Cancel(); buf.Wake();
        Assert.ThrowsAny<OperationCanceledException>(() => blocked.GetAwaiter().GetResult());
        Assert.Equal(100, buf.Scanned);                      // the abandoned stride was not counted
        buf.ReleaseRunning();
        Assert.Equal(idx, buf.Claim(default, out var resume));
        Assert.Equal(new byte[] { 1 }, resume);              // resumes after the last COMMITTED key
        var d = new List<string>();
        buf.Drain(d);
        Assert.Equal(new[] { "m1", "m2" }, d);
    }

    [Fact]
    public void EmptyBuffer_AdmitsAStrideLargerThanTheCap()
    {
        var buf = new OrderedTileBuffers(1, 2, 1 << 20);
        int idx = buf.Claim(default, out _);
        var big = Enumerable.Range(0, 10).Select(i => "x" + i).ToList();
        buf.Commit(idx, big, 20, null, 10, true, new CancellationTokenSource(Hard).Token);   // must not block
        var d = new List<string>();
        buf.Drain(d);
        Assert.Equal(big, d);
        Assert.True(buf.AllDrained);
    }

    [Fact]
    public void Drain_StopsAtTheCap_SoOneAppendStaysBounded()
    {
        var buf = new OrderedTileBuffers(5, 4, 1 << 20);
        buf.SetWindow(5);
        for (int i = 0; i < 5; i++)
        {
            int idx = buf.Claim(default, out _);
            buf.Commit(idx, new List<string> { "a" + i, "b" + i, "c" + i }, 6, null, 3, true, default);
        }
        var d = new List<string>();
        Assert.Equal(6, buf.Drain(d));                        // two tiles of 3, then the cap (4) is reached
        Assert.False(buf.AllDrained);
        var rest = new List<string>();
        while (buf.Drain(rest) > 0) { }
        Assert.Equal(15, d.Count + rest.Count);
        Assert.True(buf.AllDrained);
    }

    [Fact]
    public void Fault_StopsClaims()
    {
        var buf = new OrderedTileBuffers(3, 10, 100);
        buf.SetWindow(3);
        buf.Fail(new InvalidOperationException("x"));
        Assert.Equal(-1, buf.Claim(default, out _));
        Assert.NotNull(buf.Fault);
    }
}

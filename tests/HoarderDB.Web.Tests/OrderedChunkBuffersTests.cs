using HoarderDB.Web.Jobs;

#pragma warning disable xUnit1031

namespace HoarderDB.Web.Tests;

/// <summary>The ordering core of the parallel export, driven directly (no database, no files).</summary>
public sealed class OrderedChunkBuffersTests
{
    private static readonly TimeSpan Hard = TimeSpan.FromSeconds(30);

    private static byte[] Chunk(int tile, int seq) => System.Text.Encoding.ASCII.GetBytes($"{tile:D3}-{seq:D2}");

    [Theory]
    [InlineData(4, 8, 1)]      // tiny cap: every chunk after the first waits for the drainer
    [InlineData(2, 4, 20)]
    [InlineData(3, 6, 100000)]
    [InlineData(1, 2, 7)]
    public async Task ManyWorkers_DrainStrictlyInTileOrder_NoLossNoDuplicate_NoDeadlock(int workers, int window, long cap)
    {
        const int tiles = 40, chunksPerTile = 5;
        var buf = new OrderedChunkBuffers(tiles, cap);
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
                    int idx = buf.Claim(cts.Token);
                    if (idx < 0) return;
                    for (int s = 0; s < chunksPerTile; s++)
                    {
                        if (rnd.Next(4) == 0) Thread.Sleep(rnd.Next(3));
                        var c = Chunk(idx, s);
                        buf.Add(idx, c, c.Length, cts.Token);
                    }
                    buf.Complete(idx);
                }
            }) { IsBackground = true };
            t.Start();
            threads.Add(t);
        }
        var got = new List<string>();
        while (!buf.AllDrained)
        {
            Assert.False(cts.IsCancellationRequested, "deadlock: hard timeout");
            if (buf.TryTake(out var c)) { got.Add(System.Text.Encoding.ASCII.GetString(c.Buf, 0, c.Len)); continue; }
            await buf.WaitActivityAsync(10, cts.Token);
        }
        foreach (var t in threads) Assert.True(t.Join(Hard));
        var expected = Enumerable.Range(0, tiles).SelectMany(i => Enumerable.Range(0, chunksPerTile).Select(s => $"{i:D3}-{s:D2}")).ToList();
        Assert.Equal(expected, got);
        Assert.Equal(0, buf.Buffered());
    }

    [Fact]
    public void Claim_StaysInsideTheWindow_AndHeadAdvancesOnlyWhenDoneAndEmpty()
    {
        var buf = new OrderedChunkBuffers(5, 1000);
        buf.SetWindow(2);
        using var cts = new CancellationTokenSource();
        Assert.Equal(0, buf.Claim(cts.Token));
        Assert.Equal(1, buf.Claim(cts.Token));
        var claim = Task.Run(() => buf.Claim(cts.Token));   // tile 2 is outside the window: waits
        Assert.False(claim.Wait(200));
        var c = Chunk(0, 0);
        buf.Add(0, c, c.Length, cts.Token);
        buf.Complete(0);
        Assert.False(claim.Wait(200), "head tile 0 is done but not drained: window has not moved");
        Assert.True(buf.TryTake(out var item));
        Assert.Equal(c.Length, item.Len);
        Assert.False(buf.TryTake(out _));                    // advances past tile 0, tile 1 has nothing yet
        Assert.Equal(2, claim.WaitAsync(Hard).Result);
        cts.Cancel();
    }

    [Fact]
    public void Add_WaitsForTheDrainer_AndCancelsWithoutTakingOwnership()
    {
        var buf = new OrderedChunkBuffers(2, 10);
        buf.SetWindow(2);
        using var cts = new CancellationTokenSource();
        Assert.Equal(0, buf.Claim(cts.Token));
        var big = new byte[8];
        buf.Add(0, big, 8, cts.Token);                       // empty buffer always admits one
        var second = new byte[8];
        var add = Task.Run(() => buf.Add(0, second, 8, cts.Token));
        Assert.False(add.Wait(200), "tile is over its cap: the producer waits");
        Assert.True(buf.TryTake(out _));
        add.Wait(Hard);                                      // freed by the drainer
        Assert.Equal(8, buf.Buffered());
        var third = Task.Run(() => buf.Add(0, new byte[8], 8, cts.Token));
        Assert.False(third.Wait(150));
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => third.GetAwaiter().GetResult());
        Assert.Equal(8, buf.Buffered());                     // the cancelled chunk was not taken
        var released = new List<byte[]>();
        buf.ReleaseAll(released.Add);
        Assert.Single(released);
        Assert.Equal(0, buf.Buffered());
    }

    [Fact]
    public void Fail_WakesBlockedWorkers_AndStopsClaims()
    {
        var buf = new OrderedChunkBuffers(3, 4);
        buf.SetWindow(3);
        using var cts = new CancellationTokenSource(Hard);
        Assert.Equal(0, buf.Claim(cts.Token));
        buf.Add(0, new byte[4], 4, cts.Token);
        var add = Task.Run(() => buf.Add(0, new byte[4], 4, cts.Token));
        Assert.False(add.Wait(150));
        buf.Fail(new InvalidOperationException("x"));
        Assert.ThrowsAny<OperationCanceledException>(() => add.GetAwaiter().GetResult());
        Assert.Equal(-1, buf.Claim(cts.Token));
        Assert.IsType<InvalidOperationException>(buf.Fault);
    }
}

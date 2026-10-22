namespace HoarderDB.Web.Jobs;

/// <summary>
/// The ordering core of a parallel export: same claim/window/head rules as <see cref="OrderedTileBuffers"/>, but each tile
/// holds queued byte chunks capped in bytes. Workers fill tiles in key order inside a window of the head tile; ONE drainer
/// (the export handler, the only socket writer) takes chunks from the head tile only and moves on once the head tile is
/// complete and empty, so the output is exactly the serial output.
/// Pure data structure (no database, no files, no threads of its own); the unit tests drive it directly.
///
/// Rules that keep it deadlock-free and bounded:
///  - the head tile is always claimable (it is inside every window) and its worker waits only for the drainer;
///  - a tile whose buffer is full waits (timed 50 ms, token-aware) for the drainer; an EMPTY buffer always admits one
///    chunk, so memory is at most window x max(cap, one chunk);
///  - every wait is timed and checks the token and the fault flag.
/// Chunks are password data: held in memory only, never put into exceptions or logs. Ownership of an array passes to the
/// buffers on a successful <see cref="Add"/> and to the caller on <see cref="TryTake"/> / <see cref="ReleaseAll"/>.
/// </summary>
internal sealed class OrderedChunkBuffers
{
    internal readonly record struct ChunkItem(byte[] Buf, int Len);

    private enum State : byte { Idle, Running, Done }

    private sealed class TileState
    {
        public State State;
        public readonly Queue<ChunkItem> Chunks = new();
        public long Bytes;
    }

    private readonly object _gate = new();
    private readonly TileState[] _tiles;
    private readonly long _capBytes;
    private readonly SemaphoreSlim _signal = new(0, 1);   // drainer wake-up (async-friendly stand-in for a monitor wait)
    private int _head;
    private int _window = 1;
    private Exception? _fault;

    public OrderedChunkBuffers(int tileCount, long capBytes)
    {
        if (tileCount < 1) throw new ArgumentOutOfRangeException(nameof(tileCount));
        _tiles = new TileState[tileCount];
        for (int i = 0; i < tileCount; i++) _tiles[i] = new TileState();
        _capBytes = Math.Max(1, capBytes);
    }

    public int TileCount => _tiles.Length;

    /// <summary>True once every tile is done and every chunk has been taken.</summary>
    public bool AllDrained { get { lock (_gate) return _head >= _tiles.Length; } }

    /// <summary>A worker's failure (first one wins); the owner handles it after joining the workers.</summary>
    public Exception? Fault { get { lock (_gate) return _fault; } }

    public void Fail(Exception e)
    {
        lock (_gate) { _fault ??= e; Monitor.PulseAll(_gate); }
        Signal();
    }

    /// <summary>Workers may claim tiles within this many tiles of the head tile (2 x workers).</summary>
    public void SetWindow(int window)
    {
        lock (_gate) { _window = Math.Max(1, window); Monitor.PulseAll(_gate); }
    }

    /// <summary>Wakes every waiter (call when a token is cancelled).</summary>
    public void Wake()
    {
        lock (_gate) Monitor.PulseAll(_gate);
        Signal();
    }

    private void Signal()
    {
        try { _signal.Release(); } catch (SemaphoreFullException) { /* already signalled */ }
    }

    /// <summary>
    /// Claims the lowest idle tile inside the window. Waits (timed) while idle tiles exist only beyond the window.
    /// Returns -1 when no idle tile is left, the token is cancelled or a worker failed.
    /// </summary>
    public int Claim(CancellationToken ct)
    {
        lock (_gate)
        {
            while (true)
            {
                if (ct.IsCancellationRequested || _fault != null) return -1;
                int end = (int)Math.Min((long)_head + _window, _tiles.Length);
                for (int i = _head; i < end; i++)
                {
                    if (_tiles[i].State != State.Idle) continue;
                    _tiles[i].State = State.Running;
                    return i;
                }
                bool idleBeyond = false;
                for (int i = end; i < _tiles.Length && !idleBeyond; i++) idleBeyond = _tiles[i].State == State.Idle;
                if (!idleBeyond) return -1;
                Monitor.Wait(_gate, 50);
            }
        }
    }

    /// <summary>
    /// Queues one chunk on a tile. Waits (timed, cancellable) while the tile already holds bytes and this chunk would
    /// exceed the cap. Throws <see cref="OperationCanceledException"/> WITHOUT taking ownership of <paramref name="buf"/>
    /// when cancelled or when another worker failed.
    /// </summary>
    public void Add(int tile, byte[] buf, int len, CancellationToken ct)
    {
        lock (_gate)
        {
            var t = _tiles[tile];
            while (t.Bytes > 0 && t.Bytes + len > _capBytes)
            {
                ct.ThrowIfCancellationRequested();
                if (_fault != null) throw new OperationCanceledException();
                Monitor.Wait(_gate, 50);
            }
            ct.ThrowIfCancellationRequested();
            t.Chunks.Enqueue(new ChunkItem(buf, len));
            t.Bytes += len;
            Monitor.PulseAll(_gate);
        }
        Signal();
    }

    /// <summary>Marks a tile finished (all its chunks are queued).</summary>
    public void Complete(int tile)
    {
        lock (_gate)
        {
            _tiles[tile].State = State.Done;
            Monitor.PulseAll(_gate);
        }
        Signal();
    }

    /// <summary>
    /// Drainer: takes the next chunk of the head tile, advancing past tiles that are done and empty.
    /// False when the head tile has nothing queued yet (or everything is drained).
    /// </summary>
    public bool TryTake(out ChunkItem item)
    {
        lock (_gate)
        {
            while (_head < _tiles.Length)
            {
                var h = _tiles[_head];
                if (h.Chunks.Count > 0)
                {
                    item = h.Chunks.Dequeue();
                    h.Bytes -= item.Len;
                    Monitor.PulseAll(_gate);
                    return true;
                }
                if (h.State != State.Done) break;
                _head++;
                Monitor.PulseAll(_gate);   // an empty Done tile also opens the window
            }
            item = default;
            return false;
        }
    }

    /// <summary>Drainer: waits up to <paramref name="ms"/> for a chunk, a completed tile or a fault.</summary>
    public async Task WaitActivityAsync(int ms, CancellationToken ct)
    {
        await _signal.WaitAsync(ms, ct);
    }

    /// <summary>After every worker has been joined: hands every queued chunk array to <paramref name="release"/>.</summary>
    public void ReleaseAll(Action<byte[]> release)
    {
        lock (_gate)
        {
            foreach (var t in _tiles)
            {
                while (t.Chunks.Count > 0) release(t.Chunks.Dequeue().Buf);
                t.Bytes = 0;
            }
        }
    }

    /// <summary>Queued bytes not yet taken (test/diagnostic).</summary>
    public long Buffered()
    {
        lock (_gate)
        {
            long b = 0;
            foreach (var t in _tiles) b += t.Bytes;
            return b;
        }
    }
}

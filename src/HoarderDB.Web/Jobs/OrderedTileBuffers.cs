namespace HoarderDB.Web.Jobs;

/// <summary>
/// The ordering core of a parallel search: tiles are claimed in key order inside a window of the head tile, each tile
/// collects its matches in a capped buffer, and ONE drainer (the search thread, the only spool writer) takes the head
/// tile's buffer and moves on only when the head tile is done, so the output is exactly the serial output.
/// Pure data structure: no database, no files, no threads of its own (the unit tests drive it directly).
///
/// Rules that keep it deadlock-free and bounded:
///  - the head tile is always claimable (it is inside every window), and its worker waits only for the drainer;
///  - a worker adds a stride's matches atomically with its cursor and key count (<see cref="Commit"/>), so a stride
///    that is abandoned on cancel changes nothing and the resume point stays exact (no key counted or lost twice);
///  - a tile whose buffer is full waits for the drainer; an EMPTY buffer always admits a stride (a stride larger than
///    the cap cannot stall), so memory is at most window x max(cap, one stride);
///  - every wait is timed and wakes on <see cref="Wake"/> (called when the segment token is cancelled).
/// Matches are password data: held in memory only, never put into exceptions or logs.
/// </summary>
internal sealed class OrderedTileBuffers
{
    private enum State : byte { Idle, Running, Done }

    private sealed class TileState
    {
        public State State;
        public byte[]? Cursor;                       // raw key of the last committed key; null = not started
        public List<string> Matches = new();
        public long Chars;
    }

    private readonly object _gate = new();
    private readonly TileState[] _tiles;
    private readonly int _capMatches;
    private readonly long _capChars;
    private int _head;
    private int _window = 1;
    private long _scanned;
    private int _done;
    private Exception? _fault;

    public OrderedTileBuffers(int tileCount, int capMatches, long capChars)
    {
        if (tileCount < 1) throw new ArgumentOutOfRangeException(nameof(tileCount));
        _tiles = new TileState[tileCount];
        for (int i = 0; i < tileCount; i++) _tiles[i] = new TileState();
        _capMatches = Math.Max(1, capMatches);
        _capChars = Math.Max(1, capChars);
    }

    public int TileCount => _tiles.Length;

    /// <summary>Keys committed so far by all workers (exact; only ever grows).</summary>
    public long Scanned => Interlocked.Read(ref _scanned);

    public int DoneTiles { get { lock (_gate) return _done; } }

    /// <summary>True once every tile is done and every match has been drained.</summary>
    public bool AllDrained { get { lock (_gate) return _head >= _tiles.Length; } }

    /// <summary>A worker's failure (first one wins); the owner rethrows it after joining the workers.</summary>
    public Exception? Fault { get { lock (_gate) return _fault; } }

    public void Fail(Exception e)
    {
        lock (_gate) { _fault ??= e; Monitor.PulseAll(_gate); }
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
    }

    /// <summary>
    /// Claims the lowest idle tile inside the window and returns its index and resume cursor (null = start of the tile).
    /// Waits while idle tiles exist only beyond the window. Returns -1 when no idle tile is left or the token is cancelled.
    /// </summary>
    public int Claim(CancellationToken ct, out byte[]? resume)
    {
        lock (_gate)
        {
            while (true)
            {
                resume = null;
                if (ct.IsCancellationRequested || _fault != null) return -1;
                int end = (int)Math.Min((long)_head + _window, _tiles.Length);
                for (int i = _head; i < end; i++)
                {
                    if (_tiles[i].State != State.Idle) continue;
                    _tiles[i].State = State.Running;
                    resume = _tiles[i].Cursor;
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
    /// Publishes one stride of a tile atomically: its matches, the raw key of the last key scanned and the number of keys.
    /// Waits (timed, cancellable) while the tile's buffer is full; throws <see cref="OperationCanceledException"/> WITHOUT
    /// having changed anything if cancelled, so the stride is simply redone after a resume.
    /// </summary>
    public void Commit(int tile, List<string> matches, long chars, byte[]? cursor, long keys, bool done, CancellationToken ct)
    {
        lock (_gate)
        {
            var t = _tiles[tile];
            while (t.Matches.Count > 0 && (t.Matches.Count + matches.Count > _capMatches || t.Chars + chars > _capChars))
            {
                ct.ThrowIfCancellationRequested();
                if (_fault != null) throw new OperationCanceledException();
                Monitor.Wait(_gate, 50);
            }
            ct.ThrowIfCancellationRequested();
            t.Matches.AddRange(matches);
            t.Chars += chars;
            if (cursor != null) t.Cursor = cursor;
            Interlocked.Add(ref _scanned, keys);
            if (done) { t.State = State.Done; _done++; }
            Monitor.PulseAll(_gate);
        }
    }

    /// <summary>Drainer: waits up to <paramref name="ms"/> for a commit, a claim or a fault.</summary>
    public void WaitActivity(int ms)
    {
        lock (_gate) Monitor.Wait(_gate, ms);
    }

    /// <summary>
    /// Drainer: moves the head tile's buffered matches (in order) into <paramref name="dest"/>, advancing past tiles that
    /// are done, but stops once <paramref name="dest"/> holds the per-tile cap, so one append stays bounded.
    /// Returns the number of matches moved.
    /// </summary>
    public int Drain(List<string> dest)
    {
        lock (_gate)
        {
            int before = dest.Count, headBefore = _head;
            long chars = 0;
            while (_head < _tiles.Length)
            {
                var h = _tiles[_head];
                if (h.Matches.Count > 0)
                {
                    dest.AddRange(h.Matches);
                    chars += h.Chars;
                    h.Matches.Clear();
                    h.Chars = 0;
                }
                if (h.State != State.Done) break;
                _head++;
                if (dest.Count - before >= _capMatches || chars >= _capChars) break;
            }
            int moved = dest.Count - before;
            if (moved > 0 || _head != headBefore) Monitor.PulseAll(_gate);   // an empty Done tile also opens the window
            return moved;
        }
    }

    /// <summary>After every worker has been joined: tiles that were mid-scan become claimable again from their cursor.</summary>
    public void ReleaseRunning()
    {
        lock (_gate)
        {
            foreach (var t in _tiles) if (t.State == State.Running) t.State = State.Idle;
            Monitor.PulseAll(_gate);
        }
    }

    /// <summary>Buffered matches not yet drained (test/diagnostic).</summary>
    public (int Matches, long Chars) Buffered()
    {
        lock (_gate)
        {
            int m = 0; long c = 0;
            foreach (var t in _tiles) { m += t.Matches.Count; c += t.Chars; }
            return (m, c);
        }
    }
}

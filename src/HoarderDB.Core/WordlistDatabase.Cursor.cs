using System.Buffers.Binary;
using RocksDbSharp;

namespace HoarderDB;

public sealed partial class WordlistDatabase
{
    /// <summary>
    /// Test/diagnostic counter: how many scan-cursor iterators have been opened in this process.
    /// A search segment must open exactly one, however many keys it scans (each new iterator + seek
    /// re-reads the index of a huge SST file).
    /// </summary>
    public static long CursorsOpened => Interlocked.Read(ref _cursorsOpened);
    private static long _cursorsOpened;

    /// <summary>
    /// Opens a forward cursor over the keys whose password length is in [min, max] (either bound may be null).
    /// One iterator is held for the cursor's whole life; call <see cref="ScanCursor.SeekTo"/> before
    /// <see cref="ScanCursor.MoveNext"/>. The caller must dispose the cursor BEFORE releasing the read lease
    /// that guards this database handle. The existing <see cref="Scan"/> stays as the test oracle.
    /// </summary>
    public ScanCursor OpenScan(int? min, int? max) => new(this, min, max);

    /// <summary>Resumable forward cursor; the resume point is raw key bytes (no string round-trip).</summary>
    public sealed class ScanCursor : IDisposable
    {
        private readonly WordlistDatabase _owner;
        private readonly ScanReadOptions _ro;   // declared before the iterator: disposed after it
        private readonly Iterator _it;
        private readonly int _lo;
        private readonly int? _max;
        private bool _fresh;       // positioned by SeekTo, not yet consumed by MoveNext
        private bool _valid;
        private bool _disposed;

        internal ScanCursor(WordlistDatabase owner, int? min, int? max)
        {
            _owner = owner;
            _lo = min ?? 0;
            _max = max;
            _ro = new ScanReadOptions(0, UpperBoundFor(max));
            try { _it = owner._db.NewIterator(readOptions: _ro.Options); }
            catch { _ro.Dispose(); throw; }
            Interlocked.Increment(ref _cursorsOpened);
        }

        /// <summary>
        /// Positions the cursor. <paramref name="afterKey"/> null starts at the first key of the range; otherwise
        /// the cursor continues strictly after that raw key (a key copied with <see cref="KeyCopy"/>).
        /// </summary>
        public void SeekTo(byte[]? afterKey)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (afterKey == null) _it.Seek(LengthPrefix(_lo));
            else
            {
                _it.Seek(afterKey);
                if (_it.Valid() && _it.GetKeySpan().SequenceEqual(afterKey)) _it.Next();
            }
            _fresh = true;
            _valid = false;
        }

        /// <summary>Advances to the next in-range key; the first call after <see cref="SeekTo"/> returns the key it landed on.</summary>
        public bool MoveNext()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_fresh) _fresh = false; else if (_it.Valid()) _it.Next();
            while (_it.Valid())
            {
                int len = BinaryPrimitives.ReadInt32BigEndian(_it.GetKeySpan());
                if (len < _lo) { _it.Next(); continue; }            // only reachable via a resume seek landing short
                if (_max.HasValue && len > _max.Value) { _valid = false; return false; }   // sorted: nothing longer follows
                _valid = true;
                return true;
            }
            _valid = false;
            return false;
        }

        /// <summary>The current raw key; valid only until the next MoveNext/SeekTo/Dispose.</summary>
        public ReadOnlySpan<byte> Key => _valid ? _it.GetKeySpan() : throw new InvalidOperationException("No current key.");

        /// <summary>The current password.</summary>
        public string Word => _owner._enc.GetString(Key.Slice(4));

        /// <summary>A copy of the current raw key, safe to keep as a resume point.</summary>
        public byte[] KeyCopy() => Key.ToArray();

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _it.Dispose();
            _ro.Dispose();   // after the iterator: the native upper-bound bytes must outlive it
        }
    }
}

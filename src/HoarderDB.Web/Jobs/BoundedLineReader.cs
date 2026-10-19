using System.Text;

namespace HoarderDB.Web.Jobs;

/// <summary>
/// Line reader (terminators \n, \r, \r\n like StreamReader.ReadLine) whose memory is bounded: a line
/// longer than <c>maxChars</c> is never buffered, the rest of it is discarded up to the next terminator
/// and reported as overlong. Keeps an unbounded upload with no newlines from becoming one huge string.
/// </summary>
public sealed class BoundedLineReader(TextReader reader, int maxChars)
{
    private readonly char[] _buf = new char[1 << 16];
    private readonly StringBuilder _sb = new();
    private int _pos, _len;
    private bool _skipLf;

    /// <summary>Reads the next line. False at end of input. For an overlong line, <paramref name="line"/> is null and <paramref name="overlong"/> true.</summary>
    public bool TryReadLine(out string? line, out bool overlong)
    {
        bool over = false, any = false;
        _sb.Clear();
        while (true)
        {
            if (_pos == _len)
            {
                _len = reader.Read(_buf, 0, _buf.Length);
                _pos = 0;
                if (_len == 0)
                {
                    if (!any) { line = null; overlong = false; return false; }
                    return Finish(over, out line, out overlong);
                }
            }
            if (_skipLf)
            {
                _skipLf = false;
                if (_buf[_pos] == '\n') { _pos++; continue; }
            }
            var span = _buf.AsSpan(_pos, _len - _pos);
            var i = span.IndexOfAny('\r', '\n');
            var take = i < 0 ? span.Length : i;
            if (take > 0) any = true;
            if (!over)
            {
                if (_sb.Length + take > maxChars) { over = true; _sb.Clear(); }
                else _sb.Append(_buf, _pos, take);
            }
            _pos += take;
            if (i < 0) continue;
            if (_buf[_pos++] == '\r') _skipLf = true;
            return Finish(over, out line, out overlong);
        }
    }

    private bool Finish(bool over, out string? line, out bool overlong)
    {
        overlong = over;
        line = over ? null : _sb.ToString();
        _sb.Clear();
        return true;
    }
}

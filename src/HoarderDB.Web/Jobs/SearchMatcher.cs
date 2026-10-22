namespace HoarderDB.Web.Jobs;

/// <summary>
/// Pure matching rules for a <see cref="SearchSpec"/>. Plain ordinal comparison only (no regex, no pattern
/// characters: <c>%</c>, <c>_</c> and <c>\</c> are ordinary characters). Case folding is ordinal, per UTF-16
/// unit, so a case-insensitive equal word always has the same length as the query.
/// </summary>
public static class SearchMatcher
{
    /// <summary>A scanning search needs at least this many characters.</summary>
    public const int MinChars = 4;

    /// <summary>
    /// Key-length range to scan (inherent to the mode, not user-supplied): ignore-case exact scans exactly one
    /// length bucket (ordinal-ignore-case equality preserves length); contains starts at the query length
    /// (a shorter word cannot contain it) and has no upper bound.
    /// </summary>
    public static (int Min, int? Max) EffectiveRange(SearchSpec s) =>
        s.Contains ? (s.Query.Length, null) : (s.Query.Length, s.Query.Length);

    public static Func<string, bool> Compile(SearchSpec s)
    {
        var q = s.Query;
        var cmp = s.IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return s.Contains ? w => w.Contains(q, cmp) : w => string.Equals(w, q, cmp);
    }

    /// <summary>Matches the UTF-8 bytes of a stored word (the key without its 4-byte length prefix).</summary>
    public delegate bool Utf8Match(ReadOnlySpan<byte> word);

    /// <summary>
    /// Byte-level matcher for the one mode where it is provably identical to <see cref="Compile"/>: case-sensitive
    /// contains. UTF-8 is self-synchronising, so a valid query's bytes can only match at character boundaries of the stored
    /// bytes, and a stored key with invalid bytes (decoded lossily to U+FFFD by the string path) matches only through its
    /// valid parts, identically in both paths (a key like <c>needle\xFF</c> matches "needle" in both; the invalid byte can
    /// never be part of a match for a query without U+FFFD). Returns null (use the string matcher) for everything else: ignore-case (needs
    /// string folding, no byte folding), exact (scanned only as ignore-case), and a query with a lone surrogate or
    /// U+FFFD (the string path compares those as UTF-16 units, bytes would not).
    /// </summary>
    public static Utf8Match? CompileUtf8(SearchSpec s)
    {
        if (!s.Contains || s.IgnoreCase || s.Query.Length == 0 || s.Query.Contains('\uFFFD')) return null;
        byte[] q;
        try { q = new System.Text.UTF8Encoding(false, throwOnInvalidBytes: true).GetBytes(s.Query); }
        catch (System.Text.EncoderFallbackException) { return null; }   // lone surrogate
        return w => w.IndexOf(q) >= 0;
    }
}

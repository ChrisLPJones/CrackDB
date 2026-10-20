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
}

using System.Globalization;
using System.Text;

namespace HoarderDB.Web.Endpoints;

/// <summary>
/// Turns the user-supplied export file name into a header-safe one. The result is rebuilt from an allowlist
/// (never echoed raw), is never used for filesystem access, and reaches exactly one header:
/// Content-Disposition.
/// </summary>
internal static class ExportFileName
{
    public const string Default = "wordlist.lst";
    private const int MaxStemChars = 100;
    private static readonly string[] Reserved =
        ["CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
         "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"];

    public static string Sanitise(string? input)
    {
        if (string.IsNullOrEmpty(input)) return Default;

        // Over-long input is cut before the heavier work; the stem cap below is the real limit.
        if (input.Length > 4096) input = input[..4096];
        string s;
        try { s = input.Normalize(NormalizationForm.FormC); }
        catch (ArgumentException) { s = input; }   // lone surrogates: handled (replaced) below

        // Last path segment only.
        var cut = s.LastIndexOfAny(['/', '\\']);
        if (cut >= 0) s = s[(cut + 1)..];

        // Allowlist rebuild: BMP letters/digits and . _ - space; everything else (controls, CR/LF, quotes, ';',
        // '%', bidi/format chars, emoji, any surrogate) becomes one '_'.
        var sb = new StringBuilder(s.Length);
        foreach (var rune in s.EnumerateRunes())
        {
            var ok = rune.IsBmp && (char.IsLetterOrDigit((char)rune.Value) || rune.Value is '.' or '_' or '-' or ' ');
            sb.Append(ok ? (char)rune.Value : '_');
        }
        var t = sb.ToString();
        while (t.Contains("..", StringComparison.Ordinal)) t = t.Replace("..", ".", StringComparison.Ordinal);
        t = t.Trim('.', ' ');

        // Extension: .txt is replaced by .lst; an existing .lst keeps its spelling.
        var ext = ".lst";
        if (t.EndsWith(".lst", StringComparison.OrdinalIgnoreCase)) { ext = t[^4..]; t = t[..^4]; }
        else if (t.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)) t = t[..^4];
        t = t.Trim('.', ' ');
        if (t.Length > MaxStemChars) t = t[..MaxStemChars].Trim('.', ' ');   // output has no surrogates, so no split
        if (t.Length == 0) return Default;

        var dot = t.IndexOf('.');
        var first = dot >= 0 ? t[..dot] : t;
        if (Reserved.Contains(first.TrimEnd(' '), StringComparer.OrdinalIgnoreCase)) t = "_" + t;
        return t + ext;
    }

    /// <summary>Header value for an already sanitised name: ASCII fallback plus an RFC 5987 UTF-8 form if needed.</summary>
    public static string ContentDisposition(string clean)
    {
        var ascii = new StringBuilder(clean.Length);
        var pure = true;
        foreach (var c in clean)
        {
            if (c < 0x20 || c > 0x7E || c == '"' || c == '\\') { ascii.Append('_'); pure = false; }
            else ascii.Append(c);
        }
        var v = $"attachment; filename=\"{ascii}\"";
        if (!pure) v += "; filename*=UTF-8''" + Uri.EscapeDataString(clean);
        return v;
    }
}

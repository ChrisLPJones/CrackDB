using Microsoft.Extensions.Options;

namespace HoarderDB.Web.Options;

/// <summary>
/// Time zone (IANA id, e.g. Australia/Sydney) of the weekly auto-compaction. Day, hour and minute are
/// edited on the Settings page (stored in KeyPath/app-settings.json, default Sunday 00:00); the zone stays
/// operator-owned here.
/// Env: CRACKDB_Compaction__TimeZone/HOARDERDB_Compaction__TimeZone (HOARDERDB_ wins if both are set).
/// </summary>
public sealed class CompactionOptions
{
    public const string Section = "Compaction";
    public const string DefaultTimeZone = "Australia/Sydney";

    public string TimeZone { get; set; } = DefaultTimeZone;
}

public sealed class CompactionOptionsValidator : IValidateOptions<CompactionOptions>
{
    /// <summary>Resolves the zone, or returns null and a clear message.</summary>
    public static TimeZoneInfo? TryResolve(string? id, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(id))
        {
            error = "Compaction:TimeZone must not be empty (env CRACKDB_Compaction__TimeZone or HOARDERDB_Compaction__TimeZone), e.g. Australia/Sydney or UTC.";
            return null;
        }
        try { return TimeZoneInfo.FindSystemTimeZoneById(id.Trim()); }
        catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            error = $"Compaction:TimeZone '{id}' is not a known IANA time zone id on this server " +
                    "(env CRACKDB_Compaction__TimeZone or HOARDERDB_Compaction__TimeZone; e.g. Australia/Sydney or UTC). " +
                    "If the id is valid, the server may be missing tzdata (/usr/share/zoneinfo).";
            return null;
        }
    }

    public ValidateOptionsResult Validate(string? name, CompactionOptions o) =>
        TryResolve(o.TimeZone, out var error) is not null ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(error!);
}

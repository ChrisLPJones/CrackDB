namespace HoarderDB.Web.Storage;

/// <summary>Free-space probe for the upload path (an interface so tests can simulate a filling disk).</summary>
public interface IDiskSpace
{
    /// <summary>Free bytes on the volume holding <paramref name="dir"/>, or null when unknown.</summary>
    long? FreeBytes(string dir);

    /// <summary>How many streamed bytes pass between free-space rechecks of an unlimited upload.</summary>
    long RecheckIntervalBytes { get; }
}

public sealed class DriveDiskSpace : IDiskSpace
{
    // Must stay <= the upload free-space reserve (JobEndpoints.DiskReserveBytes, 64 MiB) so the
    // overshoot between two checks cannot eat the whole reserve.
    public long RecheckIntervalBytes => 32L * 1024 * 1024;

    public long? FreeBytes(string dir)
    {
        try { return new DriveInfo(dir).AvailableFreeSpace; }
        catch (Exception) { return null; }                     // unknown filesystem: skip the check
    }
}

namespace HoarderDB.Web.Storage;

/// <summary>Shared helpers for the small advisory/config JSON files in KeyPath (0600, atomic tmp+rename, symlinks never followed).</summary>
internal static class AtomicJsonFile
{
    /// <summary>True if something other than a regular file (symlink, directory) sits at the path.</summary>
    public static bool IsUnsafe(string path) =>
        new FileInfo(path).LinkTarget is not null || Directory.Exists(path);

    /// <summary>Moves a bad file aside as <c>.corrupt-&lt;utc&gt;</c>; best effort. Returns false if it could not be moved.</summary>
    public static bool MoveAside(string path)
    {
        try
        {
            File.Move(path, path + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff"), overwrite: false);
            return true;
        }
        catch (Exception) { return false; }
    }

    /// <summary>Writes <paramref name="json"/> to a 0600 tmp file in the same dir, flushes, renames over the target. Throws on failure.</summary>
    public static void Write(string path, byte[] json)
    {
        if (IsUnsafe(path)) throw new IOException("The target is not a regular file.");
        var tmp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            var opts = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) opts.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var fs = new FileStream(tmp, opts))
            {
                fs.Write(json);
                fs.Flush(true);
            }
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmp); } catch { /* best effort */ }
            throw;
        }
    }
}

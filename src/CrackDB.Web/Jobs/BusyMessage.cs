namespace CrackDB.Web.Jobs;

/// <summary>Friendly text for "this database is busy", built from the running job (names only, no paths).</summary>
public static class BusyMessage
{
    public static string For(JobRegistry jobs, string? db)
    {
        var running = jobs.List().FirstOrDefault(j => j.Status == "running" && (db == null || j.Db == db));
        return running?.Kind switch
        {
            "compact" => $"Maintenance in progress: database {running.Db} is being compacted. Search and imports are unavailable until it finishes.",
            "import" => $"Database {running.Db} is being imported. Search of it is unavailable until the import finishes.",
            _ => "Database is busy (import or compact in progress). Try again when it finishes.",
        };
    }
}

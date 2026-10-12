using Microsoft.Extensions.Options;

namespace CrackDB.Web.Options;

/// <summary>
/// Where databases and the Data Protection key ring live. Deliberately has no defaults: a
/// forgotten setting must stop startup rather than fall back to a real data path.
/// Env: CRACKDB_Storage__DataRoot, CRACKDB_Storage__KeyPath, CRACKDB_Storage__IncomingRoot.
/// </summary>
public sealed class StorageOptions
{
    public const string Section = "Storage";

    public string DataRoot { get; set; } = "";
    public string KeyPath { get; set; } = "";

    /// <summary>
    /// Directory server-side imports are read from (and where browser uploads are staged under
    /// <c>.uploads</c>). Optional: when unset, file imports and uploads are disabled (compact still works).
    /// </summary>
    public string IncomingRoot { get; set; } = "";
}

/// <summary>Browser-upload limits. Env: CRACKDB_Upload__MaxBytes (0 or unset = unlimited).</summary>
public sealed class UploadOptions
{
    public const string Section = "Upload";
    public long MaxBytes { get; set; }
}

public sealed class UploadOptionsValidator : IValidateOptions<UploadOptions>
{
    public ValidateOptionsResult Validate(string? name, UploadOptions o) =>
        o.MaxBytes >= 0 ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail("Upload:MaxBytes must be zero (unlimited) or greater.");
}

public sealed class StorageOptionsValidator : IValidateOptions<StorageOptions>
{
    public ValidateOptionsResult Validate(string? name, StorageOptions o)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(o.DataRoot))
            errors.Add("Storage:DataRoot is required (env CRACKDB_Storage__DataRoot).");
        else if (!Directory.Exists(o.DataRoot))
            errors.Add("Storage:DataRoot does not exist or is not a directory.");
        if (string.IsNullOrWhiteSpace(o.KeyPath))
            errors.Add("Storage:KeyPath is required (env CRACKDB_Storage__KeyPath).");
        if (!string.IsNullOrWhiteSpace(o.IncomingRoot) && !Directory.Exists(o.IncomingRoot))
            errors.Add("Storage:IncomingRoot does not exist or is not a directory.");
        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}

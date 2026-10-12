using Microsoft.Extensions.Options;

namespace CrackDB.Web.Options;

/// <summary>
/// Where databases and the Data Protection key ring live. Deliberately has no defaults: a
/// forgotten setting must stop startup rather than fall back to a real data path.
/// Env: CRACKDB_Storage__DataRoot, CRACKDB_Storage__KeyPath.
/// </summary>
public sealed class StorageOptions
{
    public const string Section = "Storage";

    public string DataRoot { get; set; } = "";
    public string KeyPath { get; set; } = "";
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
        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}

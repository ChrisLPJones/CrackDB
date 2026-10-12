using Microsoft.Extensions.Options;

namespace CrackDB.Web.Options;

/// <summary>
/// Single-user credentials. Only hashes are configured, never plaintext.
/// Env: CRACKDB_Auth__Username, CRACKDB_Auth__PasswordHash, CRACKDB_Auth__ApiKeyHash.
/// </summary>
public sealed class AuthOptions
{
    public const string Section = "Auth";

    public string Username { get; set; } = "";
    /// <summary>PasswordHasher (PBKDF2) string; produce it with `crackdb hashpw`.</summary>
    public string PasswordHash { get; set; } = "";
    /// <summary>SHA-256 of the API key, hex. Optional: unset means cookie-only.</summary>
    public string ApiKeyHash { get; set; } = "";
    /// <summary>Failed/any POST /login attempts allowed per client IP per minute.</summary>
    public int LoginAttemptsPerMinute { get; set; } = 5;
}

public sealed class AuthOptionsValidator : IValidateOptions<AuthOptions>
{
    public ValidateOptionsResult Validate(string? name, AuthOptions o)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(o.Username))
            errors.Add("Auth:Username is required (env CRACKDB_Auth__Username).");
        if (string.IsNullOrWhiteSpace(o.PasswordHash))
            errors.Add("Auth:PasswordHash is required (env CRACKDB_Auth__PasswordHash); generate it with 'crackdb hashpw'.");
        else if (!LooksLikeIdentityHash(o.PasswordHash))
            errors.Add("Auth:PasswordHash is not a valid password hash; generate it with 'crackdb hashpw'.");
        if (!string.IsNullOrEmpty(o.ApiKeyHash) &&
            !(o.ApiKeyHash.Length == 64 && o.ApiKeyHash.All(Uri.IsHexDigit)))
            errors.Add("Auth:ApiKeyHash must be the 64-character hex SHA-256 of the API key.");
        if (o.LoginAttemptsPerMinute < 1)
            errors.Add("Auth:LoginAttemptsPerMinute must be at least 1.");
        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }

    // Never echo the value itself in a message.
    private static bool LooksLikeIdentityHash(string h)
    {
        try
        {
            var b = Convert.FromBase64String(h);
            return b.Length > 13 && b[0] is 0x00 or 0x01;
        }
        catch (FormatException) { return false; }
    }
}

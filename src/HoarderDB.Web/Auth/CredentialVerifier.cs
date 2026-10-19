using System.Security.Cryptography;
using System.Text;
using HoarderDB.Web.Options;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace HoarderDB.Web.Auth;

/// <summary>Checks the single configured user's password and API key. No secrets are logged.</summary>
public sealed class CredentialVerifier(IOptions<AuthOptions> options)
{
    private readonly AuthOptions _o = options.Value;
    private readonly PasswordHasher<string> _hasher = new();

    public const string SchemeCookie = "Cookies";
    public const string SchemeApiKey = "ApiKey";
    public const string SchemeSmart = "Smart";
    public const string ApiKeyHeader = "X-API-Key";

    public string Username => _o.Username;

    /// <summary>
    /// The password hash is always verified, even for a wrong username, so timing does not
    /// reveal whether the username exists. Username compare is fixed-time on digests.
    /// </summary>
    public bool VerifyLogin(string? username, string? password)
    {
        bool userOk = FixedTimeEquals(Sha(username ?? ""), Sha(_o.Username));
        var result = _hasher.VerifyHashedPassword(_o.Username, _o.PasswordHash, password ?? "");
        bool passOk = result is PasswordVerificationResult.Success or PasswordVerificationResult.SuccessRehashNeeded;
        return userOk & passOk;
    }

    public bool VerifyApiKey(string? key)
    {
        if (string.IsNullOrEmpty(_o.ApiKeyHash) || string.IsNullOrEmpty(key)) return false;
        return FixedTimeEquals(Sha(key), Convert.FromHexString(_o.ApiKeyHash));
    }

    private static byte[] Sha(string s) => SHA256.HashData(Encoding.UTF8.GetBytes(s));
    private static bool FixedTimeEquals(byte[] a, byte[] b) => CryptographicOperations.FixedTimeEquals(a, b);
}

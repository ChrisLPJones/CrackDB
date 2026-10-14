using System.Security.Cryptography;
using System.Text;
using CrackDB.Web.Options;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace CrackDB.Web.Auth;

/// <summary>Checks the single configured user's password and API key. No secrets are logged.</summary>
public sealed class CredentialVerifier
{
    private readonly AuthOptions _o;
    private readonly PasswordHasher<string> _hasher = new();
    private readonly AdminPasswordStore _store;
    // Immutable snapshot swapped atomically, so a login racing a password change sees old or new, never a mix.
    private volatile PasswordState _state;

    // Loaded in the constructor and resolved eagerly at startup (Program.cs): a corrupt store aborts boot.
    public CredentialVerifier(IOptions<AuthOptions> options, IOptions<StorageOptions> storage)
    {
        _o = options.Value;
        var dir = Directory.CreateDirectory(storage.Value.KeyPath);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(dir.FullName, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        _store = new AdminPasswordStore(dir.FullName);
        _state = _store.Load(_o.PasswordHash, AuthOptionsValidator.LooksLikeIdentityHash);
    }

    /// <summary>Session stamp embedded in the cookie (pwv claim); changes whenever the password changes.</summary>
    public string CurrentStamp => _state.Stamp;

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
        var result = _hasher.VerifyHashedPassword(_o.Username, _state.Hash, password ?? "");
        bool passOk = result is PasswordVerificationResult.Success or PasswordVerificationResult.SuccessRehashNeeded;
        return userOk & passOk;
    }

    /// <summary>Checks only the password (fixed-time PBKDF2 compare), for the current-password confirmation.</summary>
    public bool VerifyPassword(string? password)
    {
        var r = _hasher.VerifyHashedPassword(_o.Username, _state.Hash, password ?? "");
        return r is PasswordVerificationResult.Success or PasswordVerificationResult.SuccessRehashNeeded;
    }

    /// <summary>Persists FIRST, then swaps memory; on IO failure memory is unchanged and the exception propagates.</summary>
    public void SetPassword(string newPassword)
    {
        var hash = _hasher.HashPassword(_o.Username, newPassword);
        _state = _store.Save(hash, _o.PasswordHash);
    }

    public bool VerifyApiKey(string? key)
    {
        if (string.IsNullOrEmpty(_o.ApiKeyHash) || string.IsNullOrEmpty(key)) return false;
        return FixedTimeEquals(Sha(key), Convert.FromHexString(_o.ApiKeyHash));
    }

    private static byte[] Sha(string s) => SHA256.HashData(Encoding.UTF8.GetBytes(s));
    private static bool FixedTimeEquals(byte[] a, byte[] b) => CryptographicOperations.FixedTimeEquals(a, b);
}

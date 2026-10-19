using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace HoarderDB.Web.Auth;

/// <summary>Authenticates scripts via the X-API-Key header (compared by SHA-256, fixed-time).</summary>
public sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder,
    CredentialVerifier verifier)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(CredentialVerifier.ApiKeyHeader, out var values))
            return Task.FromResult(AuthenticateResult.NoResult());
        if (!verifier.VerifyApiKey(values.ToString()))
            return Task.FromResult(AuthenticateResult.Fail("Invalid API key."));

        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, verifier.Username), new Claim("auth_method", "apikey")],
            CredentialVerifier.SchemeApiKey);
        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), CredentialVerifier.SchemeApiKey)));
    }
}

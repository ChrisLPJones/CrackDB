using System.Security.Claims;
using HoarderDB.Web.Auth;
using Microsoft.AspNetCore.Authentication;

namespace HoarderDB.Web.Endpoints;

public static class AccountEndpoints
{
    public const string PasswordLimiter = "pwchange";
    public const int MinLength = 12, MaxLength = 128;
    private const int MaxFailures = 5;
    private static readonly TimeSpan LockoutFor = TimeSpan.FromMinutes(15);

    /// <summary>Per-host state (singleton): one change at a time, plus the global lockout counters (single user).</summary>
    public sealed class State
    {
        internal readonly SemaphoreSlim Gate = new(1, 1);
        internal int Failures;
        internal DateTime LockedUntil = DateTime.MinValue;
    }

    public static void MapAccount(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/account/password", async (HttpContext ctx, CredentialVerifier verifier, State st, ILoggerFactory lf) =>
        {
            var log = lf.CreateLogger("HoarderDB.Web.Account");

            // A leaked script key must not become a password takeover: cookie sessions only.
            if (ctx.User.FindFirst("auth_method")?.Value != "cookie")
                return Results.Problem(statusCode: 403, title: "Not allowed for this credential.");
            // JSON only: a cross-site form POST cannot carry this content type (CSRF, on top of SameSite=Strict).
            if (!ctx.Request.HasJsonContentType())
                return Results.Problem(statusCode: 415, title: "Content-Type must be application/json.");

            ChangeRequest? body = null;
            try { body = await ctx.Request.ReadFromJsonAsync<ChangeRequest>(ctx.RequestAborted); }
            catch (System.Text.Json.JsonException) { }
            if (body?.CurrentPassword is not { Length: > 0 } current || body.NewPassword is not { } next)
                return Results.Problem(statusCode: 400, title: "Both currentPassword and newPassword are required.");

            await st.Gate.WaitAsync(ctx.RequestAborted);
            try
            {
                if (DateTime.UtcNow < st.LockedUntil)
                    return Locked(ctx, st.LockedUntil);
                if (st.Failures >= MaxFailures) st.Failures = 0;   // lockout expired: start a fresh count

                if (!verifier.VerifyPassword(current))
                {
                    if (++st.Failures >= MaxFailures) st.LockedUntil = DateTime.UtcNow + LockoutFor;
                    log.LogWarning("Password change rejected: wrong current password (client {Ip})",
                        ctx.Connection.RemoteIpAddress);
                    // 403, not 401: the UI treats 401 as "signed out".
                    return Results.Problem(statusCode: 403, title: "Current password is incorrect.");
                }
                st.Failures = 0;

                if (next.Length < MinLength || next.Length > MaxLength)
                    return Results.Problem(statusCode: 400, title: $"New password must be {MinLength}-{MaxLength} characters.");
                if (next == current)
                    return Results.Problem(statusCode: 400, title: "New password must differ from the current password.");
                if (string.Equals(next, verifier.Username, StringComparison.OrdinalIgnoreCase))
                    return Results.Problem(statusCode: 400, title: "New password must not be the username.");

                try { verifier.SetPassword(next); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // Memory is unchanged (persist-first); log the type only, never paths' contents or input.
                    log.LogError("Could not save the new admin password ({Error})", e.GetType().Name);
                    return Results.Problem(statusCode: 500, title: "Could not save the new password.");
                }
                log.LogInformation("Admin password changed");
            }
            finally { st.Gate.Release(); }

            // Every other session is now rejected (stamp mismatch); keep this browser signed in.
            var identity = new ClaimsIdentity(
                [new Claim(ClaimTypes.Name, verifier.Username), new Claim("auth_method", "cookie"),
                 new Claim(AuthEndpoints.StampClaim, verifier.CurrentStamp)],
                CredentialVerifier.SchemeCookie);
            await ctx.SignInAsync(CredentialVerifier.SchemeCookie, new ClaimsPrincipal(identity));
            return Results.NoContent();
        }).RequireRateLimiting(PasswordLimiter);
    }

    private static IResult Locked(HttpContext ctx, DateTime until)
    {
        ctx.Response.Headers.RetryAfter = Math.Max(1, (int)(until - DateTime.UtcNow).TotalSeconds).ToString();
        return Results.Problem(statusCode: 429, title: "Too many failed attempts. Try again later.");
    }

    private sealed record ChangeRequest(string? CurrentPassword, string? NewPassword);
}

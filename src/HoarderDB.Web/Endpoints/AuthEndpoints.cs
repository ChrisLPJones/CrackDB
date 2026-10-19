using System.Security.Claims;
using HoarderDB.Web.Auth;
using Microsoft.AspNetCore.Authentication;

namespace HoarderDB.Web.Endpoints;

public static class AuthEndpoints
{
    public const string LoginLimiter = "login";

    public static void MapAuth(this IEndpointRouteBuilder app)
    {
        app.MapPost("/login", async (HttpContext ctx, CredentialVerifier verifier) =>
        {
            string? user, pass;
            bool form = ctx.Request.HasFormContentType;
            if (form)
            {
                var f = await ctx.Request.ReadFormAsync(ctx.RequestAborted);
                user = f["username"]; pass = f["password"];
            }
            else
            {
                LoginRequest? body = null;
                try { body = await ctx.Request.ReadFromJsonAsync<LoginRequest>(ctx.RequestAborted); }
                catch (System.Text.Json.JsonException) { }
                user = body?.Username; pass = body?.Password;
            }

            if (!verifier.VerifyLogin(user, pass))
                return Results.Problem(statusCode: 401, title: "Invalid username or password.");

            var identity = new ClaimsIdentity(
                [new Claim(ClaimTypes.Name, verifier.Username), new Claim("auth_method", "cookie")],
                CredentialVerifier.SchemeCookie);
            await ctx.SignInAsync(CredentialVerifier.SchemeCookie, new ClaimsPrincipal(identity));
            return form ? Results.Redirect("/") : Results.NoContent();
        }).AllowAnonymous().RequireRateLimiting(LoginLimiter);

        app.MapPost("/logout", async (HttpContext ctx) =>
        {
            await ctx.SignOutAsync(CredentialVerifier.SchemeCookie);
            return Results.NoContent();
        }).AllowAnonymous();
    }

    private sealed record LoginRequest(string? Username, string? Password);
}

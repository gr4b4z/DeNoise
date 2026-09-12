using DeNoise.Api.Auth;
using DeNoise.Application.Auth;
using DeNoise.Contracts;
using DeNoise.Domain.Users;
using Microsoft.Extensions.Options;

namespace DeNoise.Api.Endpoints;

public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuth(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/auth").WithTags("Auth");

        group.MapGet("/providers", (IOptions<LocalAuthOptions> options) => Results.Ok(new ProvidersResponse(true, null, options.Value.SelfServiceResetEnabled)))
            .AllowAnonymous().WithName("GetAuthProviders").Produces<ProvidersResponse>();

        group.MapPost("/login", async (LoginRequest request, HttpContext http, AuthService auth, IOptions<LocalAuthOptions> options) =>
        {
            if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrEmpty(request.Password))
            {
                return Problems.Result(http, StatusCodes.Status400BadRequest, "validation", "Username and password are required", null);
            }
            var result = await auth.LoginAsync(request.Username, request.Password, http.ClientIp(), http.Request.Headers.UserAgent, http.CorrelationId(), http.RequestAborted);
            switch (result.Outcome)
            {
                case LoginOutcome.Locked:
                    return Problems.Result(http, StatusCodes.Status423Locked, "locked", "Account locked", "Too many failed attempts. Try again later.", new { lockedUntil = result.LockedUntil });
                case LoginOutcome.InvalidCredentials:
                    // Byte-identical for unknown user and wrong password (10 §6).
                    return Problems.Result(http, StatusCodes.Status401Unauthorized, "invalid-credentials", "Incorrect username or password", null);
            }
            http.Response.Cookies.Append(DeNoiseAuthenticationHandler.CookieName, result.SessionToken!, new CookieOptions
            {
                HttpOnly = true,
                Secure = http.Request.IsHttps,
                SameSite = SameSiteMode.Lax,
                Path = "/",
                IsEssential = true,
                Expires = request.KeepSignedIn ? result.ExpiresAt : null,
            });
            return Results.NoContent();
        }).AllowAnonymous().RequireRateLimiting("login").WithName("Login").Produces(204).ProducesProblem(401).ProducesProblem(423).ProducesProblem(429);

        group.MapPost("/logout", async (HttpContext http, AuthService auth) =>
        {
            var principal = http.PrincipalOrNull();
            if (http.Request.Cookies.TryGetValue(DeNoiseAuthenticationHandler.CookieName, out var cookie) && principal is not null && cookie is not null)
            {
                await auth.LogoutAsync(cookie, principal.UserId, http.CorrelationId(), http.ClientIp(), http.RequestAborted);
            }
            http.Response.Cookies.Delete(DeNoiseAuthenticationHandler.CookieName, new CookieOptions { Path = "/" });
            return Results.NoContent();
        }).RequireAuthorization().WithName("Logout").Produces(204);

        group.MapGet("/csrf", (HttpContext http, CsrfTokens tokens) =>
        {
            var principal = http.Principal();
            if (principal.Credential != CredentialKind.Session) return Results.Ok(new CsrfResponse(string.Empty));
            return Results.Ok(new CsrfResponse(tokens.Issue(principal.SessionId!)));
        }).RequireAuthorization().WithName("GetCsrfToken").Produces<CsrfResponse>();

        group.MapPost("/change-password", async (ChangePasswordRequest request, HttpContext http, AuthService auth) =>
        {
            var principal = http.Principal();
            if (principal.Credential != CredentialKind.Session) return Problems.Result(http, StatusCodes.Status403Forbidden, "forbidden", "Forbidden", "Passwords can only be changed from a signed-in session.");
            var problems = await auth.ChangePasswordAsync(principal, request.Current, request.New, http.CorrelationId(), http.ClientIp(), http.RequestAborted);
            return problems.Count == 0
                ? Results.NoContent()
                : Problems.Result(http, StatusCodes.Status422UnprocessableEntity, "validation", "Password not accepted", string.Join("; ", problems), new { errors = problems });
        }).RequireAuthorization().AddEndpointFilter<CsrfFilter>().WithName("ChangePassword").Produces(204).ProducesProblem(422);

        group.MapPost("/reset/request", (HttpContext http) => Results.Accepted()).AllowAnonymous().WithName("RequestPasswordReset").Produces(202)
            .WithDescription("Self-service reset lands with SMTP (C4); always answers 202 so nothing is leaked.");

        return app;
    }
}

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

        group.MapGet("/providers", (IOptions<LocalAuthOptions> options, IOptions<OidcAuthOptions> oidc) => Results.Ok(new ProvidersResponse(
                options.Value.Enabled,
                oidc.Value.Enabled ? new OidcProvider(oidc.Value.DisplayName, OidcStartPath) : null,
                options.Value.SelfServiceResetEnabled)))
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
            SetSessionCookie(http, result.SessionToken!, request.KeepSignedIn ? result.ExpiresAt : null);
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

        MapOidc(app, group);
        return app;
    }

    public const string OidcStartPath = "/auth/oidc/start";
    public const string OidcStateCookie = "denoise_oidc";
    /// <summary>Every refused SSO sign-in lands here, whatever the reason: the audit tells them apart, the caller cannot.</summary>
    public const string OidcFailureRedirect = "/login?error=sso";

    /// <summary>
    /// M11b: authorization code + PKCE, server side. <c>start</c> seals state, nonce and verifier into a short-lived cookie and
    /// redirects to Entra; the callback validates everything, projects roles (ADR-15) and ends in the same
    /// <c>denoise_session</c> cookie a password login sets. Both answer 404 while <c>Auth:Oidc:Enabled</c> is false.
    /// </summary>
    private static void MapOidc(IEndpointRouteBuilder app, RouteGroupBuilder group)
    {
        var callbackPath = app.ServiceProvider.GetRequiredService<IOptions<OidcAuthOptions>>().Value.CallbackPath;
        var cookiePath = callbackPath.StartsWith("/auth/oidc/", StringComparison.Ordinal) ? "/auth/oidc" : "/";

        group.MapGet("/oidc/start", async (string? returnTo, HttpContext http, IExternalIdentityProvider provider, ExternalSignInService signIn, ILoggerFactory logs) =>
        {
            if (!provider.Enabled) return Results.NotFound();
            ExternalChallenge challenge;
            try
            {
                challenge = await provider.BuildChallengeAsync(ReturnToPolicy.Sanitize(returnTo), http.RequestAborted);
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or IOException or TaskCanceledException)
            {
                logs.CreateLogger("DeNoise.Auth.Oidc").LogWarning("OIDC sign-in refused: {Reason}", OidcRefusalReasons.ProviderUnavailable);
                await signIn.RecordRefusalAsync(OidcRefusalReasons.ProviderUnavailable, null, ex.GetType().Name, http.ClientIp(), http.CorrelationId(), http.RequestAborted);
                return Results.Redirect(OidcFailureRedirect);
            }
            http.Response.Cookies.Append(OidcStateCookie, challenge.StateCookie, new CookieOptions
            {
                HttpOnly = true,
                Secure = http.Request.IsHttps,
                // Lax: the callback is a top-level GET navigation from the identity provider, which Lax cookies accompany.
                SameSite = SameSiteMode.Lax,
                Path = cookiePath,
                IsEssential = true,
                Expires = challenge.ExpiresAt,
            });
            return Results.Redirect(challenge.AuthorizationUrl.AbsoluteUri);
        }).AllowAnonymous().RequireRateLimiting("oidc").WithName("StartOidcSignIn")
            .WithDescription("Starts SSO: redirects (302) to the identity provider. `returnTo` must be a local path; anything else returns to the queue. 404 when OIDC is not configured.")
            .Produces(302).Produces(404);

        app.MapGet(callbackPath, async (string? code, string? state, string? error, [Microsoft.AspNetCore.Mvc.FromQuery(Name = "error_description")] string? errorDescription,
            HttpContext http, IExternalIdentityProvider provider, ExternalSignInService signIn, ILoggerFactory logs) =>
        {
            if (!provider.Enabled) return Results.NotFound();
            var cookie = http.Request.Cookies[OidcStateCookie];
            http.Response.Cookies.Delete(OidcStateCookie, new CookieOptions { Path = cookiePath, HttpOnly = true, Secure = http.Request.IsHttps, SameSite = SameSiteMode.Lax });
            var log = logs.CreateLogger("DeNoise.Auth.Oidc");

            var result = await provider.CompleteAsync(new ExternalCallback(code, state, error, errorDescription, cookie), http.RequestAborted);
            if (!result.Succeeded)
            {
                log.LogWarning("OIDC sign-in refused: {Reason}", result.FailureReason);
                await signIn.RecordRefusalAsync(result.FailureReason!, result.Subject, result.FailureDetail, http.ClientIp(), http.CorrelationId(), http.RequestAborted);
                return Results.Redirect(OidcFailureRedirect);
            }

            var signedIn = await signIn.SignInAsync(result.Principal!, http.ClientIp(), http.Request.Headers.UserAgent, http.CorrelationId(), http.RequestAborted);
            if (!signedIn.Succeeded)
            {
                log.LogWarning("OIDC sign-in refused: {Reason}", signedIn.RefusalReason);
                return Results.Redirect(OidcFailureRedirect);
            }
            SetSessionCookie(http, signedIn.SessionToken!, null);
            return Results.Redirect(result.ReturnTo);
        }).AllowAnonymous().RequireRateLimiting("oidc").WithTags("Auth").WithName("CompleteOidcSignIn")
            .WithDescription("Identity-provider redirect target. Success sets the session cookie and redirects to the validated `returnTo`; every failure redirects to `/login?error=sso` and is audited as `auth.oidc_refused`.")
            .Produces(302).Produces(404);
    }

    /// <summary>The one place the session cookie is written, for password and SSO sign-in alike.</summary>
    private static void SetSessionCookie(HttpContext http, string token, DateTimeOffset? expires)
        => http.Response.Cookies.Append(DeNoiseAuthenticationHandler.CookieName, token, new CookieOptions
        {
            HttpOnly = true,
            Secure = http.Request.IsHttps,
            SameSite = SameSiteMode.Lax,
            Path = "/",
            IsEssential = true,
            Expires = expires,
        });
}

using System.Security.Claims;
using System.Text.Encodings.Web;
using AlertHub.Application.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace AlertHub.Api.Auth;

/// <summary>
/// One scheme, two credentials (06 §1): the session cookie for the UI or <c>Authorization: Bearer ah_pat_…</c> for API
/// clients. The resolved <see cref="AlertHubPrincipal"/> is stored in <c>HttpContext.Items</c> for handlers.
/// </summary>
public sealed class AlertHubAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "AlertHub";
    public const string CookieName = "alerthub_session";
    public const string PrincipalKey = "AlertHub.Principal";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        AlertHubPrincipal? principal = null;
        var authorization = Request.Headers.Authorization.ToString();
        if (authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            var tokens = Context.RequestServices.GetRequiredService<PersonalAccessTokenService>();
            principal = await tokens.AuthenticateAsync(authorization["Bearer ".Length..].Trim(), Context.RequestAborted);
            if (principal is null) return AuthenticateResult.Fail("invalid token");
        }
        else if (Request.Cookies.TryGetValue(CookieName, out var cookie) && !string.IsNullOrEmpty(cookie))
        {
            var auth = Context.RequestServices.GetRequiredService<AuthService>();
            var resolved = await auth.ResolveSessionAsync(cookie, Context.RequestAborted);
            if (resolved is null) return AuthenticateResult.Fail("invalid session");
            principal = AlertHubPrincipal.ForSession(resolved.Value.User, resolved.Value.Session.SessionId);
        }
        if (principal is null) return AuthenticateResult.NoResult();

        Context.Items[PrincipalKey] = principal;
        var identity = new ClaimsIdentity(SchemeName);
        identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, principal.UserId.ToString()));
        identity.AddClaim(new Claim(ClaimTypes.Name, principal.Username));
        foreach (var role in principal.Roles) identity.AddClaim(new Claim(ClaimTypes.Role, role));
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        return Problems.WriteAsync(Context, StatusCodes.Status401Unauthorized, "unauthenticated", "Authentication required", "Sign in or supply a personal access token.");
    }

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties)
        => Problems.WriteAsync(Context, StatusCodes.Status403Forbidden, "forbidden", "Forbidden", "You do not have permission to perform this action.");
}

public static class PrincipalExtensions
{
    public static AlertHubPrincipal Principal(this HttpContext http)
        => http.Items[AlertHubAuthenticationHandler.PrincipalKey] as AlertHubPrincipal ?? throw new InvalidOperationException("no authenticated principal");

    public static AlertHubPrincipal? PrincipalOrNull(this HttpContext http) => http.Items[AlertHubAuthenticationHandler.PrincipalKey] as AlertHubPrincipal;

    public static string CorrelationId(this HttpContext http) => System.Diagnostics.Activity.Current?.TraceId.ToString() ?? http.TraceIdentifier;

    /// <summary>Client address for lockout, rate limiting and audit; the in-memory test server reports none, which counts as loopback.</summary>
    public static System.Net.IPAddress ClientIp(this HttpContext http) => http.Connection.RemoteIpAddress ?? System.Net.IPAddress.Loopback;
}

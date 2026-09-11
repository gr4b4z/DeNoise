using AlertHub.Application.Auth;
using Microsoft.AspNetCore.DataProtection;

namespace AlertHub.Api.Auth;

/// <summary>
/// CSRF for cookie sessions (ADR-14): the SPA fetches a token from <c>GET /auth/csrf</c> and sends it back in
/// <c>X-CSRF-Token</c> on every mutation. The token is the session id protected with a shared Data Protection key, so
/// it is bound to the session and verifiable on any replica. Bearer (PAT) requests are exempt: no cookie, no CSRF.
/// </summary>
public sealed class CsrfTokens(IDataProtectionProvider provider)
{
    public const string HeaderName = "X-CSRF-Token";
    private readonly IDataProtector _protector = provider.CreateProtector("AlertHub.Csrf.v1");

    public string Issue(string sessionId) => _protector.Protect(sessionId);

    public bool Verify(string? token, string sessionId)
    {
        if (string.IsNullOrEmpty(token)) return false;
        try
        {
            return _protector.Unprotect(token) == sessionId;
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return false;
        }
    }
}

/// <summary>Endpoint filter: mutations from a cookie session must carry a valid CSRF header (403 otherwise).</summary>
public sealed class CsrfFilter(CsrfTokens tokens) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        if (HttpMethods.IsGet(http.Request.Method) || HttpMethods.IsHead(http.Request.Method) || HttpMethods.IsOptions(http.Request.Method)) return await next(context);
        var principal = http.PrincipalOrNull();
        if (principal is { Credential: CredentialKind.Session, SessionId: { } sessionId } && !tokens.Verify(http.Request.Headers[CsrfTokens.HeaderName], sessionId))
        {
            return Problems.Result(http, StatusCodes.Status403Forbidden, "csrf", "Missing or invalid CSRF token", $"Send the token from GET /auth/csrf in the {CsrfTokens.HeaderName} header.");
        }
        return await next(context);
    }
}

/// <summary>A user who must change their password may only reach <c>/api/v1/me</c> and <c>/auth/*</c> (06 §6).</summary>
public sealed class MustChangePasswordFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var principal = http.PrincipalOrNull();
        if (principal is { MustChangePassword: true } && !http.Request.Path.StartsWithSegments("/auth") && http.Request.Path != "/api/v1/me")
        {
            return Problems.Result(http, StatusCodes.Status403Forbidden, "password-change-required", "Password change required", "Change your password before using the application.");
        }
        return await next(context);
    }
}

/// <summary>Requires a permission from the RBAC matrix (04 §8); scope checks happen in the services.</summary>
public sealed class RequirePermissionFilter(string permission) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var principal = http.PrincipalOrNull();
        if (principal is null) return Problems.Result(http, StatusCodes.Status401Unauthorized, "unauthenticated", "Authentication required", null);
        if (!principal.Has(permission)) return Problems.Result(http, StatusCodes.Status403Forbidden, "forbidden", "Forbidden", $"This action requires '{permission}'.");
        return await next(context);
    }
}

public static class EndpointFilterExtensions
{
    public static TBuilder RequirePermission<TBuilder>(this TBuilder builder, string permission) where TBuilder : IEndpointConventionBuilder
        => builder.AddEndpointFilter(new RequirePermissionFilter(permission));
}

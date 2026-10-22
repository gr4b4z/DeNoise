using Microsoft.AspNetCore.StaticFiles;

namespace DeNoise.Api;

/// <summary>
/// Serves the React SPA that <c>build/Dockerfile</c> copies into <c>wwwroot</c> of the Api image, from the same origin as
/// the API (session cookie, CSRF and SSE assume it). Client-side routes fall back to <c>index.html</c>; paths under
/// <c>/api</c>, <c>/auth</c>, <c>/healthz</c> and <c>/openapi</c> never do, so an unknown API path stays a 404 and a
/// wrong method stays a 405. Without a built SPA (<c>dotnet run</c>, tests) nothing is registered and the Vite dev
/// server serves the UI.
/// </summary>
public static class SpaHosting
{
    public const string RouteConstraintName = "spa";

    private static readonly StaticFileOptions FileOptions = new()
    {
        // Vite fingerprints everything under /assets; index.html must be revalidated so a rollout is picked up at once.
        OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl =
            ctx.Context.Request.Path.StartsWithSegments("/assets", StringComparison.OrdinalIgnoreCase) ? "public, max-age=31536000, immutable" : "no-cache",
    };

    public static IServiceCollection AddSpaHosting(this IServiceCollection services) =>
        services.Configure<RouteOptions>(o => o.SetParameterPolicy<SpaPathConstraint>(RouteConstraintName));

    /// <summary>Adds default-file and static-file middleware when <c>wwwroot/index.html</c> exists.</summary>
    /// <returns>Whether the SPA is present; pass it to <see cref="MapSpaFallback"/>.</returns>
    public static bool UseSpaStaticFiles(this WebApplication app)
    {
        if (!app.Environment.WebRootFileProvider.GetFileInfo("index.html").Exists) return false;
        app.UseDefaultFiles();
        app.UseStaticFiles(FileOptions);
        return true;
    }

    /// <summary>Maps the lowest-priority fallback: any non-file path outside the reserved prefixes returns <c>index.html</c>.</summary>
    public static void MapSpaFallback(this WebApplication app) =>
        app.MapFallbackToFile($"{{*path:nonfile:{RouteConstraintName}}}", "index.html", FileOptions);
}

/// <summary>Rejects catch-all values whose first segment belongs to the server (<c>api</c>, <c>auth</c>, <c>healthz</c>, <c>openapi</c>).</summary>
public sealed class SpaPathConstraint : IRouteConstraint
{
    private static readonly string[] Reserved = ["api", "auth", "healthz", "openapi"];

    public bool Match(HttpContext? httpContext, IRouter? route, string routeKey, RouteValueDictionary values, RouteDirection routeDirection)
    {
        if (!values.TryGetValue(routeKey, out var value) || value?.ToString() is not { Length: > 0 } path) return true;
        var slash = path.IndexOf('/', StringComparison.Ordinal);
        var first = slash < 0 ? path : path[..slash];
        return !Reserved.Contains(first, StringComparer.OrdinalIgnoreCase);
    }
}

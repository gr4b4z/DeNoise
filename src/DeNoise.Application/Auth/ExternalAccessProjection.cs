using DeNoise.Domain.Users;

namespace DeNoise.Application.Auth;

/// <summary>What a token grants once mapped: DeNoise roles (ordered as <see cref="Roles.All"/>) and access scopes, plus the claim values no map knew.</summary>
public sealed record ProjectedAccess(string[] Roles, string[] Scopes, IReadOnlyList<string> UnmappedRoles, bool GroupsOverage);

/// <summary>
/// ADR-15 projection: <c>roles</c> claim → <c>roleMap</c> → DeNoise roles; <c>groups</c> claim → <c>scopeMap</c> → <c>access_scope</c>.
/// Unknown app roles and unmapped groups grant nothing. On groups overage (the token carries a Graph pointer instead of
/// the groups) the scopes are projected as empty: the least privilege, since the membership is not known.
/// </summary>
public static class ExternalAccessProjection
{
    public static ProjectedAccess Project(ExternalPrincipal principal, OidcAuthOptions options)
    {
        var roleMap = options.EffectiveRoleMap;
        var roles = new HashSet<string>(StringComparer.Ordinal);
        var unmapped = new List<string>();
        foreach (var value in principal.Roles)
        {
            if (roleMap.TryGetValue(value, out var role) && Domain.Users.Roles.All.Contains(role)) roles.Add(role);
            else unmapped.Add(value);
        }

        var scopes = new HashSet<string>(StringComparer.Ordinal);
        if (!principal.GroupsOverage)
        {
            foreach (var group in principal.Groups)
            {
                if (options.ScopeMap.TryGetValue(group, out var scope) && !string.IsNullOrWhiteSpace(scope)) scopes.Add(scope.Trim());
            }
        }

        return new ProjectedAccess(
            Domain.Users.Roles.All.Where(roles.Contains).ToArray(),
            scopes.Order(StringComparer.Ordinal).ToArray(),
            unmapped.Distinct(StringComparer.Ordinal).ToList(),
            principal.GroupsOverage);
    }
}

/// <summary>
/// <c>returnTo</c> after sign-in must be a path on this host: anything absolute, protocol-relative (<c>//host</c>, <c>/\host</c>),
/// containing control characters or back-slashes, or pointing back into the sign-in flow is replaced by the queue.
/// </summary>
public static class ReturnToPolicy
{
    public const string Default = "/queue";
    private const int MaxLength = 2048;

    public static string Sanitize(string? returnTo)
    {
        if (string.IsNullOrEmpty(returnTo) || returnTo.Length > MaxLength) return Default;
        if (returnTo[0] != '/') return Default;
        if (returnTo.Length > 1 && returnTo[1] is '/' or '\\') return Default;
        foreach (var c in returnTo)
        {
            if (char.IsControl(c) || c == '\\' || char.IsWhiteSpace(c)) return Default;
        }
        if (!Uri.TryCreate(returnTo, UriKind.Relative, out _)) return Default;
        var path = returnTo.Split('?', '#')[0];
        if (path.StartsWith("/auth/", StringComparison.OrdinalIgnoreCase) || path.Equals("/login", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/login/", StringComparison.OrdinalIgnoreCase))
        {
            return Default;
        }
        return returnTo;
    }
}

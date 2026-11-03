using DeNoise.Domain.Users;

namespace DeNoise.Application.Auth;

public static class OidcCredentials
{
    /// <summary>Client secret from configuration (Key Vault reference on the platform).</summary>
    public const string Secret = "secret";
    /// <summary>Workload identity federation: the client assertion is a managed-identity token for <c>api://AzureADTokenExchange</c>. Nothing to rotate.</summary>
    public const string Federated = "federated";
}

/// <summary>
/// OpenID Connect sign-in with Entra ID (M11b, <c>Auth:Oidc</c>, env <c>Auth__Oidc__*</c>). Off unless <see cref="Enabled"/> is
/// true, so deployments without the section behave exactly as before. DeNoise is a confidential client using the
/// authorization code flow with PKCE; the browser never holds an Entra token.
/// </summary>
public sealed class OidcAuthOptions
{
    public const string Section = "Auth:Oidc";

    /// <summary>The four app role values of the Entra registration (M11b) → DeNoise roles. Used when <see cref="RoleMap"/> is empty.</summary>
    public static readonly IReadOnlyDictionary<string, string> DefaultRoleMap = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["DeNoise.Viewer"] = Roles.Viewer,
        ["DeNoise.Operator"] = Roles.Operator,
        ["DeNoise.IntegrationAdmin"] = Roles.IntegrationAdmin,
        ["DeNoise.PlatformAdmin"] = Roles.PlatformAdmin,
    };

    public bool Enabled { get; set; }
    /// <summary>Single-tenant authority, e.g. <c>https://login.microsoftonline.com/&lt;tenantId&gt;/v2.0</c>. <c>common</c>, <c>organizations</c> and <c>consumers</c> are refused.</summary>
    public string? Authority { get; set; }
    /// <summary>Application (client) id of the app registration; also the expected id_token audience.</summary>
    public string? ClientId { get; set; }
    /// <summary><see cref="OidcCredentials.Federated"/> (production) or <see cref="OidcCredentials.Secret"/>.</summary>
    public string Credential { get; set; } = OidcCredentials.Federated;
    /// <summary>Client secret, only with <c>credential: secret</c>. Secret; never logged.</summary>
    public string? ClientSecret { get; set; }
    /// <summary>Client id of a user-assigned managed identity for <c>credential: federated</c>; unset uses the system-assigned identity.</summary>
    public string? ManagedIdentityClientId { get; set; }
    /// <summary>Audience of the managed-identity token used as client assertion.</summary>
    public string FederatedTokenAudience { get; set; } = "api://AzureADTokenExchange";
    /// <summary>Callback path; the redirect URI registered in Entra is <c>DeNoise__PublicBaseUrl</c> + this path.</summary>
    public string CallbackPath { get; set; } = "/auth/oidc/callback";
    /// <summary>Requested scopes.</summary>
    public string Scope { get; set; } = "openid profile email";
    public string RoleClaim { get; set; } = "roles";
    public string GroupClaim { get; set; } = "groups";
    /// <summary>Claim whose value matches a pre-SSO account by e-mail (see <see cref="MatchByEmail"/>).</summary>
    public string EmailClaim { get; set; } = "email";
    /// <summary>Entra app role value → DeNoise role. Empty means <see cref="DefaultRoleMap"/>.</summary>
    public Dictionary<string, string> RoleMap { get; set; } = new(StringComparer.Ordinal);
    /// <summary>Entra group object id → <c>access_scope</c> (spec §19.2). A group maps to exactly one scope.</summary>
    public Dictionary<string, string> ScopeMap { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>A first sign-in with at least one mapped app role creates the local user (ADR-15).</summary>
    public bool AutoProvision { get; set; } = true;
    /// <summary>Resolve groups through Microsoft Graph on overage. Not implemented in this release: enabling it fails start-up.</summary>
    public bool GraphGroupLookup { get; set; }
    /// <summary>Link a first SSO sign-in to a pre-SSO local account with the same e-mail (ADR-14 migration note). Never applies to <c>local_only</c> accounts or guests.</summary>
    public bool MatchByEmail { get; set; } = true;
    /// <summary>Label of the SSO button.</summary>
    public string DisplayName { get; set; } = "Microsoft";
    /// <summary>Require HTTPS for the discovery document and JWKS. Only the test and Compose mock provider turn it off.</summary>
    public bool RequireHttpsMetadata { get; set; } = true;
    public TimeSpan ClockSkew { get; set; } = TimeSpan.FromMinutes(5);
    /// <summary>How long a started sign-in may take before the callback is refused.</summary>
    public TimeSpan StateLifetime { get; set; } = TimeSpan.FromMinutes(10);

    public IReadOnlyDictionary<string, string> EffectiveRoleMap => RoleMap.Count == 0 ? DefaultRoleMap : RoleMap;

    /// <summary>Configuration problems; checked at start-up only when <see cref="Enabled"/> is true.</summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        if (!Enabled) return problems;
        if (!Uri.TryCreate(Authority, UriKind.Absolute, out var authority))
        {
            problems.Add("Auth:Oidc:Authority must be an absolute URL");
        }
        else
        {
            if (RequireHttpsMetadata && authority.Scheme != Uri.UriSchemeHttps) problems.Add("Auth:Oidc:Authority must use https");
            var tenant = authority.AbsolutePath.Trim('/').Split('/').FirstOrDefault() ?? string.Empty;
            if (tenant is "common" or "organizations" or "consumers") problems.Add("Auth:Oidc:Authority must name a single tenant (not common, organizations or consumers)");
        }
        if (string.IsNullOrWhiteSpace(ClientId)) problems.Add("Auth:Oidc:ClientId is required");
        if (Credential is not (OidcCredentials.Secret or OidcCredentials.Federated)) problems.Add("Auth:Oidc:Credential must be 'federated' or 'secret'");
        if (Credential == OidcCredentials.Secret && string.IsNullOrEmpty(ClientSecret)) problems.Add("Auth:Oidc:ClientSecret is required with credential 'secret'");
        if (string.IsNullOrEmpty(CallbackPath) || CallbackPath[0] != '/') problems.Add("Auth:Oidc:CallbackPath must start with '/'");
        var unknown = EffectiveRoleMap.Values.Where(r => !Roles.All.Contains(r)).Distinct().ToList();
        if (unknown.Count > 0) problems.Add($"Auth:Oidc:RoleMap maps to unknown roles: {string.Join(", ", unknown)}");
        if (GraphGroupLookup) problems.Add("Auth:Oidc:GraphGroupLookup is not supported yet; emit only groups assigned to the application (groupMembershipClaims: ApplicationGroup) to stay under the claim limit");
        if (StateLifetime <= TimeSpan.Zero) problems.Add("Auth:Oidc:StateLifetime must be positive");
        return problems;
    }
}

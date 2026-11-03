using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DeNoise.Application.Auth;
using DeNoise.Application.Notifications;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace DeNoise.Infrastructure.Auth;

/// <summary>
/// OpenID Connect authorization code flow with PKCE against Entra ID (M11b), DeNoise as a confidential client.
/// <list type="bullet">
/// <item>The challenge carries <c>state</c>, <c>nonce</c> and an S256 PKCE challenge; state, nonce, verifier and the
/// validated <c>returnTo</c> travel back in a Data Protection–sealed cookie (shared key ring, ADR-18, so any replica can
/// finish the sign-in).</item>
/// <item>A state is consumed once per replica (replay guard); Entra additionally refuses a redeemed code.</item>
/// <item>The code is redeemed with a client secret or a federated client assertion; the id_token is validated for
/// signature (tenant JWKS through a cached <see cref="ConfigurationManager{T}"/> that refreshes on key rollover), issuer,
/// audience, expiry with clock skew on the injected <see cref="TimeProvider"/>, and nonce.</item>
/// </list>
/// Tokens, codes, verifiers and secrets are never logged or audited; failures carry a reason code only.
/// </summary>
public sealed class OidcIdentityProvider(
    IOptions<OidcAuthOptions> options, IOptions<NotificationOptions> notifications, OidcMetadataSource metadata, IHttpClientFactory http,
    IDataProtectionProvider dataProtection, IClientAssertionProvider assertions, IMemoryCache cache, TimeProvider time, ILogger<OidcIdentityProvider> logger)
    : IExternalIdentityProvider
{
    public const string HttpClientName = "denoise-oidc";
    private const string Purpose = "DeNoise.Auth.Oidc.State.v1";
    private static readonly string[] Algorithms =
    [
        SecurityAlgorithms.RsaSha256, SecurityAlgorithms.RsaSha384, SecurityAlgorithms.RsaSha512,
        SecurityAlgorithms.RsaSsaPssSha256, SecurityAlgorithms.RsaSsaPssSha384, SecurityAlgorithms.RsaSsaPssSha512,
        SecurityAlgorithms.EcdsaSha256, SecurityAlgorithms.EcdsaSha384, SecurityAlgorithms.EcdsaSha512,
    ];

    private readonly IDataProtector _protector = dataProtection.CreateProtector(Purpose);
    private readonly JsonWebTokenHandler _handler = new() { MapInboundClaims = false };

    public string Name => Domain.Users.AuthProviders.Oidc;
    public bool Enabled => options.Value.Enabled;

    /// <summary>Absolute redirect URI registered in Entra: public base URL + callback path.</summary>
    public string RedirectUri => notifications.Value.PublicBaseUrl.TrimEnd('/') + options.Value.CallbackPath;

    public async Task<ExternalChallenge> BuildChallengeAsync(string returnTo, CancellationToken ct = default)
    {
        var o = options.Value;
        if (!o.Enabled) throw new InvalidOperationException("OIDC sign-in is not enabled");
        var config = await metadata.GetAsync(ct);
        var now = time.GetUtcNow();
        var payload = new StatePayload(Random(), Random(), Random(), ReturnToPolicy.Sanitize(returnTo), now.ToUnixTimeSeconds());
        var challenge = Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(payload.Verifier)));

        var query = new Dictionary<string, string>
        {
            ["client_id"] = o.ClientId!,
            ["response_type"] = "code",
            ["response_mode"] = "query",
            ["redirect_uri"] = RedirectUri,
            ["scope"] = o.Scope,
            ["state"] = payload.State,
            ["nonce"] = payload.Nonce,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
        };
        var endpoint = config.AuthorizationEndpoint;
        var separator = endpoint.Contains('?', StringComparison.Ordinal) ? '&' : '?';
        var url = endpoint + separator + string.Join('&', query.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
        var sealedState = _protector.Protect(JsonSerializer.Serialize(payload));
        return new ExternalChallenge(new Uri(url), sealedState, now + o.StateLifetime);
    }

    public async Task<ExternalAuthResult> CompleteAsync(ExternalCallback callback, CancellationToken ct = default)
    {
        var o = options.Value;
        if (!o.Enabled) return ExternalAuthResult.Fail(OidcRefusalReasons.Disabled);
        if (string.IsNullOrEmpty(callback.StateCookie)) return ExternalAuthResult.Fail(OidcRefusalReasons.StateMissing);

        StatePayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<StatePayload>(_protector.Unprotect(callback.StateCookie));
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or FormatException)
        {
            return ExternalAuthResult.Fail(OidcRefusalReasons.StateInvalid, "state cookie does not unprotect");
        }
        if (payload is null) return ExternalAuthResult.Fail(OidcRefusalReasons.StateInvalid);

        var returnTo = ReturnToPolicy.Sanitize(payload.ReturnTo);
        var now = time.GetUtcNow();
        if (now - DateTimeOffset.FromUnixTimeSeconds(payload.IssuedAt) > o.StateLifetime) return ExternalAuthResult.Fail(OidcRefusalReasons.StateExpired, returnTo: returnTo);
        if (string.IsNullOrEmpty(callback.State) || !FixedTimeEquals(callback.State, payload.State))
        {
            return ExternalAuthResult.Fail(OidcRefusalReasons.StateInvalid, "state parameter does not match the cookie", returnTo: returnTo);
        }

        // Consume the state before redeeming anything: a replayed callback (same state + cookie) stops here on this replica.
        var replayKey = "oidc-state:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(payload.State)));
        if (cache.TryGetValue(replayKey, out _)) return ExternalAuthResult.Fail(OidcRefusalReasons.StateReplayed, returnTo: returnTo);
        cache.Set(replayKey, true, o.StateLifetime + o.ClockSkew);

        if (!string.IsNullOrEmpty(callback.Error))
        {
            return ExternalAuthResult.Fail(OidcRefusalReasons.IdpError, Truncate($"{callback.Error}: {callback.ErrorDescription}", 300), returnTo: returnTo);
        }
        if (string.IsNullOrEmpty(callback.Code)) return ExternalAuthResult.Fail(OidcRefusalReasons.CodeMissing, returnTo: returnTo);

        OpenIdConnectConfiguration config;
        string? idToken;
        try
        {
            config = await metadata.GetAsync(ct);
            var (status, error) = await RedeemAsync(config, callback.Code, payload.Verifier, ct);
            if (status is null) return ExternalAuthResult.Fail(OidcRefusalReasons.CodeRejected, error, returnTo: returnTo);
            idToken = status;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or IOException)
        {
            logger.LogWarning("OIDC provider unavailable during sign-in: {ErrorType}", ex.GetType().Name);
            return ExternalAuthResult.Fail(OidcRefusalReasons.ProviderUnavailable, ex.GetType().Name, returnTo: returnTo);
        }

        var validation = await ValidateAsync(idToken, config, ct);
        if (validation.Reason is not null) return ExternalAuthResult.Fail(validation.Reason, validation.Detail, Claim(validation.Token, "oid"), returnTo);
        var token = validation.Token!;

        var validTo = token.ValidTo == DateTime.MinValue ? (DateTimeOffset?)null : new DateTimeOffset(token.ValidTo, TimeSpan.Zero);
        var validFrom = token.ValidFrom == DateTime.MinValue ? (DateTimeOffset?)null : new DateTimeOffset(token.ValidFrom, TimeSpan.Zero);
        var oid = Claim(token, "oid");
        if (validTo is null || now > validTo.Value + o.ClockSkew) return ExternalAuthResult.Fail(OidcRefusalReasons.TokenExpired, subject: oid, returnTo: returnTo);
        if (validFrom is { } nbf && nbf > now + o.ClockSkew) return ExternalAuthResult.Fail(OidcRefusalReasons.TokenNotYetValid, subject: oid, returnTo: returnTo);

        var nonce = Claim(token, "nonce");
        if (nonce is null || !FixedTimeEquals(nonce, payload.Nonce)) return ExternalAuthResult.Fail(OidcRefusalReasons.NonceMismatch, subject: oid, returnTo: returnTo);
        if (string.IsNullOrWhiteSpace(oid)) return ExternalAuthResult.Fail(OidcRefusalReasons.MissingSubject, "token has no oid claim", returnTo: returnTo);

        var principal = new ExternalPrincipal(
            Issuer: token.Issuer,
            Subject: oid,
            Email: Claim(token, o.EmailClaim),
            DisplayName: Claim(token, "name"),
            PreferredUsername: Claim(token, "preferred_username"),
            Roles: StringValues(token, o.RoleClaim),
            Groups: StringValues(token, o.GroupClaim),
            GroupsOverage: IsOverage(token, o.GroupClaim),
            // Entra's optional `acct` claim: 0 = member of the tenant, 1 = guest.
            IsGuest: Claim(token, "acct") == "1");
        return new ExternalAuthResult(principal, returnTo);
    }

    private async Task<(string? IdToken, string? Error)> RedeemAsync(OpenIdConnectConfiguration config, string code, string verifier, CancellationToken ct)
    {
        var o = options.Value;
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = RedirectUri,
            ["client_id"] = o.ClientId!,
            ["code_verifier"] = verifier,
            ["scope"] = o.Scope,
        };
        if (o.Credential == OidcCredentials.Secret)
        {
            form["client_secret"] = o.ClientSecret!;
        }
        else
        {
            form["client_assertion_type"] = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer";
            form["client_assertion"] = await assertions.GetAssertionAsync(ct);
        }

        using var client = http.CreateClient(HttpClientName);
        using var content = new FormUrlEncodedContent(form);
        using var response = await client.PostAsync(new Uri(config.TokenEndpoint), content, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        string? error = null;
        string? idToken = null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                if (doc.RootElement.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String) error = e.GetString();
                if (doc.RootElement.TryGetProperty("id_token", out var t) && t.ValueKind == JsonValueKind.String) idToken = t.GetString();
            }
        }
        catch (JsonException)
        {
            error = "non-JSON token response";
        }
        if (!response.IsSuccessStatusCode || idToken is null)
        {
            // Only the OAuth error code is kept (e.g. invalid_grant for a redeemed or tampered code): never the body, which can echo request data.
            return (null, Truncate($"{(int)response.StatusCode} {error ?? (idToken is null ? "no id_token" : "error")}", 120));
        }
        return (idToken, null);
    }

    private async Task<(JsonWebToken? Token, string? Reason, string? Detail)> ValidateAsync(string idToken, OpenIdConnectConfiguration config, CancellationToken ct)
    {
        var result = await _handler.ValidateTokenAsync(idToken, Parameters(config));
        if (!result.IsValid && result.Exception is SecurityTokenSignatureKeyNotFoundException)
        {
            // Signing key rollover: refresh the JWKS once and retry before refusing.
            metadata.RequestRefresh();
            config = await metadata.GetAsync(ct);
            result = await _handler.ValidateTokenAsync(idToken, Parameters(config));
        }
        if (result.IsValid) return ((JsonWebToken)result.SecurityToken, null, null);

        var reason = result.Exception switch
        {
            SecurityTokenInvalidAudienceException => OidcRefusalReasons.InvalidAudience,
            SecurityTokenInvalidIssuerException => OidcRefusalReasons.InvalidIssuer,
            SecurityTokenSignatureKeyNotFoundException or SecurityTokenInvalidSignatureException => OidcRefusalReasons.InvalidSignature,
            SecurityTokenExpiredException => OidcRefusalReasons.TokenExpired,
            SecurityTokenNotYetValidException => OidcRefusalReasons.TokenNotYetValid,
            _ => OidcRefusalReasons.InvalidToken,
        };
        JsonWebToken? unvalidated = null;
        try
        {
            unvalidated = _handler.ReadJsonWebToken(idToken);
        }
        catch (ArgumentException)
        {
        }
        // Exception messages are PII-scrubbed by IdentityModel; only the type name is kept anyway.
        return (unvalidated, reason, result.Exception?.GetType().Name);
    }

    private TokenValidationParameters Parameters(OpenIdConnectConfiguration config) => new()
    {
        ValidateIssuer = true,
        ValidIssuer = config.Issuer,
        ValidateAudience = true,
        ValidAudience = options.Value.ClientId,
        IssuerSigningKeys = config.SigningKeys,
        RequireSignedTokens = true,
        ValidAlgorithms = Algorithms,
        // Lifetime is checked against the injected TimeProvider after validation (AGENTS.md rule 5).
        ValidateLifetime = false,
        RequireExpirationTime = true,
    };

    // JsonWebToken.Claims flattens arrays into one claim per element and renders numbers and objects as text, which is
    // exactly the shape needed here (roles and groups are arrays, Entra's acct is a number, _claim_names an object).
    private static IReadOnlyList<string> StringValues(JsonWebToken token, string claim)
        => token.Claims.Where(c => c.Type == claim && c.Value.Length > 0).Select(c => c.Value).ToList();

    private static string? Claim(JsonWebToken? token, string name) => token?.Claims.FirstOrDefault(c => c.Type == name)?.Value;

    /// <summary>Entra replaces a too-large groups claim with <c>_claim_names.groups</c> (or <c>hasgroups</c> in implicit flows).</summary>
    private static bool IsOverage(JsonWebToken token, string groupClaim)
    {
        if (Claim(token, "hasgroups") is { } has && has.Equals("true", StringComparison.OrdinalIgnoreCase)) return true;
        if (Claim(token, "_claim_names") is not { } names) return false;
        try
        {
            using var doc = JsonDocument.Parse(names);
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty(groupClaim, out _);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string Random() => Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));

    private static bool FixedTimeEquals(string a, string b)
        => CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    private sealed record StatePayload(string State, string Nonce, string Verifier, string ReturnTo, long IssuedAt);
}

/// <summary>Discovery document and JWKS of the configured authority, cached and refreshed by <see cref="ConfigurationManager{T}"/> (12 h, or on demand after a key miss).</summary>
public sealed class OidcMetadataSource
{
    private readonly Lazy<ConfigurationManager<OpenIdConnectConfiguration>> _manager;

    public OidcMetadataSource(IOptions<OidcAuthOptions> options, IHttpClientFactory http)
    {
        _manager = new Lazy<ConfigurationManager<OpenIdConnectConfiguration>>(() =>
        {
            var o = options.Value;
            var address = o.Authority!.TrimEnd('/') + "/.well-known/openid-configuration";
            var retriever = new HttpDocumentRetriever(http.CreateClient(OidcIdentityProvider.HttpClientName)) { RequireHttps = o.RequireHttpsMetadata };
            return new ConfigurationManager<OpenIdConnectConfiguration>(address, new OpenIdConnectConfigurationRetriever(), retriever)
            {
                AutomaticRefreshInterval = TimeSpan.FromHours(12),
                RefreshInterval = TimeSpan.FromMinutes(5),
            };
        });
    }

    public async Task<OpenIdConnectConfiguration> GetAsync(CancellationToken ct)
    {
        var config = await _manager.Value.GetConfigurationAsync(ct);
        if (string.IsNullOrEmpty(config.Issuer) || config.Issuer.Contains("{tenantid}", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("discovery document names no single-tenant issuer");
        }
        return config;
    }

    public void RequestRefresh() => _manager.Value.RequestRefresh();
}

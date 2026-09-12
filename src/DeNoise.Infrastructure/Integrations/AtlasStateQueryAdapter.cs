using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using DeNoise.Application.Abstractions;
using DeNoise.Application.Notifications;
using DeNoise.Application.Processing;
using DeNoise.Domain.Episodes;
using DeNoise.Domain.Integrations;
using Microsoft.Extensions.Logging;

namespace DeNoise.Infrastructure.Integrations;

/// <summary>Atlas Admin API access of one integration, read from <c>cfg.integration.capabilities</c> (<c>atlas</c> object; keys stored encrypted).</summary>
public sealed record AtlasApiSettings(Uri BaseUrl, string GroupId, string PublicKey, string PrivateKey);

public static class AtlasCapabilities
{
    public const string DefaultBaseUrl = "https://cloud.mongodb.com";
    public const string ApiVersionMediaType = "application/vnd.atlas.2023-01-01+json";

    /// <summary>Writes the <c>atlas</c> capability with encrypted keys into a capabilities document; <c>state_query</c> becomes true.</summary>
    public static string Configure(string? capabilitiesJson, string groupId, string publicKey, string privateKey, string? baseUrl, ISecretProtector protector)
    {
        JsonObject root;
        try
        {
            root = JsonNode.Parse(string.IsNullOrWhiteSpace(capabilitiesJson) ? "{}" : capabilitiesJson) as JsonObject ?? new JsonObject();
        }
        catch (JsonException)
        {
            root = new JsonObject();
        }
        root["state_query"] = true;
        root["atlas"] = new JsonObject
        {
            ["base_url"] = string.IsNullOrWhiteSpace(baseUrl) ? DefaultBaseUrl : baseUrl!.TrimEnd('/'),
            ["group_id"] = groupId,
            ["credentials_enc"] = protector.Protect($"{publicKey}:{privateKey}"),
        };
        return root.ToJsonString(JsonDefaults.Stored);
    }

    public static AtlasApiSettings? Read(Integration integration, ISecretProtector protector)
    {
        try
        {
            if (JsonNode.Parse(integration.Capabilities) is not JsonObject root || root["atlas"] is not JsonObject atlas) return null;
            var enc = atlas["credentials_enc"]?.ToString();
            var group = atlas["group_id"]?.ToString();
            if (string.IsNullOrEmpty(enc) || string.IsNullOrEmpty(group)) return null;
            var creds = protector.Unprotect(enc);
            var colon = creds.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0) return null;
            var baseUrl = atlas["base_url"]?.ToString();
            return new AtlasApiSettings(new Uri(string.IsNullOrWhiteSpace(baseUrl) ? DefaultBaseUrl : baseUrl!, UriKind.Absolute), group, creds[..colon], creds[(colon + 1)..]);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or UriFormatException or System.Security.Cryptography.CryptographicException)
        {
            return null;
        }
    }

    /// <summary>What the API and UI may show: group id and base URL, never the keys.</summary>
    public static (string? GroupId, string? BaseUrl, bool HasCredentials) Describe(string capabilitiesJson)
    {
        try
        {
            if (JsonNode.Parse(capabilitiesJson) is JsonObject root && root["atlas"] is JsonObject atlas)
            {
                return (atlas["group_id"]?.ToString(), atlas["base_url"]?.ToString(), !string.IsNullOrEmpty(atlas["credentials_enc"]?.ToString()));
            }
        }
        catch (JsonException)
        {
            // fall through
        }
        return (null, null, false);
    }
}

/// <summary>Creates the HTTP handler for one Atlas account — replaced in tests with a scripted handler.</summary>
public interface IAtlasHttpHandlerFactory
{
    HttpMessageHandler Create(AtlasApiSettings settings);
}

public sealed class DigestAtlasHttpHandlerFactory : IAtlasHttpHandlerFactory
{
    public HttpMessageHandler Create(AtlasApiSettings settings) => new SocketsHttpHandler
    {
        Credentials = new NetworkCredential(settings.PublicKey, settings.PrivateKey), // Atlas programmatic API keys authenticate with HTTP Digest
        PreAuthenticate = false,
        ConnectTimeout = TimeSpan.FromSeconds(5),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    };
}

/// <summary>
/// <c>queryable_state</c> for MongoDB Atlas (spec §12.1, 04 §5.3 guard 7): asks the Admin API whether the alert behind an
/// episode is still open, and probes the project endpoint for the <c>api_probe</c> coverage method. Every outcome is
/// reported, never guessed: an unreachable or unauthorised API is <see cref="StateQueryOutcome.Error"/>.
/// </summary>
public sealed class AtlasStateQueryAdapter(ISecretProtector protector, ILogger<AtlasStateQueryAdapter> logger, IAtlasHttpHandlerFactory? handlers = null) : IStateQueryAdapter, IDisposable
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private readonly IAtlasHttpHandlerFactory _handlers = handlers ?? new DigestAtlasHttpHandlerFactory();
    private readonly ConcurrentDictionary<string, HttpClient> _clients = new(StringComparer.Ordinal);

    public async Task<StateQueryResult> QueryAsync(Integration integration, Episode episode, CancellationToken ct = default)
    {
        var settings = AtlasCapabilities.Read(integration, protector);
        if (settings is null) return new StateQueryResult(StateQueryOutcome.Unsupported, "atlas api credentials not configured");
        if (string.IsNullOrEmpty(episode.SourceAlertId)) return new StateQueryResult(StateQueryOutcome.Error, "episode has no source alert id to query");
        try
        {
            using var response = await Client(integration, settings).GetAsync(new Uri($"api/atlas/v2/groups/{Uri.EscapeDataString(settings.GroupId)}/alerts/{Uri.EscapeDataString(episode.SourceAlertId)}", UriKind.Relative), ct);
            if (response.StatusCode == HttpStatusCode.NotFound) return new StateQueryResult(StateQueryOutcome.NotActive, "alert not found in Atlas (closed and expired, or deleted)");
            if (!response.IsSuccessStatusCode) return new StateQueryResult(StateQueryOutcome.Error, $"atlas api responded {(int)response.StatusCode}");
            var body = await response.Content.ReadAsStringAsync(ct);
            var status = (JsonNode.Parse(body) as JsonObject)?["status"]?.ToString();
            return status?.ToUpperInvariant() switch
            {
                "OPEN" or "TRACKING" => new StateQueryResult(StateQueryOutcome.Active, $"atlas alert status {status}"),
                "CLOSED" or "CANCELLED" => new StateQueryResult(StateQueryOutcome.NotActive, $"atlas alert status {status}"),
                _ => new StateQueryResult(StateQueryOutcome.Error, $"atlas alert status '{status}' not understood"),
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            logger.LogWarning(ex, "Atlas state query failed for integration {IntegrationId}", integration.IntegrationId);
            return new StateQueryResult(StateQueryOutcome.Error, ex.GetType().Name + ": " + ex.Message);
        }
    }

    public async Task<ProbeResult> ProbeAsync(Integration integration, CancellationToken ct = default)
    {
        var settings = AtlasCapabilities.Read(integration, protector);
        if (settings is null) return new ProbeResult(false, false, "atlas api credentials not configured");
        try
        {
            using var response = await Client(integration, settings).GetAsync(new Uri($"api/atlas/v2/groups/{Uri.EscapeDataString(settings.GroupId)}", UriKind.Relative), ct);
            return response.IsSuccessStatusCode
                ? new ProbeResult(true, true, $"atlas project reachable ({(int)response.StatusCode})")
                : new ProbeResult(true, false, $"atlas api responded {(int)response.StatusCode}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new ProbeResult(true, false, ex.GetType().Name + ": " + ex.Message);
        }
    }

    private HttpClient Client(Integration integration, AtlasApiSettings settings)
        => _clients.GetOrAdd($"{integration.IntegrationId}:{integration.Version}", _ =>
        {
            var client = new HttpClient(_handlers.Create(settings), disposeHandler: true) { BaseAddress = new Uri(settings.BaseUrl.GetLeftPart(UriPartial.Authority) + "/"), Timeout = Timeout };
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue(AtlasCapabilities.ApiVersionMediaType));
            client.DefaultRequestHeaders.UserAgent.ParseAdd("DeNoise/1.0");
            return client;
        });

    public void Dispose()
    {
        foreach (var client in _clients.Values) client.Dispose();
        _clients.Clear();
    }
}

/// <summary>Picks the adapter by integration type; types without one report <see cref="StateQueryOutcome.Unsupported"/> (the lifecycle guards treat that as "cannot verify").</summary>
public sealed class StateQueryAdapterRouter(AtlasStateQueryAdapter atlas) : IStateQueryAdapter
{
    public Task<StateQueryResult> QueryAsync(Integration integration, Episode episode, CancellationToken ct = default)
        => integration.Type == IntegrationTypes.Atlas ? atlas.QueryAsync(integration, episode, ct) : Task.FromResult(new StateQueryResult(StateQueryOutcome.Unsupported, $"no state query adapter for '{integration.Type}'"));

    public Task<ProbeResult> ProbeAsync(Integration integration, CancellationToken ct = default)
        => integration.Type == IntegrationTypes.Atlas ? atlas.ProbeAsync(integration, ct) : Task.FromResult(new ProbeResult(false, false, $"no api probe for '{integration.Type}'"));
}

using System.Net;
using System.Text.Json.Nodes;
using DeNoise.Domain.Integrations;

namespace DeNoise.Application.Ingest;

/// <summary>
/// Opt-in for producers that cannot set request headers. An Azure Monitor action group sends a bare POST to the
/// configured URI, so neither <c>Authorization: Bearer</c> (06 §2) nor a signature header (07 §3) can carry the
/// credential; the same holds for any webhook sender whose only configurable field is the URL. Such an integration
/// may present its ingest token as the last path segment instead, the way heartbeat pings already do (06 §3).
/// The capability is per integration so the weaker mode never opens by default.
/// </summary>
public static class IngestCredentialPolicy
{
    /// <summary>Capability flag in <c>cfg.integration.capabilities</c>: <c>{ "path_credential": true }</c>.</summary>
    public const string PathCredentialCapability = "path_credential";

    public static bool AllowsPathCredential(Integration integration)
    {
        if (string.IsNullOrWhiteSpace(integration.Capabilities)) return false;
        try
        {
            return JsonNode.Parse(integration.Capabilities) is JsonObject root
                && root[PathCredentialCapability] is JsonValue flag
                && flag.TryGetValue<bool>(out var allowed)
                && allowed;
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// A path credential travels in the URL, where ingress logs and activity history keep it, so the allow list that
    /// is already part of an integration's configuration is enforced for this mode. An empty list means no
    /// restriction. Header-authenticated requests are unchanged: their allow list is still stored but not applied.
    /// </summary>
    public static bool PermitsAddress(IReadOnlyList<string>? allowList, IPAddress? address)
    {
        if (allowList is null || allowList.Count == 0) return true;
        if (address is null) return false;

        var candidate = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
        foreach (var entry in allowList)
        {
            if (Matches(entry, candidate)) return true;
        }
        return false;
    }

    private static bool Matches(string entry, IPAddress address)
    {
        var value = entry.Trim();
        if (value.Length == 0) return false;

        if (value.Contains('/', StringComparison.Ordinal))
        {
            return IPNetwork.TryParse(value, out var network)
                && network.BaseAddress.AddressFamily == address.AddressFamily
                && network.Contains(address);
        }

        if (!IPAddress.TryParse(value, out var single)) return false;
        if (single.IsIPv4MappedToIPv6) single = single.MapToIPv4();
        return single.Equals(address);
    }
}

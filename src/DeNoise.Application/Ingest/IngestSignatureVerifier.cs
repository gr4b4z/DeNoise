using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DeNoise.Application.Abstractions;
using DeNoise.Application.Notifications;
using DeNoise.Domain.Integrations;

namespace DeNoise.Application.Ingest;

/// <summary>
/// Per-integration webhook signature settings (<c>cfg.integration.hmac_config</c>): Atlas signs the raw body with HMAC
/// (historically SHA-1 in <c>X-MMS-Signature</c>, base64); the algorithm, header and encoding are configurable because that
/// contract must be verified against a captured request before <c>required</c> is switched on (07 §3).
/// </summary>
public sealed record HmacConfig(string Algorithm = "sha1", string Header = "X-MMS-Signature", string Encoding = "base64", bool Required = false)
{
    public static readonly IReadOnlyList<string> Algorithms = ["sha1", "sha256"];
    public static readonly IReadOnlyList<string> Encodings = ["base64", "hex"];

    public static HmacConfig? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            if (JsonNode.Parse(json) is not JsonObject o) return null;
            return new HmacConfig(
                (o["algorithm"]?.ToString() ?? "sha1").ToLowerInvariant(),
                o["header"]?.ToString() is { Length: > 0 } h ? h : "X-MMS-Signature",
                (o["encoding"]?.ToString() ?? "base64").ToLowerInvariant(),
                o["required"] is JsonValue r && r.TryGetValue<bool>(out var req) && req);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public string ToJson() => JsonSerializer.Serialize(new { algorithm = Algorithm, header = Header, encoding = Encoding, required = Required }, JsonDefaults.Stored);

    public IReadOnlyList<Mapping.MappingValidationError> Validate()
    {
        var errors = new List<Mapping.MappingValidationError>();
        if (!Algorithms.Contains(Algorithm)) errors.Add(new Mapping.MappingValidationError("$.hmac.algorithm", "must be sha1 or sha256"));
        if (!Encodings.Contains(Encoding)) errors.Add(new Mapping.MappingValidationError("$.hmac.encoding", "must be base64 or hex"));
        if (string.IsNullOrWhiteSpace(Header) || Header.Any(c => c is ':' or ' ' or '\r' or '\n')) errors.Add(new Mapping.MappingValidationError("$.hmac.header", "must be a header name"));
        return errors;
    }
}

public enum SignatureVerdict
{
    /// <summary>No secret configured: nothing to check.</summary>
    NotConfigured,
    Valid,
    /// <summary>Header absent. Rejected only when <see cref="HmacConfig.Required"/>.</summary>
    Missing,
    Invalid,
}

public interface IIngestSignatureVerifier
{
    SignatureVerdict Verify(Integration integration, ReadOnlySpan<byte> body, IReadOnlyDictionary<string, string> headers);
}

/// <summary>Constant-time HMAC check over the raw body; the secret is decrypted per request (it never leaves the process).</summary>
public sealed class IngestSignatureVerifier(ISecretProtector protector) : IIngestSignatureVerifier
{
    public SignatureVerdict Verify(Integration integration, ReadOnlySpan<byte> body, IReadOnlyDictionary<string, string> headers)
    {
        if (integration.HmacSecretEnc is null) return SignatureVerdict.NotConfigured;
        var config = HmacConfig.Parse(integration.HmacConfig) ?? new HmacConfig();
        if (!headers.TryGetValue(config.Header, out var presented) || string.IsNullOrWhiteSpace(presented)) return SignatureVerdict.Missing;
        var secret = Encoding.UTF8.GetBytes(protector.Unprotect(integration.HmacSecretEnc));
        var expected = Compute(config.Algorithm, secret, body);
        var actual = Decode(config.Encoding, presented.Trim());
        return actual is not null && actual.Length == expected.Length && CryptographicOperations.FixedTimeEquals(actual, expected) ? SignatureVerdict.Valid : SignatureVerdict.Invalid;
    }

    /// <summary>Signs a body the way a producer would — used by the destination-free test send of the wizard and by tests.</summary>
    public static string Sign(HmacConfig config, string secret, ReadOnlySpan<byte> body)
    {
        var mac = Compute(config.Algorithm, Encoding.UTF8.GetBytes(secret), body);
        return config.Encoding == "hex" ? Convert.ToHexStringLower(mac) : Convert.ToBase64String(mac);
    }

#pragma warning disable CA5350 // Atlas' documented webhook signature is HMAC-SHA1 (07 §3); sha256 is offered and preferred where the producer supports it.
    private static byte[] Compute(string algorithm, byte[] secret, ReadOnlySpan<byte> body)
        => algorithm == "sha256" ? HMACSHA256.HashData(secret, body) : HMACSHA1.HashData(secret, body);
#pragma warning restore CA5350

    private static byte[]? Decode(string encoding, string value)
    {
        try
        {
            return encoding == "hex" ? Convert.FromHexString(value) : Convert.FromBase64String(value);
        }
        catch (FormatException)
        {
            return null;
        }
    }
}

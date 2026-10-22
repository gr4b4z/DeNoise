using System.Security.Cryptography;
using System.Text;

namespace DeNoise.Domain.Alerts;

/// <summary>An identity component before canonicalisation: a scalar string, an object of strings, or nothing.</summary>
public sealed record IdentityInput(string Name, string? Scalar, IReadOnlyDictionary<string, string>? Object, bool Optional = false, bool CaseInsensitive = false)
{
    public bool IsMissing => Scalar is null && (Object is null || Object.Count == 0);
}

/// <summary>Thrown when a required identity component is missing (04 §4 step 1). Surfaces as a mapping exception.</summary>
public sealed class MissingIdentityComponentException(string component)
    : Exception($"Identity component '{component}' is missing and not marked optional; refusing to merge under an empty key.")
{
    public string Component { get; } = component;
}

/// <summary>Fingerprint canonicalisation (04 §4). Deterministic across workers by construction.</summary>
public static class Fingerprint
{
    public const string EmptyMarker = "∅";
    private const char ComponentSeparator = '';

    public sealed record Result(string Hex, IReadOnlyList<IdentityComponent> Components, string Canonical);

    public static Result Compute(Guid integrationId, int identityVersion, IReadOnlyList<IdentityInput> inputs)
    {
        if (inputs.Count == 0) throw new ArgumentException("identity must have at least one component", nameof(inputs));

        var components = new List<IdentityComponent>(inputs.Count);
        foreach (var input in inputs)
        {
            string value;
            if (input.IsMissing)
            {
                if (!input.Optional) throw new MissingIdentityComponentException(input.Name);
                value = EmptyMarker;
            }
            else if (input.Object is not null && input.Object.Count > 0)
            {
                value = string.Join(';', input.Object
                    .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                    .Select(kv => kv.Key + "=" + CanonicaliseString(kv.Value, input.CaseInsensitive)));
            }
            else
            {
                value = CanonicaliseString(input.Scalar!, input.CaseInsensitive);
            }
            components.Add(new IdentityComponent(input.Name, value));
        }

        var canonical = new StringBuilder()
            .Append(integrationId.ToString("D")).Append(ComponentSeparator)
            .Append(identityVersion).Append(ComponentSeparator)
            .AppendJoin(ComponentSeparator, components.Select(c => c.Name + "=" + c.Value))
            .ToString();
        var hex = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        return new Result(hex, components, canonical);
    }

    /// <summary>Unicode NFC, trimmed, case preserved unless the component is marked case-insensitive.</summary>
    public static string CanonicaliseString(string value, bool caseInsensitive)
    {
        var s = value.Normalize(NormalizationForm.FormC).Trim();
        return caseInsensitive ? s.ToLowerInvariant() : s;
    }

    public static byte[] ToBytes(string hex) => Convert.FromHexString(hex);
    public static string ToHex(byte[] bytes) => Convert.ToHexStringLower(bytes);
}

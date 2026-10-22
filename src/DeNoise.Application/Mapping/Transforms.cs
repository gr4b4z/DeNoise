using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace DeNoise.Application.Mapping;

/// <summary>String transforms (07 §1). Applied in order; every transform is total (never throws on odd input).</summary>
public static partial class Transforms
{
    public static string Apply(string value, IReadOnlyList<string> transforms)
    {
        foreach (var t in transforms)
        {
            value = ApplyOne(value, t);
        }
        return value;
    }

    public static string ApplyOne(string value, string transform)
    {
        switch (transform)
        {
            case "lower": return value.ToLowerInvariant();
            case "upper": return value.ToUpperInvariant();
            case "trim": return value.Trim();
            case "azure_subscription":
                {
                    var m = SubscriptionRegex().Match(value);
                    return m.Success ? "/subscriptions/" + m.Groups[1].Value.ToLowerInvariant() : value;
                }
            case "azure_resource_group":
                {
                    var m = ResourceGroupRegex().Match(value);
                    return m.Success ? m.Groups[1].Value : value;
                }
            case "hostname":
                {
                    var v = value.Trim();
                    if (Uri.TryCreate(v.Contains("://", StringComparison.Ordinal) ? v : "tcp://" + v, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host))
                    {
                        return uri.Host;
                    }
                    var colon = v.LastIndexOf(':');
                    return colon > 0 && int.TryParse(v.AsSpan(colon + 1), out _) ? v[..colon] : v;
                }
            case "sha256":
                return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
            default:
                if (transform.StartsWith("truncate:", StringComparison.Ordinal) && int.TryParse(transform.AsSpan("truncate:".Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n >= 0)
                {
                    return value.Length <= n ? value : value[..n];
                }
                return value;
        }
    }

    [GeneratedRegex("/subscriptions/([^/]+)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex SubscriptionRegex();

    [GeneratedRegex("/resourceGroups/([^/]+)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex ResourceGroupRegex();
}

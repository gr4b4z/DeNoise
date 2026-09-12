using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DeNoise.Application.Mapping;

/// <summary>Type conversion for <c>as:</c> (07 §1) and the "empty" rule used by <c>first_nonempty</c> and <c>required</c>.</summary>
public static class ValueCoercion
{
    private static readonly string[] TimestampFormats =
    [
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK", "yyyy-MM-dd'T'HH:mm:ssK", "yyyy-MM-dd HH:mm:ss.FFFFFFFK", "yyyy-MM-dd HH:mm:ssK",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF", "yyyy-MM-dd'T'HH:mm:ss", "yyyy-MM-dd",
    ];

    public static bool IsEmpty(JsonNode? node) => node switch
    {
        null => true,
        JsonArray a => a.Count == 0,
        JsonObject o => o.Count == 0,
        JsonValue v => v.GetValueKind() == JsonValueKind.Null || (v.TryGetValue<string>(out var s) && string.IsNullOrWhiteSpace(s)),
        _ => false,
    };

    public static string AsDisplayString(JsonNode? node) => node switch
    {
        null => string.Empty,
        JsonValue v when v.GetValueKind() == JsonValueKind.String => v.GetValue<string>(),
        JsonValue v when v.GetValueKind() == JsonValueKind.Null => string.Empty,
        JsonValue v => v.ToJsonString(),
        _ => node.ToJsonString(),
    };

    /// <summary>Coerce to the requested type; throws <see cref="MappingException"/> when impossible.</summary>
    public static JsonNode? Coerce(JsonNode? node, ValueType type, string field)
    {
        if (node is null) return null;
        try
        {
            switch (type)
            {
                case ValueType.String:
                    return JsonValue.Create(AsDisplayString(node));
                case ValueType.Timestamp:
                    return JsonValue.Create(ParseTimestamp(node, field).ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture));
                case ValueType.Int:
                    if (node is JsonValue iv)
                    {
                        if (iv.TryGetValue<long>(out var l)) return JsonValue.Create(l);
                        if (iv.TryGetValue<double>(out var d)) return JsonValue.Create((long)d);
                        if (long.TryParse(AsDisplayString(iv).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)) return JsonValue.Create(parsed);
                    }
                    throw new MappingException($"cannot convert '{AsDisplayString(node)}' to int", field);
                case ValueType.Float:
                    if (node is JsonValue fv)
                    {
                        if (fv.TryGetValue<double>(out var d)) return JsonValue.Create(d);
                        if (double.TryParse(AsDisplayString(fv).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)) return JsonValue.Create(parsed);
                    }
                    throw new MappingException($"cannot convert '{AsDisplayString(node)}' to float", field);
                case ValueType.Bool:
                    if (node is JsonValue bv)
                    {
                        if (bv.TryGetValue<bool>(out var b)) return JsonValue.Create(b);
                        var s = AsDisplayString(bv).Trim().ToLowerInvariant();
                        if (s is "true" or "1" or "yes") return JsonValue.Create(true);
                        if (s is "false" or "0" or "no") return JsonValue.Create(false);
                    }
                    throw new MappingException($"cannot convert '{AsDisplayString(node)}' to bool", field);
                case ValueType.StringArray:
                    return node switch
                    {
                        JsonArray arr => new JsonArray(arr.Select(x => (JsonNode?)JsonValue.Create(AsDisplayString(x))).ToArray()),
                        _ => new JsonArray(JsonValue.Create(AsDisplayString(node))),
                    };
                case ValueType.Object:
                    if (node is JsonObject) return node.DeepClone();
                    throw new MappingException($"expected an object for '{field}'", field);
                default:
                    return node;
            }
        }
        catch (MappingException)
        {
            throw;
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException or OverflowException)
        {
            throw new MappingException($"cannot convert value for '{field}': {ex.Message}", field, ex);
        }
    }

    public static DateTimeOffset ParseTimestamp(JsonNode node, string field)
    {
        if (node is JsonValue v)
        {
            if (v.TryGetValue<DateTimeOffset>(out var dto)) return dto;
            if (v.TryGetValue<long>(out var epoch))
            {
                // seconds vs milliseconds heuristic: anything past year 2200 in seconds is milliseconds
                return epoch > 7_258_118_400L ? DateTimeOffset.FromUnixTimeMilliseconds(epoch) : DateTimeOffset.FromUnixTimeSeconds(epoch);
            }
            if (v.TryGetValue<double>(out var epochD))
            {
                return epochD > 7_258_118_400d ? DateTimeOffset.FromUnixTimeMilliseconds((long)epochD) : DateTimeOffset.FromUnixTimeSeconds((long)epochD);
            }
            var s = AsDisplayString(v).Trim();
            if (DateTimeOffset.TryParseExact(s, TimestampFormats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var exact)) return exact;
            if (DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var loose)) return loose;
        }
        throw new MappingException($"'{AsDisplayString(node)}' is not a timestamp", field);
    }

    /// <summary>Object of strings for <c>dimensions</c> / <c>labels</c>: nested values are rendered as JSON text.</summary>
    public static IReadOnlyDictionary<string, string>? AsStringObject(JsonNode? node)
    {
        if (node is not JsonObject obj || obj.Count == 0) return null;
        return obj.Where(kv => !IsEmpty(kv.Value)).ToDictionary(kv => kv.Key, kv => AsDisplayString(kv.Value), StringComparer.Ordinal);
    }
}

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace AlertHub.Application.Mapping;

/// <summary>
/// YAML → <see cref="JsonNode"/> so mapping and policy documents share one in-memory shape regardless of
/// whether they arrived as YAML (import, editor) or JSON (stored <c>body jsonb</c>). Plain scalars are typed
/// by YAML 1.2 core-schema rules (int, float, bool, null); quoted scalars stay strings.
/// </summary>
public static class YamlJson
{
    public static JsonNode? Parse(string yaml)
    {
        var stream = new YamlStream();
        try
        {
            using var reader = new StringReader(yaml);
            stream.Load(reader);
        }
        catch (YamlException ex)
        {
            throw new MappingValidationException([new MappingValidationError("$", $"invalid YAML: {ex.Message}")]);
        }
        if (stream.Documents.Count == 0) return null;
        if (stream.Documents.Count > 1) throw new MappingValidationException([new MappingValidationError("$", "exactly one YAML document is expected")]);
        return Convert(stream.Documents[0].RootNode);
    }

    public static string ToYaml(JsonNode? node)
    {
        var yaml = new YamlStream(new YamlDocument(ToYamlNode(node)));
        using var writer = new StringWriter();
        yaml.Save(writer, assignAnchors: false);
        var text = writer.ToString();
        return text.EndsWith("...\n", StringComparison.Ordinal) ? text[..^4] : text;
    }

    private static JsonNode? Convert(YamlNode node) => node switch
    {
        YamlMappingNode map => ConvertMap(map),
        YamlSequenceNode seq => new JsonArray(seq.Children.Select(Convert).ToArray()),
        YamlScalarNode scalar => ConvertScalar(scalar),
        _ => null,
    };

    private static JsonObject ConvertMap(YamlMappingNode map)
    {
        var obj = new JsonObject();
        foreach (var (key, value) in map.Children)
        {
            var name = key is YamlScalarNode s ? s.Value ?? string.Empty : key.ToString() ?? string.Empty;
            if (obj.ContainsKey(name)) throw new MappingValidationException([new MappingValidationError(name, "duplicate key")]);
            obj[name] = Convert(value);
        }
        return obj;
    }

    private static JsonNode? ConvertScalar(YamlScalarNode scalar)
    {
        var value = scalar.Value;
        if (value is null) return null;
        if (scalar.Style is ScalarStyle.SingleQuoted or ScalarStyle.DoubleQuoted or ScalarStyle.Literal or ScalarStyle.Folded)
        {
            return JsonValue.Create(value);
        }
        switch (value)
        {
            case "" or "~" or "null" or "Null" or "NULL": return null;
            case "true" or "True" or "TRUE": return JsonValue.Create(true);
            case "false" or "False" or "FALSE": return JsonValue.Create(false);
        }
        if (long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var l)) return JsonValue.Create(l);
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && long.TryParse(value.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex)) return JsonValue.Create(hex);
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && !value.Contains(':', StringComparison.Ordinal)) return JsonValue.Create(d);
        return JsonValue.Create(value);
    }

    private static YamlNode ToYamlNode(JsonNode? node)
    {
        switch (node)
        {
            case null:
                return new YamlScalarNode("null") { Style = ScalarStyle.Plain };
            case JsonObject obj:
                var map = new YamlMappingNode();
                foreach (var (key, value) in obj) map.Add(new YamlScalarNode(key), ToYamlNode(value));
                return map;
            case JsonArray arr:
                var seq = new YamlSequenceNode();
                foreach (var item in arr) seq.Add(ToYamlNode(item));
                return seq;
            case JsonValue v:
                return v.GetValueKind() switch
                {
                    JsonValueKind.String => new YamlScalarNode(v.GetValue<string>()) { Style = LooksTyped(v.GetValue<string>()) ? ScalarStyle.DoubleQuoted : ScalarStyle.Any },
                    JsonValueKind.True => new YamlScalarNode("true"),
                    JsonValueKind.False => new YamlScalarNode("false"),
                    JsonValueKind.Null => new YamlScalarNode("null"),
                    _ => new YamlScalarNode(v.ToJsonString()),
                };
            default:
                return new YamlScalarNode(node.ToJsonString());
        }
    }

    private static bool LooksTyped(string s) => s.Length == 0 || s is "true" or "false" or "null" or "~" || double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out _);
}

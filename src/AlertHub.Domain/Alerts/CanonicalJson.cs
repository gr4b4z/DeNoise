using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace AlertHub.Domain.Alerts;

/// <summary>
/// Deterministic JSON serialisation for hashing: object keys sorted ordinally, no whitespace, numbers rendered
/// from their raw text, strings escaped identically on every worker. Used by the body-hash delivery key (04 §3).
/// </summary>
public static class CanonicalJson
{
    public static byte[] Canonicalise(ReadOnlySpan<byte> utf8Json, IReadOnlyCollection<string>? ignorePointers = null)
    {
        using var doc = JsonDocument.Parse(utf8Json.ToArray(), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        var ignore = (ignorePointers ?? []).Select(NormalisePointer).ToHashSet(StringComparer.Ordinal);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false, SkipValidation = false }))
        {
            Write(doc.RootElement, writer, "", ignore);
        }
        return buffer.WrittenSpan.ToArray();
    }

    private static void Write(JsonElement element, Utf8JsonWriter writer, string pointer, HashSet<string> ignore)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    var childPointer = pointer + "/" + Escape(property.Name);
                    if (ignore.Contains(childPointer)) continue;
                    writer.WritePropertyName(property.Name);
                    Write(property.Value, writer, childPointer, ignore);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                var index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    var childPointer = pointer + "/" + index.ToString(CultureInfo.InvariantCulture);
                    if (!ignore.Contains(childPointer)) Write(item, writer, childPointer, ignore);
                    index++;
                }
                writer.WriteEndArray();
                break;
            case JsonValueKind.Number:
                writer.WriteRawValue(element.GetRawText());
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString());
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            default:
                writer.WriteNullValue();
                break;
        }
    }

    /// <summary>
    /// Accepts JSON Pointers (<c>/data/essentials/firedDateTime</c>) and simple dotted JSONPaths
    /// (<c>$.data.essentials.firedDateTime</c>, <c>$.items[0].ts</c>) and normalises both to a pointer.
    /// </summary>
    public static string NormalisePointer(string path)
    {
        if (path.StartsWith('/')) return path;
        var p = path.StartsWith("$.", StringComparison.Ordinal) ? path[2..] : path.TrimStart('$');
        var sb = new StringBuilder();
        foreach (var segment in p.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            var name = segment;
            var bracket = name.IndexOf('[', StringComparison.Ordinal);
            var indices = new List<string>();
            while (bracket >= 0)
            {
                var close = name.IndexOf(']', bracket);
                indices.Add(name[(bracket + 1)..close].Trim('\'', '"'));
                name = name.Remove(bracket, close - bracket + 1);
                bracket = name.IndexOf('[', StringComparison.Ordinal);
            }
            if (name.Length > 0) sb.Append('/').Append(Escape(name));
            foreach (var i in indices) sb.Append('/').Append(Escape(i));
        }
        return sb.ToString();
    }

    private static string Escape(string token) => token.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);
}

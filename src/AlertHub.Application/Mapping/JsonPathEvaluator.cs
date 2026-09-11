using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Json.Path;

namespace AlertHub.Application.Mapping;

/// <summary>
/// JSONPath (RFC 9535 subset, 07) over the raw body with the <c>$headers</c> pseudo-root for request headers.
/// Parsed paths are cached; mapping documents are immutable so the cache never goes stale.
/// </summary>
public static class JsonPathEvaluator
{
    public static readonly PathParsingOptions ParsingOptions = new() { AllowInOperator = true, TolerateExtraWhitespace = true, AllowMathOperations = true };
    private static readonly ConcurrentDictionary<string, JsonPath> Cache = new(StringComparer.Ordinal);
    private const string HeadersRoot = "$headers";

    public static IReadOnlyList<JsonNode?> Evaluate(string path, JsonNode? body, JsonObject? headers)
    {
        var effective = NormalisePath(path);
        var root = path.StartsWith(HeadersRoot, StringComparison.Ordinal) ? headers : body;
        if (root is null) return [];
        var compiled = Cache.GetOrAdd(effective, p => JsonPath.Parse(p, ParsingOptions));
        var result = compiled.Evaluate(root);
        if (result.Matches is null) return [];
        return result.Matches.Select(m => m.Value).ToList();
    }

    /// <summary>Single value: the first match, or null. Arrays and objects are returned as nodes.</summary>
    public static JsonNode? First(string path, JsonNode? body, JsonObject? headers)
    {
        var matches = Evaluate(path, body, headers);
        return matches.Count == 0 ? null : matches[0];
    }

    /// <summary><c>$headers['X-Name']</c> / <c>$headers.X-Name</c> → <c>$['X-Name']</c> evaluated against the headers object.</summary>
    public static string NormalisePath(string path)
    {
        if (!path.StartsWith(HeadersRoot, StringComparison.Ordinal)) return path;
        var rest = path[HeadersRoot.Length..];
        if (rest.StartsWith('.'))
        {
            var name = rest[1..];
            return $"$['{name.Replace("'", "\\'", StringComparison.Ordinal)}']";
        }
        return "$" + rest;
    }
}

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AlertHub.Domain.Common;

namespace AlertHub.Application.Mapping;

/// <summary>
/// Evaluates predicates against a reference resolver. In mappings the resolver reads JSONPaths from the raw
/// body and canonical names from already-mapped fields; in routing it reads the normalised event + episode.
/// Severity references compare on the canonical ordinal (07 §5).
/// </summary>
public static class PredicateEvaluator
{
    public static bool Evaluate(Predicate predicate, Func<string, JsonNode?> resolve) => predicate switch
    {
        Predicate.All all => all.Items.All(p => Evaluate(p, resolve)),
        Predicate.Any any => any.Items.Any(p => Evaluate(p, resolve)),
        Predicate.Not not => !Evaluate(not.Item, resolve),
        Predicate.Exists ex => !ValueCoercion.IsEmpty(resolve(ex.Ref)),
        Predicate.Eq eq => Equal(eq.Ref, resolve(eq.Ref), eq.Value),
        Predicate.Neq neq => !Equal(neq.Ref, resolve(neq.Ref), neq.Value),
        Predicate.In @in => @in.Values.Any(v => Equal(@in.Ref, resolve(@in.Ref), v)),
        Predicate.Nin nin => !nin.Values.Any(v => Equal(nin.Ref, resolve(nin.Ref), v)),
        Predicate.Regex rx => resolve(rx.Ref) is { } node && rx.Pattern.IsMatch(ValueCoercion.AsDisplayString(node)),
        Predicate.Gte gte => Compare(gte.Ref, resolve(gte.Ref), gte.Value) is { } c && c >= 0,
        Predicate.Lte lte => Compare(lte.Ref, resolve(lte.Ref), lte.Value) is { } c && c <= 0,
        _ => false,
    };

    private static bool Equal(string reference, JsonNode? actual, JsonNode? expected)
    {
        if (IsSeverityRef(reference))
        {
            return actual is not null && expected is not null && SeverityRank(actual) == SeverityRank(expected);
        }
        if (actual is null || expected is null) return actual is null && expected is null;
        if (actual is JsonValue a && expected is JsonValue e)
        {
            if (a.GetValueKind() == JsonValueKind.String || e.GetValueKind() == JsonValueKind.String)
            {
                return string.Equals(ValueCoercion.AsDisplayString(a), ValueCoercion.AsDisplayString(e), StringComparison.Ordinal);
            }
            if (TryNumber(a, out var an) && TryNumber(e, out var en)) return an.Equals(en);
            if (a.TryGetValue<bool>(out var ab) && e.TryGetValue<bool>(out var eb)) return ab == eb;
        }
        return JsonNode.DeepEquals(actual, expected);
    }

    private static int? Compare(string reference, JsonNode? actual, JsonNode? expected)
    {
        if (actual is null || expected is null) return null;
        if (IsSeverityRef(reference)) return SeverityRank(actual).CompareTo(SeverityRank(expected));
        if (actual is JsonValue a && expected is JsonValue e)
        {
            if (TryNumber(a, out var an) && TryNumber(e, out var en)) return an.CompareTo(en);
            var asText = ValueCoercion.AsDisplayString(a);
            var esText = ValueCoercion.AsDisplayString(e);
            if (DateTimeOffset.TryParse(asText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var ad)
                && DateTimeOffset.TryParse(esText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var ed))
            {
                return ad.CompareTo(ed);
            }
            return string.CompareOrdinal(asText, esText);
        }
        return null;
    }

    private static bool IsSeverityRef(string reference) => reference is "severity" or "source_severity" || reference.EndsWith(".severity", StringComparison.Ordinal);

    private static int SeverityRank(JsonNode node) => SeverityExtensions.ParseWire(ValueCoercion.AsDisplayString(node)).Rank();

    private static bool TryNumber(JsonValue value, out double number)
    {
        if (value.TryGetValue<double>(out number)) return true;
        if (value.TryGetValue<long>(out var l)) { number = l; return true; }
        if (value.TryGetValue<int>(out var i)) { number = i; return true; }
        if (value.GetValueKind() == JsonValueKind.String && double.TryParse(value.GetValue<string>(), NumberStyles.Float, CultureInfo.InvariantCulture, out number)) return true;
        number = 0;
        return false;
    }
}

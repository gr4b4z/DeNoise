using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace DeNoise.Application.Mapping;

/// <summary>Parses the predicate grammar (07 §5) from its JSON/YAML shape.</summary>
public static class PredicateParser
{
    public static Predicate? Parse(JsonNode? node, string path, List<MappingValidationError> errors, Func<string, bool>? refValidator = null)
    {
        if (node is not JsonObject obj || obj.Count != 1)
        {
            errors.Add(new MappingValidationError(path, "predicate must be an object with exactly one operator (eq, neq, in, nin, regex, exists, gte, lte, all, any, not)"));
            return null;
        }
        var (op, arg) = obj.First();
        switch (op)
        {
            case "all":
            case "any":
                {
                    if (arg is not JsonArray items || items.Count == 0)
                    {
                        errors.Add(new MappingValidationError($"{path}.{op}", "must be a non-empty list of predicates"));
                        return null;
                    }
                    var parsed = new List<Predicate>();
                    for (var i = 0; i < items.Count; i++)
                    {
                        var p = Parse(items[i], $"{path}.{op}[{i}]", errors, refValidator);
                        if (p is not null) parsed.Add(p);
                    }
                    return op == "all" ? new Predicate.All(parsed) : new Predicate.Any(parsed);
                }
            case "not":
                {
                    var inner = Parse(arg, $"{path}.not", errors, refValidator);
                    return inner is null ? null : new Predicate.Not(inner);
                }
            case "exists":
                {
                    var reference = arg?.ToString();
                    if (string.IsNullOrWhiteSpace(reference))
                    {
                        errors.Add(new MappingValidationError($"{path}.exists", "must be a reference"));
                        return null;
                    }
                    ValidateRef(reference, $"{path}.exists", errors, refValidator);
                    return new Predicate.Exists(reference);
                }
            case "eq":
            case "neq":
            case "gte":
            case "lte":
            case "regex":
                {
                    if (arg is not JsonArray pair || pair.Count != 2 || pair[0] is not JsonValue refNode || !refNode.TryGetValue<string>(out var reference))
                    {
                        errors.Add(new MappingValidationError($"{path}.{op}", "must be [ <ref>, <value> ]"));
                        return null;
                    }
                    ValidateRef(reference, $"{path}.{op}[0]", errors, refValidator);
                    var value = pair[1]?.DeepClone();
                    switch (op)
                    {
                        case "eq": return new Predicate.Eq(reference, value);
                        case "neq": return new Predicate.Neq(reference, value);
                        case "gte": return new Predicate.Gte(reference, value);
                        case "lte": return new Predicate.Lte(reference, value);
                        default:
                            try
                            {
                                return new Predicate.Regex(reference, new Regex(value?.ToString() ?? string.Empty, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)));
                            }
                            catch (ArgumentException ex)
                            {
                                errors.Add(new MappingValidationError($"{path}.regex[1]", $"invalid regex: {ex.Message}"));
                                return null;
                            }
                    }
                }
            case "in":
            case "nin":
                {
                    if (arg is not JsonArray pair || pair.Count != 2 || pair[0] is not JsonValue refNode || !refNode.TryGetValue<string>(out var reference) || pair[1] is not JsonArray values)
                    {
                        errors.Add(new MappingValidationError($"{path}.{op}", "must be [ <ref>, [v1, v2, …] ]"));
                        return null;
                    }
                    ValidateRef(reference, $"{path}.{op}[0]", errors, refValidator);
                    var list = values.Select(v => v?.DeepClone()).ToList();
                    return op == "in" ? new Predicate.In(reference, list) : new Predicate.Nin(reference, list);
                }
            default:
                errors.Add(new MappingValidationError($"{path}.{op}", "unknown operator"));
                return null;
        }
    }

    private static void ValidateRef(string reference, string path, List<MappingValidationError> errors, Func<string, bool>? refValidator)
    {
        if (refValidator is not null)
        {
            if (!refValidator(reference)) errors.Add(new MappingValidationError(path, $"unknown reference '{reference}'"));
            return;
        }
        MappingParser.ValidateRef(reference, path, errors);
    }
}

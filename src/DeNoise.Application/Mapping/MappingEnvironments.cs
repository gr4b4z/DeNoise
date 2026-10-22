using System.Text.Json.Nodes;
using DeNoise.Domain.Alerts;

namespace DeNoise.Application.Mapping;

/// <summary>
/// The environment values a mapping can produce that are knowable when it is saved (ADR-17): constants, <c>lookup.map</c> and
/// <c>lookup.regex_map</c> targets, defaults, and lookup tables an <c>enrich</c> step writes into <c>environment</c>. Values copied
/// from the payload (<c>path</c>, <c>template</c>, <c>header</c>) cannot be checked here; at query time anything outside the dictionary
/// counts as unclassified.
/// </summary>
public static class MappingEnvironments
{
    public sealed record Literal(string Path, string Value);

    public static IReadOnlyList<Literal> Literals(MappingDocument doc)
    {
        var literals = new List<Literal>();
        // Same base path as MappingParser reports, so the editor highlights the same place for both kinds of error.
        var basePath = doc.BasePath;
        if (doc.Fields.TryGetValue(CanonicalFields.Environment, out var rule))
        {
            Collect(rule, $"{basePath}.fields.environment", [], literals);
        }
        for (var i = 0; i < doc.Enrich.Count; i++)
        {
            var step = doc.Enrich[i];
            if (step.Set != CanonicalFields.Environment || !doc.LookupTables.TryGetValue(step.LookupTable, out var table)) continue;
            foreach (var (key, value) in table)
            {
                if (!string.IsNullOrEmpty(value)) literals.Add(new Literal($"{basePath}.lookup_tables.{step.LookupTable}.{key}", value));
            }
        }
        return literals;
    }

    /// <summary>One error per literal outside the dictionary, with the path the mapping editor highlights.</summary>
    public static IReadOnlyList<MappingValidationError> Validate(MappingDocument doc, IReadOnlyCollection<string> dictionary)
    {
        var known = dictionary as IReadOnlySet<string> ?? new HashSet<string>(dictionary, StringComparer.Ordinal);
        var allowed = string.Join(", ", dictionary.Order(StringComparer.Ordinal));
        return Literals(doc)
            .Where(l => !known.Contains(l.Value))
            .Select(l => new MappingValidationError(l.Path, $"environment '{l.Value}' is not a configured environment class (one of: {allowed}); add it to the environment dictionary or map to an existing class"))
            .ToList();
    }

    private static void Collect(MappingRule rule, string path, IReadOnlyList<string> outerTransforms, List<Literal> literals)
    {
        // The engine applies a rule's own transforms before those of the rule that contains it (first_nonempty candidates).
        var transforms = rule.Transform.Concat(outerTransforms).ToList();
        void Add(string suffix, JsonNode? node)
        {
            if (node is null) return;
            var text = node is JsonValue v && v.TryGetValue<string>(out var s) ? s : node.ToJsonString();
            var value = Transforms.Apply(text, transforms);
            if (value.Length > 0) literals.Add(new Literal(path + suffix, value));
        }

        switch (rule)
        {
            case ConstRule c:
                Add(".const", c.Value);
                break;
            case LookupRule l:
                if (l.Map is not null)
                {
                    foreach (var (key, value) in l.Map) Add($".lookup.map.{key}", value);
                }
                if (l.RegexMap is not null)
                {
                    for (var i = 0; i < l.RegexMap.Count; i++) Add($".lookup.regex_map[{i}]", l.RegexMap[i].Value);
                }
                if (l.HasLookupDefault) Add(".lookup.default", l.LookupDefault);
                break;
            case FirstNonEmptyRule f:
                for (var i = 0; i < f.Candidates.Count; i++) Collect(f.Candidates[i], $"{path}.first_nonempty[{i}]", transforms, literals);
                break;
        }
        if (rule.HasDefault) Add(".default", rule.Default);
    }
}

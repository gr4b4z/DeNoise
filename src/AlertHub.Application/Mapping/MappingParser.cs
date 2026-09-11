using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AlertHub.Domain.Alerts;
using Json.Path;

namespace AlertHub.Application.Mapping;

/// <summary>Turns a YAML/JSON mapping document into a <see cref="MappingDocument"/>, collecting every error with its path (07 §1 "Validation on save").</summary>
public static class MappingParser
{
    private static readonly HashSet<string> KnownRuleKeys = ["path", "const", "first_nonempty", "lookup", "template", "object", "pick", "array", "header", "as", "transform", "default", "max_length", "when"];
    private static readonly HashSet<string> KnownTransforms = ["lower", "upper", "trim", "azure_subscription", "azure_resource_group", "hostname", "sha256"];
    private static readonly HashSet<string> KnownDocumentKeys = ["version", "identity_version", "applies_when", "ignore_paths_for_body_key", "fields", "required", "identity", "enrich", "lookup_tables", "lifecycle_profile_hint"];
    private static readonly HashSet<string> LifecycleProfiles = ["explicit_recovery", "queryable_state", "repeating_while_active", "one_shot", "unknown"];

    public static MappingDocument ParseYaml(string yaml, int? versionOverride = null)
    {
        var root = YamlJson.Parse(yaml) ?? throw new MappingValidationException([new MappingValidationError("$", "document is empty")]);
        return Parse(root, versionOverride);
    }

    public static MappingDocument Parse(JsonNode root, int? versionOverride = null)
    {
        var errors = new List<MappingValidationError>();
        if (root is not JsonObject top)
        {
            throw new MappingValidationException([new MappingValidationError("$", "document must be a mapping")]);
        }
        var doc = top["mapping"] as JsonObject ?? top; // accept both `mapping:` wrapper and bare
        var basePath = ReferenceEquals(doc, top) ? "$" : "$.mapping";

        foreach (var key in doc.Select(kv => kv.Key).Where(k => !KnownDocumentKeys.Contains(k)))
        {
            errors.Add(new MappingValidationError($"{basePath}.{key}", "unknown key"));
        }

        var fields = new Dictionary<string, MappingRule>(StringComparer.Ordinal);
        if (doc["fields"] is JsonObject fieldsNode)
        {
            foreach (var (name, ruleNode) in fieldsNode)
            {
                var path = $"{basePath}.fields.{name}";
                if (!CanonicalFields.All.Contains(name))
                {
                    errors.Add(new MappingValidationError(path, $"unknown target field '{name}'"));
                    continue;
                }
                var rule = ParseRule(ruleNode, path, errors);
                if (rule is not null) fields[name] = rule;
            }
        }
        else
        {
            errors.Add(new MappingValidationError($"{basePath}.fields", "required"));
        }

        var required = ReadStringList(doc["required"], $"{basePath}.required", errors);
        foreach (var r in required)
        {
            if (!CanonicalFields.All.Contains(r)) errors.Add(new MappingValidationError($"{basePath}.required", $"unknown field '{r}'"));
            else if (!fields.ContainsKey(r)) errors.Add(new MappingValidationError($"{basePath}.required", $"required field '{r}' has no rule"));
        }
        if (!fields.ContainsKey(CanonicalFields.EventType)) errors.Add(new MappingValidationError($"{basePath}.fields.event_type", "a rule for event_type is required"));

        var identity = new List<IdentitySpec>();
        if (doc["identity"] is JsonArray identityNode && identityNode.Count > 0)
        {
            for (var i = 0; i < identityNode.Count; i++)
            {
                var path = $"{basePath}.identity[{i}]";
                if (identityNode[i] is not JsonObject item)
                {
                    errors.Add(new MappingValidationError(path, "must be an object"));
                    continue;
                }
                var name = item["name"]?.GetValue<string>();
                var jsonPath = item["path"]?.ToString();
                var field = item["field"]?.ToString();
                if (string.IsNullOrWhiteSpace(name)) errors.Add(new MappingValidationError(path, "name is required"));
                if (jsonPath is null && field is null) errors.Add(new MappingValidationError(path, "either path or field is required"));
                if (jsonPath is not null) ValidateJsonPath(jsonPath, $"{path}.path", errors);
                if (field is not null && !CanonicalFields.All.Contains(field)) errors.Add(new MappingValidationError($"{path}.field", $"unknown field '{field}'"));
                else if (field is not null && !fields.ContainsKey(field)) errors.Add(new MappingValidationError($"{path}.field", $"field '{field}' has no rule"));
                var transform = ReadTransforms(item["transform"], $"{path}.transform", errors);
                identity.Add(new IdentitySpec(name ?? "?", jsonPath, field, transform, ReadBool(item["optional"]), ReadBool(item["case_insensitive"])));
            }
            if (identity.Select(i => i.Name).Distinct(StringComparer.Ordinal).Count() != identity.Count)
            {
                errors.Add(new MappingValidationError($"{basePath}.identity", "component names must be unique"));
            }
        }
        else
        {
            errors.Add(new MappingValidationError($"{basePath}.identity", "identity must be a non-empty list"));
        }

        var lookupTables = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);
        if (doc["lookup_tables"] is JsonObject tables)
        {
            foreach (var (tableName, tableNode) in tables)
            {
                if (tableNode is not JsonObject rows)
                {
                    errors.Add(new MappingValidationError($"{basePath}.lookup_tables.{tableName}", "must be an object of key → value"));
                    continue;
                }
                lookupTables[tableName] = rows.ToDictionary(kv => kv.Key, kv => kv.Value?.ToString() ?? string.Empty, StringComparer.Ordinal);
            }
        }

        var enrich = new List<EnrichStep>();
        if (doc["enrich"] is JsonArray enrichNode)
        {
            for (var i = 0; i < enrichNode.Count; i++)
            {
                var path = $"{basePath}.enrich[{i}]";
                if (enrichNode[i] is not JsonObject step)
                {
                    errors.Add(new MappingValidationError(path, "must be an object"));
                    continue;
                }
                var set = step["set"]?.ToString();
                var table = step["lookup_table"]?.ToString();
                var key = step["key"]?.ToString();
                if (set is null || !CanonicalFields.All.Contains(set)) errors.Add(new MappingValidationError($"{path}.set", "must name a canonical field"));
                if (table is null || !lookupTables.ContainsKey(table)) errors.Add(new MappingValidationError($"{path}.lookup_table", "must name a table in lookup_tables"));
                if (key is null || !CanonicalFields.All.Contains(key)) errors.Add(new MappingValidationError($"{path}.key", "must name a canonical field"));
                enrich.Add(new EnrichStep(set ?? "?", table ?? "?", key ?? "?", ReadBool(step["when_empty"])));
            }
        }

        Predicate? appliesWhen = null;
        if (doc["applies_when"] is not null)
        {
            appliesWhen = PredicateParser.Parse(doc["applies_when"], $"{basePath}.applies_when", errors);
        }

        var hint = doc["lifecycle_profile_hint"]?.ToString();
        if (hint is not null && !LifecycleProfiles.Contains(hint))
        {
            errors.Add(new MappingValidationError($"{basePath}.lifecycle_profile_hint", $"unknown profile '{hint}'"));
        }

        var identityVersion = doc["identity_version"] is JsonValue iv && TryGetInt(iv, out var ivInt) ? ivInt : 1;
        if (identityVersion < 1) errors.Add(new MappingValidationError($"{basePath}.identity_version", "must be ≥ 1"));

        if (errors.Count > 0) throw new MappingValidationException(errors);

        return new MappingDocument
        {
            Version = versionOverride ?? (doc["version"] is JsonValue v && TryGetInt(v, out var vi) ? vi : 0),
            IdentityVersion = identityVersion,
            AppliesWhen = appliesWhen,
            IgnorePathsForBodyKey = ReadStringList(doc["ignore_paths_for_body_key"], $"{basePath}.ignore_paths_for_body_key", errors),
            Fields = fields,
            Required = required,
            Identity = identity,
            Enrich = enrich,
            LookupTables = lookupTables,
            LifecycleProfileHint = hint,
            Source = (JsonObject)doc.DeepClone(),
        };
    }

    private static MappingRule? ParseRule(JsonNode? node, string path, List<MappingValidationError> errors)
    {
        if (node is not JsonObject obj)
        {
            errors.Add(new MappingValidationError(path, "rule must be an object such as { path: \"$.x\" }"));
            return null;
        }
        foreach (var key in obj.Select(kv => kv.Key).Where(k => !KnownRuleKeys.Contains(k)))
        {
            errors.Add(new MappingValidationError($"{path}.{key}", "unknown rule key"));
        }

        var kinds = obj.Select(kv => kv.Key).Where(k => k is "path" or "const" or "first_nonempty" or "lookup" or "template" or "object" or "pick" or "array" or "header").ToList();
        if (kinds.Count != 1)
        {
            errors.Add(new MappingValidationError(path, kinds.Count == 0 ? "rule needs exactly one of path/const/first_nonempty/lookup/template/object/pick/array/header" : $"rule has several kinds: {string.Join(", ", kinds)}"));
            return null;
        }

        MappingRule? rule = kinds[0] switch
        {
            "path" => ParsePath(obj, path, errors),
            "const" => new ConstRule(obj["const"]?.DeepClone()),
            "first_nonempty" => ParseFirstNonEmpty(obj, path, errors),
            "lookup" => ParseLookup(obj, path, errors),
            "template" => ParseTemplate(obj, path, errors),
            "object" => ParseObject(obj, path, errors),
            "pick" => ParsePick(obj, path, errors),
            "array" => ParseArray(obj, path, errors),
            "header" => obj["header"]?.ToString() is { Length: > 0 } h ? new HeaderRule(h) : Fail(errors, $"{path}.header", "header name is required"),
            _ => null,
        };
        if (rule is null) return null;

        ValueType? valueType = null;
        if (obj["as"] is not null)
        {
            valueType = ParseValueType(obj["as"]!.ToString(), $"{path}.as", errors);
        }
        var transform = ReadTransforms(obj["transform"], $"{path}.transform", errors);
        int? maxLength = null;
        if (obj["max_length"] is JsonValue ml)
        {
            if (TryGetInt(ml, out var mlInt) && mlInt > 0) maxLength = mlInt;
            else errors.Add(new MappingValidationError($"{path}.max_length", "must be a positive integer"));
        }
        Predicate? when = null;
        if (obj["when"] is not null) when = PredicateParser.Parse(obj["when"], $"{path}.when", errors);

        return rule with
        {
            As = valueType,
            Transform = transform,
            Default = obj["default"]?.DeepClone(),
            HasDefault = obj.ContainsKey("default"),
            MaxLength = maxLength,
            When = when,
        };
    }

    private static MappingRule? ParsePath(JsonObject obj, string path, List<MappingValidationError> errors)
    {
        var p = obj["path"]?.ToString();
        if (string.IsNullOrWhiteSpace(p)) return Fail(errors, $"{path}.path", "JSONPath is required");
        ValidateJsonPath(p, $"{path}.path", errors);
        return new PathRule(p);
    }

    private static MappingRule? ParseFirstNonEmpty(JsonObject obj, string path, List<MappingValidationError> errors)
    {
        if (obj["first_nonempty"] is not JsonArray arr || arr.Count == 0) return Fail(errors, $"{path}.first_nonempty", "must be a non-empty list of rules");
        var candidates = new List<MappingRule>();
        for (var i = 0; i < arr.Count; i++)
        {
            var r = ParseRule(arr[i], $"{path}.first_nonempty[{i}]", errors);
            if (r is not null) candidates.Add(r);
        }
        return new FirstNonEmptyRule(candidates);
    }

    private static MappingRule? ParseLookup(JsonObject obj, string path, List<MappingValidationError> errors)
    {
        if (obj["lookup"] is not JsonObject l) return Fail(errors, $"{path}.lookup", "must be an object with from and map|regex_map");
        var from = l["from"]?.ToString();
        if (string.IsNullOrWhiteSpace(from)) return Fail(errors, $"{path}.lookup.from", "required");
        ValidateRef(from, $"{path}.lookup.from", errors);
        Dictionary<string, JsonNode?>? map = null;
        List<(Regex, JsonNode?)>? regexMap = null;
        if (l["map"] is JsonObject m)
        {
            map = m.ToDictionary(kv => kv.Key, kv => kv.Value?.DeepClone(), StringComparer.Ordinal);
        }
        if (l["regex_map"] is JsonObject rm)
        {
            regexMap = [];
            foreach (var (pattern, value) in rm)
            {
                try
                {
                    regexMap.Add((new Regex(Anchor(pattern), RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)), value?.DeepClone()));
                }
                catch (ArgumentException ex)
                {
                    errors.Add(new MappingValidationError($"{path}.lookup.regex_map", $"invalid regex '{pattern}': {ex.Message}"));
                }
            }
        }
        if (map is null && regexMap is null) return Fail(errors, $"{path}.lookup", "map or regex_map is required");
        foreach (var key in l.Select(kv => kv.Key).Where(k => k is not ("from" or "map" or "regex_map" or "default")))
        {
            errors.Add(new MappingValidationError($"{path}.lookup.{key}", "unknown key"));
        }
        return new LookupRule(from, map, regexMap, l["default"]?.DeepClone(), l.ContainsKey("default"));
    }

    private static MappingRule? ParseTemplate(JsonObject obj, string path, List<MappingValidationError> errors)
    {
        var t = obj["template"]?.ToString();
        if (t is null) return Fail(errors, $"{path}.template", "required");
        foreach (var placeholder in TemplateRenderer.Placeholders(t))
        {
            ValidateRef(placeholder, $"{path}.template", errors);
        }
        return new TemplateRule(t);
    }

    private static MappingRule? ParseObject(JsonObject obj, string path, List<MappingValidationError> errors)
    {
        if (obj["object"] is not JsonObject members) return Fail(errors, $"{path}.object", "must be an object of member → rule");
        var parsed = new Dictionary<string, MappingRule>(StringComparer.Ordinal);
        foreach (var (name, ruleNode) in members)
        {
            var r = ParseRule(ruleNode, $"{path}.object.{name}", errors);
            if (r is not null) parsed[name] = r;
        }
        return new ObjectRule(parsed);
    }

    private static MappingRule? ParsePick(JsonObject obj, string path, List<MappingValidationError> errors)
    {
        var paths = ReadStringList(obj["pick"], $"{path}.pick", errors);
        if (paths.Count == 0) return Fail(errors, $"{path}.pick", "must be a non-empty list of JSONPaths");
        foreach (var p in paths) ValidateJsonPath(p, $"{path}.pick", errors);
        return new PickRule(paths);
    }

    private static MappingRule? ParseArray(JsonObject obj, string path, List<MappingValidationError> errors)
    {
        if (obj["array"] is not JsonObject a) return Fail(errors, $"{path}.array", "must be an object with from and each");
        var from = a["from"]?.ToString();
        if (string.IsNullOrWhiteSpace(from)) return Fail(errors, $"{path}.array.from", "required");
        ValidateJsonPath(from, $"{path}.array.from", errors);
        var each = ParseRule(a["each"], $"{path}.array.each", errors);
        if (each is null) return null;
        return new ArrayRule(from, each, a["join"]?.ToString());
    }

    private static ValueType? ParseValueType(string value, string path, List<MappingValidationError> errors) => value switch
    {
        "string" => ValueType.String,
        "timestamp" => ValueType.Timestamp,
        "int" => ValueType.Int,
        "float" => ValueType.Float,
        "bool" => ValueType.Bool,
        "string[]" => ValueType.StringArray,
        "object" => ValueType.Object,
        _ => Fail<ValueType?>(errors, path, $"unknown type '{value}' (string|timestamp|int|float|bool|string[]|object)"),
    };

    private static IReadOnlyList<string> ReadTransforms(JsonNode? node, string path, List<MappingValidationError> errors)
    {
        var list = ReadStringList(node, path, errors);
        foreach (var t in list)
        {
            if (KnownTransforms.Contains(t)) continue;
            if (t.StartsWith("truncate:", StringComparison.Ordinal) && int.TryParse(t.AsSpan("truncate:".Length), out var n) && n > 0) continue;
            errors.Add(new MappingValidationError(path, $"unknown transform '{t}'"));
        }
        return list;
    }

    private static IReadOnlyList<string> ReadStringList(JsonNode? node, string path, List<MappingValidationError> errors)
    {
        switch (node)
        {
            case null: return [];
            case JsonArray arr:
                var list = new List<string>();
                foreach (var item in arr)
                {
                    if (item is JsonValue v && v.TryGetValue<string>(out var s)) list.Add(s);
                    else errors.Add(new MappingValidationError(path, "list items must be strings"));
                }
                return list;
            case JsonValue single when single.TryGetValue<string>(out var one): return [one];
            default:
                errors.Add(new MappingValidationError(path, "must be a list of strings"));
                return [];
        }
    }

    private static bool ReadBool(JsonNode? node) => node is JsonValue v && v.TryGetValue<bool>(out var b) && b;

    internal static bool TryGetInt(JsonValue value, out int result)
    {
        if (value.TryGetValue<int>(out result)) return true;
        if (value.TryGetValue<long>(out var l) && l is >= int.MinValue and <= int.MaxValue) { result = (int)l; return true; }
        if (value.TryGetValue<double>(out var d) && Math.Abs(d - Math.Round(d)) < double.Epsilon && d is >= int.MinValue and <= int.MaxValue) { result = (int)d; return true; }
        if (value.TryGetValue<string>(out var s) && int.TryParse(s, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out result)) return true;
        result = 0;
        return false;
    }

    /// <summary>Refs are either canonical field names or JSONPaths (07 §5). Header pseudo-root is accepted.</summary>
    internal static void ValidateRef(string reference, string path, List<MappingValidationError> errors)
    {
        if (reference.StartsWith('$')) ValidateJsonPath(reference, path, errors);
        else if (!IsFieldRef(reference)) errors.Add(new MappingValidationError(path, $"unknown reference '{reference}'"));
    }

    internal static bool IsFieldRef(string reference)
    {
        if (CanonicalFields.All.Contains(reference)) return true;
        if (reference.StartsWith("labels.", StringComparison.Ordinal) || reference.StartsWith("dimensions.", StringComparison.Ordinal)) return true;
        return reference is "integration.type" or "integration.id" or "integration.name" or "access_scope" or "condition_state" or "handling_state" or "owning_team_id" or "is_actionable";
    }

    internal static void ValidateJsonPath(string path, string errorPath, List<MappingValidationError> errors)
    {
        var effective = JsonPathEvaluator.NormalisePath(path);
        if (!JsonPath.TryParse(effective, out _, JsonPathEvaluator.ParsingOptions))
        {
            errors.Add(new MappingValidationError(errorPath, $"invalid JSONPath '{path}'"));
        }
    }

    private static string Anchor(string pattern)
    {
        var p = pattern.StartsWith('^') ? pattern : "^" + pattern;
        return p.EndsWith('$') ? p : p + "$";
    }

    private static MappingRule? Fail(List<MappingValidationError> errors, string path, string message)
    {
        errors.Add(new MappingValidationError(path, message));
        return null;
    }

    private static T? Fail<T>(List<MappingValidationError> errors, string path, string message)
    {
        errors.Add(new MappingValidationError(path, message));
        return default;
    }
}

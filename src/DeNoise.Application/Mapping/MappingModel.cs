using System.Text.Json.Nodes;

namespace DeNoise.Application.Mapping;

/// <summary>Parsed, validated mapping document (07 §1). Immutable once created; one instance per stored version.</summary>
public sealed class MappingDocument
{
    public int Version { get; init; }
    public int IdentityVersion { get; init; } = 1;
    public Predicate? AppliesWhen { get; init; }
    public IReadOnlyList<string> IgnorePathsForBodyKey { get; init; } = [];
    public required IReadOnlyDictionary<string, MappingRule> Fields { get; init; }
    public IReadOnlyList<string> Required { get; init; } = [];
    public required IReadOnlyList<IdentitySpec> Identity { get; init; }
    public IReadOnlyList<EnrichStep> Enrich { get; init; } = [];
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> LookupTables { get; init; } = new Dictionary<string, IReadOnlyDictionary<string, string>>();
    public string? LifecycleProfileHint { get; init; }
    /// <summary>The document as JSON, for storage in <c>cfg.mapping.body</c>.</summary>
    public required JsonObject Source { get; init; }
}

/// <summary>One identity component (04 §4): value from a JSONPath or from an already-mapped canonical field.</summary>
public sealed record IdentitySpec(string Name, string? Path, string? Field, IReadOnlyList<string> Transform, bool Optional, bool CaseInsensitive);

/// <summary>Post-mapping enrichment from a local lookup table (07 §1 <c>enrich:</c>).</summary>
public sealed record EnrichStep(string Set, string LookupTable, string Key, bool WhenEmpty);

/// <summary>Target value type (07 §1 <c>as:</c>).</summary>
public enum ValueType
{
    String,
    Timestamp,
    Int,
    Float,
    Bool,
    StringArray,
    Object,
}

/// <summary>A field rule. Subclasses per rule kind; common modifiers here.</summary>
public abstract record MappingRule
{
    public ValueType? As { get; init; }
    public IReadOnlyList<string> Transform { get; init; } = [];
    public JsonNode? Default { get; init; }
    public bool HasDefault { get; init; }
    public int? MaxLength { get; init; }
    public Predicate? When { get; init; }
}

public sealed record PathRule(string Path) : MappingRule;
public sealed record ConstRule(JsonNode? Value) : MappingRule;
public sealed record FirstNonEmptyRule(IReadOnlyList<MappingRule> Candidates) : MappingRule;
public sealed record LookupRule(string From, IReadOnlyDictionary<string, JsonNode?>? Map, IReadOnlyList<(System.Text.RegularExpressions.Regex Pattern, JsonNode? Value)>? RegexMap, JsonNode? LookupDefault, bool HasLookupDefault) : MappingRule;
public sealed record TemplateRule(string Template) : MappingRule;
public sealed record ObjectRule(IReadOnlyDictionary<string, MappingRule> Members) : MappingRule;
public sealed record PickRule(IReadOnlyList<string> Paths) : MappingRule;
public sealed record ArrayRule(string From, MappingRule Each, string? Join) : MappingRule;
public sealed record HeaderRule(string Header) : MappingRule;

/// <summary>Predicate grammar (07 §5), shared by mappings, routing, grouping and suppression.</summary>
public abstract record Predicate
{
    public sealed record Eq(string Ref, JsonNode? Value) : Predicate;
    public sealed record Neq(string Ref, JsonNode? Value) : Predicate;
    public sealed record In(string Ref, IReadOnlyList<JsonNode?> Values) : Predicate;
    public sealed record Nin(string Ref, IReadOnlyList<JsonNode?> Values) : Predicate;
    public sealed record Regex(string Ref, System.Text.RegularExpressions.Regex Pattern) : Predicate;
    public sealed record Exists(string Ref) : Predicate;
    public sealed record Gte(string Ref, JsonNode? Value) : Predicate;
    public sealed record Lte(string Ref, JsonNode? Value) : Predicate;
    public sealed record All(IReadOnlyList<Predicate> Items) : Predicate;
    public sealed record Any(IReadOnlyList<Predicate> Items) : Predicate;
    public sealed record Not(Predicate Item) : Predicate;
}

public sealed record MappingValidationError(string Path, string Message)
{
    public override string ToString() => $"{Path}: {Message}";
}

/// <summary>Raised on save/parse; carries every error with its path so the editor can point at them (07 §1 validation).</summary>
public sealed class MappingValidationException(IReadOnlyList<MappingValidationError> errors)
    : Exception($"mapping invalid: {string.Join("; ", errors.Select(e => e.ToString()))}")
{
    public IReadOnlyList<MappingValidationError> Errors { get; } = errors;
}

/// <summary>Raised while interpreting one event: the event is quarantined in <c>alert.mapping_failure</c>.</summary>
public sealed class MappingException(string message, string? field = null, Exception? inner = null) : Exception(message, inner)
{
    public string? Field { get; } = field;
}

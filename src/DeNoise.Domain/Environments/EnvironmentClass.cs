using System.Text.RegularExpressions;

namespace DeNoise.Domain.Environments;

/// <summary>
/// One configured environment class (<c>cfg.environment</c>, spec §15.5, ADR-17). The dictionary is the set of values the canonical
/// <c>environment</c> field may take: mappings are validated against it on save, the queue offers its entries as filter chips, and a
/// value outside it is treated as unclassified. It names a class (production, staging…), never one product's instance of it —
/// the product is the integration's <c>access_scope</c> (spec §19.2).
/// </summary>
public sealed class EnvironmentClass
{
    public required string Key { get; init; }
    public required string Label { get; set; }
    public int SortOrder { get; set; }
    /// <summary>Built-in entries (<c>unknown</c>) cannot be deleted: a mapping that cannot derive an environment yields it (spec §15.5 rule 1).</summary>
    public bool IsBuiltIn { get; init; }
    public int Version { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public static partial class EnvironmentClasses
{
    /// <summary>The value a mapping yields when it cannot derive an environment; always in the dictionary and in the default view.</summary>
    public const string Unknown = "unknown";

    public const string Production = "production";

    /// <summary>
    /// Keys the <c>EnvironmentDictionary</c> migration seeds (spec §15.5 classes, plus <c>ci</c> and <c>ephemeral</c> that the pipeline
    /// reference mappings produce). Tests hold every reference mapping to this list; the migration itself is frozen SQL.
    /// </summary>
    public static readonly IReadOnlyList<string> Seeded = [Production, "staging", "development", "ci", "ephemeral", Unknown];

    /// <summary>Spec §15.5 rule 4: entry points open the queue on production plus unclassified. Fixed by the spec, not configurable.</summary>
    public static readonly IReadOnlyList<string> DefaultView = [Production, Unknown];

    public const int MaxKeyLength = 32;
    public const int MaxLabelLength = 64;

    /// <summary>Keys are lower-case so that the generic mapping's <c>transform: [lower, trim]</c> lands on them.</summary>
    public static bool IsValidKey(string? key) => key is not null && KeyPattern().IsMatch(key);

    [GeneratedRegex("^[a-z][a-z0-9_-]{0,31}$", RegexOptions.CultureInvariant)]
    private static partial Regex KeyPattern();
}

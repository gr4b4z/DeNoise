using System.Text.Json.Nodes;

namespace DeNoise.Application.Mapping;

/// <summary>Stored mapping version (<c>cfg.mapping</c>, 05 §6). Immutable; exactly one version per mapping id is active.</summary>
public sealed class MappingVersion
{
    public Guid MappingId { get; init; }
    public int Version { get; init; }
    public Guid IntegrationId { get; init; }
    /// <summary>Evaluation order among the integration's mappings (lower first); <c>applies_when</c> decides within that order.</summary>
    public int Order { get; set; }
    public string? Name { get; set; }
    public required string Body { get; init; }
    public string? SourceYaml { get; init; }
    public int IdentityVersion { get; init; }
    /// <summary>JSON array of <c>{ name?, body, headers?, expected }</c>; every sample must pass before activation (07 §1).</summary>
    public string Samples { get; init; } = "[]";
    public DateTimeOffset? ActivatedAt { get; set; }
    public DateTimeOffset? DeactivatedAt { get; set; }
    public string? CreatedBy { get; init; }
    public DateTimeOffset CreatedAt { get; init; }

    public bool IsActive => ActivatedAt is not null && DeactivatedAt is null;
}

public interface IMappingRepository
{
    Task<IReadOnlyList<MappingVersion>> ListAsync(Guid integrationId, CancellationToken ct = default);
    Task<MappingVersion?> GetAsync(Guid mappingId, int version, CancellationToken ct = default);
    Task<MappingVersion?> GetActiveAsync(Guid mappingId, CancellationToken ct = default);
    Task<int> NextVersionAsync(Guid mappingId, CancellationToken ct = default);
    void Add(MappingVersion version);
}

/// <summary>A stored sample and the values it must produce.</summary>
public sealed record MappingSample(string? Name, JsonNode Body, IReadOnlyDictionary<string, string>? Headers, IReadOnlyDictionary<string, string> Expected);

namespace DeNoise.Domain.Integrations;

/// <summary>
/// One immutable version of an integration's configuration (<c>cfg.integration</c>, 05 §6, ADR-6).
/// Exactly one version per <see cref="IntegrationId"/> has <see cref="DeactivatedAt"/> = null (★ partial unique index).
/// Ingest credentials live on the version so rotation is itself an audited configuration change.
/// </summary>
public sealed class Integration
{
    public Guid IntegrationId { get; init; }
    public int Version { get; init; } = 1;
    public required string Name { get; set; }
    /// <summary>See <see cref="IntegrationTypes"/>.</summary>
    public required string Type { get; init; }
    /// <summary>Server-assigned visibility boundary (spec §19.2); never taken from payloads.</summary>
    public required string AccessScope { get; set; }
    /// <summary>Owning team; nullable until teams exist (milestone 3), then routing falls back to triage when null.</summary>
    public Guid? OwnerTeamId { get; set; }
    /// <summary>Non-secret 12-character prefix used in <c>/ingest/{key}</c> for a single-row lookup (ADR-11).</summary>
    public required string IngestKeyId { get; init; }
    /// <summary>Argon2id hash of the ingest token; plaintext is shown exactly once.</summary>
    public required string IngestTokenHash { get; init; }
    public string? HmacSecretEnc { get; set; }
    /// <summary>JSON: <c>{ "algorithm": "sha1|sha256", "header": "X-MMS-Signature", "required": bool }</c> (07 §3).</summary>
    public string? HmacConfig { get; set; }
    public string[]? IpAllowList { get; set; }
    /// <summary>JSON capability profile (spec §10.1), e.g. <c>retransmission_indistinguishable</c>, <c>state_query</c>.</summary>
    public string Capabilities { get; set; } = "{}";
    /// <summary>JSON coverage configuration (spec §13.5).</summary>
    public string Coverage { get; set; } = "{}";
    /// <summary>JSON lifecycle profile defaults (spec §12.2).</summary>
    public string ProfileDefaults { get; set; } = "{}";
    /// <summary>Inactive integrations reject ingestion with 401 but keep their history.</summary>
    public bool Active { get; set; } = true;
    /// <summary>Per-integration shadow mode (milestone 12): ingest and process, but never notify.</summary>
    public bool Shadow { get; set; }
    public DateTimeOffset ActivatedAt { get; init; }
    public DateTimeOffset? DeactivatedAt { get; set; }
    public string? CreatedBy { get; init; }
    public string? SourceYaml { get; init; }
    public DateTimeOffset CreatedAt { get; init; }

    public bool IsCurrent => DeactivatedAt is null;

    /// <summary>Copies this version into the next one; the caller mutates the copy and deactivates this row in the same transaction.</summary>
    public Integration NextVersion(DateTimeOffset now, string? createdBy, string? ingestKeyId = null, string? ingestTokenHash = null) => new()
    {
        IntegrationId = IntegrationId,
        Version = Version + 1,
        Name = Name,
        Type = Type,
        AccessScope = AccessScope,
        OwnerTeamId = OwnerTeamId,
        IngestKeyId = ingestKeyId ?? IngestKeyId,
        IngestTokenHash = ingestTokenHash ?? IngestTokenHash,
        HmacSecretEnc = HmacSecretEnc,
        HmacConfig = HmacConfig,
        IpAllowList = IpAllowList,
        Capabilities = Capabilities,
        Coverage = Coverage,
        ProfileDefaults = ProfileDefaults,
        Active = Active,
        Shadow = Shadow,
        ActivatedAt = now,
        CreatedBy = createdBy,
        SourceYaml = SourceYaml,
        CreatedAt = now,
    };
}

public static class IntegrationTypes
{
    public const string AzureMonitor = "azure_monitor";
    public const string Atlas = "atlas";
    public const string GenericWebhook = "generic_webhook";
    public static readonly IReadOnlyList<string> All = [AzureMonitor, Atlas, GenericWebhook];
}

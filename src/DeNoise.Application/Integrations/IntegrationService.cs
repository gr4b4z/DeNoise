using System.Text.Json;
using DeNoise.Application.Abstractions;
using DeNoise.Application.Audit;
using DeNoise.Domain.Audit;
using DeNoise.Domain.Common;
using DeNoise.Domain.Integrations;

namespace DeNoise.Application.Integrations;

public sealed record CreateIntegration(string Name, string Type, string AccessScope, Guid? OwnerTeamId = null,
    string? Capabilities = null, string? Coverage = null, string? ProfileDefaults = null, string? SourceYaml = null,
    string? HmacSecret = null, Ingest.HmacConfig? Hmac = null);

/// <summary>
/// Configuration change: every non-null member replaces the current value in a new configuration version (05 §2 — versions,
/// never in-place edits). <see cref="HmacSecret"/> is stored encrypted; <see cref="ClearHmac"/> removes secret and config.
/// </summary>
public sealed record UpdateIntegration(string? Name = null, string? AccessScope = null, Guid? OwnerTeamId = null, bool ClearOwnerTeam = false,
    string? Capabilities = null, string? Coverage = null, string? ProfileDefaults = null, string[]? IpAllowList = null,
    string? HmacSecret = null, Ingest.HmacConfig? Hmac = null, bool ClearHmac = false, bool? Active = null, bool? Shadow = null);

/// <summary>Result of creating or rotating credentials: the secret is returned exactly once (AGENTS.md rule 6).</summary>
public sealed record IntegrationCredentials(Integration Integration, string IngestKeyId, string IngestToken)
{
    /// <summary>Path a producer posts to; the token goes in the <c>Authorization: Bearer</c> header (06 §2).</summary>
    public string IngestPath => $"/ingest/{IngestKeyId}";
}

public sealed class IntegrationService(
    IIntegrationRepository integrations, ISecretHasher hasher, IAuditWriter audit, IUnitOfWork uow, TimeProvider time, Notifications.ISecretProtector protector)
{
    public async Task<IntegrationCredentials> CreateAsync(CreateIntegration request, Actor actor, CancellationToken ct = default)
    {
        if (!IntegrationTypes.All.Contains(request.Type))
        {
            throw new ArgumentException($"Unknown integration type '{request.Type}'.", nameof(request));
        }
        if (string.IsNullOrWhiteSpace(request.AccessScope))
        {
            throw new ArgumentException("access_scope is required.", nameof(request));
        }

        var now = time.GetUtcNow();
        var keyId = TokenGenerator.NewKeyId();
        var secret = TokenGenerator.NewSecret();
        var integration = new Integration
        {
            IntegrationId = Ids.New(time),
            Version = 1,
            Name = request.Name.Trim(),
            Type = request.Type,
            AccessScope = request.AccessScope.Trim(),
            OwnerTeamId = request.OwnerTeamId,
            IngestKeyId = keyId,
            IngestTokenHash = hasher.HashToken(secret),
            Capabilities = request.Capabilities ?? "{}",
            Coverage = request.Coverage ?? "{}",
            ProfileDefaults = request.ProfileDefaults ?? "{}",
            SourceYaml = request.SourceYaml,
            ActivatedAt = now,
            CreatedAt = now,
            CreatedBy = actor.Id,
        };
        if (request.HmacSecret is { Length: > 0 } hmacSecret)
        {
            var hmac = request.Hmac ?? new Ingest.HmacConfig();
            var errors = hmac.Validate();
            if (errors.Count > 0) throw new Mapping.MappingValidationException(errors);
            integration.HmacSecretEnc = protector.Protect(hmacSecret);
            integration.HmacConfig = hmac.ToJson();
        }
        ValidateDocuments(integration);
        integrations.Add(integration);
        audit.Record(new AuditEntry
        {
            Id = Ids.New(time),
            At = now,
            ActorType = actor.Type,
            ActorId = actor.Id,
            ActorDisplay = actor.Display,
            Action = "integration.create",
            TargetType = "integration",
            TargetId = integration.IntegrationId.ToString(),
            AccessScope = integration.AccessScope,
            After = Describe(integration),
            CorrelationId = actor.CorrelationId,
            RequestIp = actor.Ip,
        });
        await uow.CommitAsync(ct);
        return new IntegrationCredentials(integration, keyId, secret);
    }

    /// <summary>Rotates the ingest token by creating a new configuration version; the old token stops working at commit.</summary>
    public async Task<IntegrationCredentials> RotateIngestTokenAsync(Guid integrationId, Actor actor, CancellationToken ct = default)
    {
        var current = await integrations.GetCurrentAsync(integrationId, ct)
            ?? throw new KeyNotFoundException($"Integration {integrationId} not found.");
        var now = time.GetUtcNow();
        var keyId = TokenGenerator.NewKeyId();
        var secret = TokenGenerator.NewSecret();
        var next = current.NextVersion(now, actor.Id, keyId, hasher.HashToken(secret));
        current.DeactivatedAt = now;
        integrations.Add(next);
        audit.Record(new AuditEntry
        {
            Id = Ids.New(time),
            At = now,
            ActorType = actor.Type,
            ActorId = actor.Id,
            ActorDisplay = actor.Display,
            Action = "integration.rotate_ingest_token",
            TargetType = "integration",
            TargetId = integrationId.ToString(),
            AccessScope = next.AccessScope,
            Before = JsonSerializer.Serialize(new { version = current.Version, ingestKeyId = current.IngestKeyId }),
            After = JsonSerializer.Serialize(new { version = next.Version, ingestKeyId = next.IngestKeyId }),
            CorrelationId = actor.CorrelationId,
            RequestIp = actor.Ip,
        });
        await uow.CommitAsync(ct);
        return new IntegrationCredentials(next, keyId, secret);
    }

    /// <summary>Creates configuration version n+1 with the requested changes; <paramref name="expectedVersion"/> is the caller's <c>If-Match</c>.</summary>
    public async Task<Integration> UpdateAsync(Guid integrationId, int? expectedVersion, UpdateIntegration request, Actor actor, CancellationToken ct = default)
    {
        var current = await integrations.GetCurrentAsync(integrationId, ct)
            ?? throw new KeyNotFoundException($"Integration {integrationId} not found.");
        if (expectedVersion is { } expected && expected != current.Version) throw new Episodes.VersionConflictException(integrationId, current.Version);
        var now = time.GetUtcNow();
        var next = current.NextVersion(now, actor.Id);
        if (request.Name is { } name && !string.IsNullOrWhiteSpace(name)) next.Name = name.Trim();
        if (request.AccessScope is { } scope && !string.IsNullOrWhiteSpace(scope)) next.AccessScope = scope.Trim();
        if (request.ClearOwnerTeam) next.OwnerTeamId = null;
        else if (request.OwnerTeamId is { } team) next.OwnerTeamId = team;
        if (request.Capabilities is not null) next.Capabilities = request.Capabilities;
        if (request.Coverage is not null) next.Coverage = request.Coverage;
        if (request.ProfileDefaults is not null) next.ProfileDefaults = request.ProfileDefaults;
        if (request.IpAllowList is not null) next.IpAllowList = request.IpAllowList.Length == 0 ? null : request.IpAllowList;
        if (request.ClearHmac)
        {
            next.HmacSecretEnc = null;
            next.HmacConfig = null;
        }
        else
        {
            if (request.HmacSecret is { Length: > 0 } secret) next.HmacSecretEnc = protector.Protect(secret);
            if (request.Hmac is { } hmac)
            {
                var errors = hmac.Validate().ToList();
                if (next.HmacSecretEnc is null && hmac.Required) errors.Add(new Mapping.MappingValidationError("$.hmac.required", "cannot require a signature without a secret"));
                if (errors.Count > 0) throw new Mapping.MappingValidationException(errors);
                next.HmacConfig = hmac.ToJson();
            }
        }
        if (request.Active is { } active) next.Active = active;
        if (request.Shadow is { } shadow) next.Shadow = shadow;
        ValidateDocuments(next);
        current.DeactivatedAt = now;
        integrations.Add(next);
        audit.Record(new AuditEntry
        {
            Id = Ids.New(time),
            At = now,
            ActorType = actor.Type,
            ActorId = actor.Id,
            ActorDisplay = actor.Display,
            Action = "integration.update",
            TargetType = "integration",
            TargetId = integrationId.ToString(),
            AccessScope = next.AccessScope,
            Before = Describe(current),
            After = Describe(next),
            CorrelationId = actor.CorrelationId,
            RequestIp = actor.Ip,
        });
        await uow.CommitAsync(ct);
        return next;
    }

    /// <summary>Coverage, profile defaults and capabilities are JSON documents with a schema: refuse what the workers could not read.</summary>
    private static void ValidateDocuments(Integration i)
    {
        var errors = new List<Mapping.MappingValidationError>();
        foreach (var (name, json) in new[] { ("capabilities", i.Capabilities), ("coverage", i.Coverage), ("profile_defaults", i.ProfileDefaults) })
        {
            try
            {
                if (System.Text.Json.Nodes.JsonNode.Parse(json) is not System.Text.Json.Nodes.JsonObject) errors.Add(new Mapping.MappingValidationError($"$.{name}", "must be a JSON object"));
            }
            catch (JsonException ex)
            {
                errors.Add(new Mapping.MappingValidationError($"$.{name}", $"invalid JSON: {ex.Message}"));
            }
        }
        if (errors.Count == 0)
        {
            try
            {
                _ = Coverage.CoverageConfig.Parse(i.Coverage);
            }
            catch (Mapping.MappingValidationException ex)
            {
                errors.AddRange(ex.Errors.Select(e => new Mapping.MappingValidationError("$.coverage" + e.Path.TrimStart('$'), e.Message)));
            }
        }
        if (errors.Count > 0) throw new Mapping.MappingValidationException(errors);
    }

    public async Task SetActiveAsync(Guid integrationId, bool active, Actor actor, string? reason, CancellationToken ct = default)
    {
        var current = await integrations.GetCurrentAsync(integrationId, ct)
            ?? throw new KeyNotFoundException($"Integration {integrationId} not found.");
        if (current.Active == active) return;
        var now = time.GetUtcNow();
        var next = current.NextVersion(now, actor.Id);
        next.Active = active;
        current.DeactivatedAt = now;
        integrations.Add(next);
        audit.Record(new AuditEntry
        {
            Id = Ids.New(time),
            At = now,
            ActorType = actor.Type,
            ActorId = actor.Id,
            ActorDisplay = actor.Display,
            Action = active ? "integration.enable" : "integration.disable",
            TargetType = "integration",
            TargetId = integrationId.ToString(),
            AccessScope = next.AccessScope,
            Before = JsonSerializer.Serialize(new { active = current.Active }),
            After = JsonSerializer.Serialize(new { active }),
            Reason = reason,
            CorrelationId = actor.CorrelationId,
            RequestIp = actor.Ip,
        });
        await uow.CommitAsync(ct);
    }

    // Never includes credential material.
    private static string Describe(Integration i) => JsonSerializer.Serialize(new
    {
        version = i.Version,
        name = i.Name,
        type = i.Type,
        accessScope = i.AccessScope,
        ownerTeamId = i.OwnerTeamId,
        ingestKeyId = i.IngestKeyId,
        active = i.Active,
        shadow = i.Shadow,
        hmacConfigured = i.HmacSecretEnc is not null,
        hmac = i.HmacConfig,
        capabilities = i.Capabilities,
        coverage = i.Coverage,
        profileDefaults = i.ProfileDefaults,
    });
}

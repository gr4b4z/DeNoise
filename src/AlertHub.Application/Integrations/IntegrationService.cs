using System.Text.Json;
using AlertHub.Application.Abstractions;
using AlertHub.Application.Audit;
using AlertHub.Domain.Audit;
using AlertHub.Domain.Common;
using AlertHub.Domain.Integrations;

namespace AlertHub.Application.Integrations;

public sealed record CreateIntegration(string Name, string Type, string AccessScope, Guid? OwnerTeamId = null,
    string? Capabilities = null, string? Coverage = null, string? ProfileDefaults = null, string? SourceYaml = null);

/// <summary>Result of creating or rotating credentials: the secret is returned exactly once (AGENTS.md rule 6).</summary>
public sealed record IntegrationCredentials(Integration Integration, string IngestKeyId, string IngestToken)
{
    /// <summary>Path a producer posts to; the token goes in the <c>Authorization: Bearer</c> header (06 §2).</summary>
    public string IngestPath => $"/ingest/{IngestKeyId}";
}

public sealed class IntegrationService(
    IIntegrationRepository integrations, ISecretHasher hasher, IAuditWriter audit, IUnitOfWork uow, TimeProvider time)
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
    });
}

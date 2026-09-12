using System.Text.Json;
using System.Text.Json.Nodes;
using AlertHub.Application.Abstractions;
using AlertHub.Application.Audit;
using AlertHub.Application.Mapping;
using AlertHub.Application.Routing;
using AlertHub.Domain.Audit;
using AlertHub.Domain.Common;
using AlertHub.Domain.Policies;

namespace AlertHub.Application.Policies;

public interface IPolicyRepository
{
    Task<PolicyVersion?> GetAsync(string kind, Guid policyId, int version, CancellationToken ct = default);
    Task<PolicyVersion?> GetActiveAsync(string kind, Guid policyId, CancellationToken ct = default);
    /// <summary>All active versions of a kind (routing has one document; escalation/lifecycle have many).</summary>
    Task<IReadOnlyList<PolicyVersion>> ListActiveAsync(string kind, CancellationToken ct = default);
    Task<IReadOnlyList<PolicyVersion>> ListVersionsAsync(string kind, Guid policyId, CancellationToken ct = default);
    Task<int> NextVersionAsync(string kind, Guid policyId, CancellationToken ct = default);
    void Add(PolicyVersion version);
}

/// <summary>Validates a policy body for its kind; each milestone adds its kind here.</summary>
public interface IPolicyValidator
{
    string Kind { get; }
    /// <summary>Throws <see cref="MappingValidationException"/> when invalid.</summary>
    void Validate(JsonNode body, int version);
}

public sealed class RoutingPolicyValidator : IPolicyValidator
{
    public string Kind => PolicyKinds.Routing;
    public void Validate(JsonNode body, int version) => RoutingPolicyDocument.Parse(body, version);
}

public sealed class EscalationPolicyValidator : IPolicyValidator
{
    public string Kind => PolicyKinds.Escalation;
    public void Validate(JsonNode body, int version) => EscalationPolicyDocument.Parse(body, version);
}

/// <summary>Well-known ids so a single routing document can be addressed without lookup.</summary>
public static class WellKnownPolicies
{
    /// <summary>The one routing rule set; routing has exactly one document (04 §7.1 evaluates one ordered list).</summary>
    public static readonly Guid Routing = Guid.Parse("00000000-0000-0000-0000-00000000c0de");
    /// <summary>The one grouping rule set (04 §7.4 evaluates one ordered list).</summary>
    public static readonly Guid Grouping = Guid.Parse("00000000-0000-0000-0000-00000000c0df");
}

public sealed record CreatePolicyVersion(string Kind, string Yaml, Guid? PolicyId = null, string? Name = null);

/// <summary>Create/activate/rollback for every policy kind (ADR-6, 06 §4 "Policies").</summary>
public sealed class PolicyService(IPolicyRepository repository, IEnumerable<IPolicyValidator> validators, IEnumerable<IPolicyActivationHook> activationHooks, IAuditWriter audit, IUnitOfWork uow, TimeProvider time)
{
    public async Task<PolicyVersion> CreateVersionAsync(CreatePolicyVersion request, Actor actor, CancellationToken ct = default)
    {
        if (!PolicyKinds.All.Contains(request.Kind)) throw new ArgumentException($"Unknown policy kind '{request.Kind}'.", nameof(request));
        var validator = validators.FirstOrDefault(v => v.Kind == request.Kind)
            ?? throw new NotSupportedException($"Policies of kind '{request.Kind}' are not supported yet.");
        var policyId = request.PolicyId ?? (request.Kind == PolicyKinds.Routing ? WellKnownPolicies.Routing : request.Kind == PolicyKinds.Grouping ? WellKnownPolicies.Grouping : Ids.New(time));
        var version = await repository.NextVersionAsync(request.Kind, policyId, ct);
        var body = YamlJson.Parse(request.Yaml) ?? throw new MappingValidationException([new MappingValidationError("$", "document is empty")]);
        validator.Validate(body, version);

        var now = time.GetUtcNow();
        var stored = new PolicyVersion
        {
            PolicyId = policyId,
            Kind = request.Kind,
            Version = version,
            Name = request.Name,
            Body = body.ToJsonString(),
            SourceYaml = request.Yaml,
            CreatedBy = actor.Id,
            CreatedAt = now,
        };
        repository.Add(stored);
        audit.Record(new AuditEntry
        {
            Id = Ids.New(time),
            At = now,
            ActorType = actor.Type,
            ActorId = actor.Id,
            ActorDisplay = actor.Display,
            Action = $"policy.{request.Kind}.create_version",
            TargetType = "policy",
            TargetId = policyId.ToString(),
            After = JsonSerializer.Serialize(new { version }, JsonDefaults.Stored),
            CorrelationId = actor.CorrelationId,
            RequestIp = actor.Ip,
        });
        await uow.CommitAsync(ct);
        return stored;
    }

    public async Task ActivateAsync(string kind, Guid policyId, int version, Actor actor, CancellationToken ct = default)
    {
        var target = await repository.GetAsync(kind, policyId, version, ct) ?? throw new KeyNotFoundException($"{kind} policy {policyId} v{version} not found.");
        if (target.IsActive) return;
        var validator = validators.FirstOrDefault(v => v.Kind == kind) ?? throw new NotSupportedException($"Policies of kind '{kind}' are not supported yet.");
        validator.Validate(JsonNode.Parse(target.Body)!, version);

        var now = time.GetUtcNow();
        var current = await repository.GetActiveAsync(kind, policyId, ct);
        if (current is not null) current.DeactivatedAt = now;
        target.ActivatedAt = now;
        target.DeactivatedAt = null;
        audit.Record(new AuditEntry
        {
            Id = Ids.New(time),
            At = now,
            ActorType = actor.Type,
            ActorId = actor.Id,
            ActorDisplay = actor.Display,
            Action = $"policy.{kind}.activate",
            TargetType = "policy",
            TargetId = policyId.ToString(),
            Before = JsonSerializer.Serialize(new { version = current?.Version }, JsonDefaults.Stored),
            After = JsonSerializer.Serialize(new { version }, JsonDefaults.Stored),
            CorrelationId = actor.CorrelationId,
            RequestIp = actor.Ip,
        });
        await uow.CommitAsync(ct);
        foreach (var hook in activationHooks) await hook.AfterActivatedAsync(kind, policyId, version, actor, ct);
    }

    /// <summary>Rollback = activate an earlier version (ADR-6: exactly one activation path).</summary>
    public Task RollbackAsync(string kind, Guid policyId, int toVersion, Actor actor, CancellationToken ct = default)
        => ActivateAsync(kind, policyId, toVersion, actor, ct);
}

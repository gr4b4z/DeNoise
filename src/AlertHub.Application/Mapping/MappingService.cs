using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AlertHub.Application.Abstractions;
using AlertHub.Application.Audit;
using AlertHub.Application.Integrations;
using AlertHub.Application.Processing;
using AlertHub.Domain.Audit;
using AlertHub.Domain.Common;

using AlertHub.Domain.Integrations;

namespace AlertHub.Application.Mapping;

public sealed record CreateMappingVersion(Guid IntegrationId, string Yaml, Guid? MappingId = null, string? Name = null, int Order = 100, IReadOnlyList<MappingSample>? Samples = null);

/// <summary>Mapping versions: create (not active), verify samples, activate (ADR-6: DB is truth, YAML is import/export).</summary>
public sealed class MappingService(IMappingRepository repository, IIntegrationRepository integrations, IMappingResolver resolver, IAuditWriter audit, IUnitOfWork uow, TimeProvider time)
{
    public async Task<MappingVersion> CreateVersionAsync(CreateMappingVersion request, Actor actor, CancellationToken ct = default)
    {
        var integration = await integrations.GetCurrentAsync(request.IntegrationId, ct)
            ?? throw new KeyNotFoundException($"Integration {request.IntegrationId} not found.");
        var mappingId = request.MappingId ?? Ids.New(time);
        var version = await repository.NextVersionAsync(mappingId, ct);
        var doc = MappingParser.ParseYaml(request.Yaml, version);
        var samples = request.Samples ?? [];
        var failures = VerifySamples(doc, samples, integration.IntegrationId);
        if (failures.Count > 0)
        {
            throw new MappingValidationException(failures);
        }

        var now = time.GetUtcNow();
        var stored = new MappingVersion
        {
            MappingId = mappingId,
            Version = version,
            IntegrationId = integration.IntegrationId,
            Order = request.Order,
            Name = request.Name,
            Body = doc.Source.ToJsonString(),
            SourceYaml = request.Yaml,
            IdentityVersion = doc.IdentityVersion,
            Samples = JsonSerializer.Serialize(samples.Select(s => new { name = s.Name, body = s.Body, headers = s.Headers, expected = s.Expected }), JsonDefaults.Stored),
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
            Action = "mapping.create_version",
            TargetType = "mapping",
            TargetId = mappingId.ToString(),
            AccessScope = integration.AccessScope,
            After = JsonSerializer.Serialize(new { version, integrationId = integration.IntegrationId, identityVersion = doc.IdentityVersion }, JsonDefaults.Stored),
            CorrelationId = actor.CorrelationId,
            RequestIp = actor.Ip,
        });
        await uow.CommitAsync(ct);
        return stored;
    }

    /// <summary>
    /// Seeds the reference mappings of the integration's type (07 §2–3) as active version 1 each, once: an Azure Monitor or Atlas
    /// integration interprets events before anyone edits a mapping. Returns how many mappings were created.
    /// </summary>
    public async Task<int> SeedReferenceAsync(Integration integration, Actor actor, CancellationToken ct = default)
    {
        var references = ReferenceMappings.For(integration.Type);
        if (references.Count == 0) return 0;
        if ((await repository.ListAsync(integration.IntegrationId, ct)).Count > 0) return 0;
        var created = 0;
        foreach (var reference in references)
        {
            var version = await CreateVersionAsync(new CreateMappingVersion(integration.IntegrationId, reference.Yaml, Name: reference.Name, Order: reference.Order, Samples: reference.Samples), actor, ct);
            await ActivateAsync(version.MappingId, version.Version, actor, ct);
            created++;
        }
        return created;
    }

    /// <summary>Activates a version after re-running its samples; deactivates the previously active version of the same mapping id.</summary>
    public async Task ActivateAsync(Guid mappingId, int version, Actor actor, CancellationToken ct = default)
    {
        var target = await repository.GetAsync(mappingId, version, ct) ?? throw new KeyNotFoundException($"Mapping {mappingId} v{version} not found.");
        if (target.IsActive) return;
        var doc = MappingParser.Parse(JsonNode.Parse(target.Body)!, target.Version);
        var failures = VerifySamples(doc, ParseSamples(target.Samples), target.IntegrationId);
        if (failures.Count > 0)
        {
            throw new MappingValidationException(failures);
        }

        var now = time.GetUtcNow();
        var current = await repository.GetActiveAsync(mappingId, ct);
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
            Action = "mapping.activate",
            TargetType = "mapping",
            TargetId = mappingId.ToString(),
            Before = JsonSerializer.Serialize(new { version = current?.Version }, JsonDefaults.Stored),
            After = JsonSerializer.Serialize(new { version }, JsonDefaults.Stored),
            CorrelationId = actor.CorrelationId,
            RequestIp = actor.Ip,
        });
        await uow.CommitAsync(ct);
        resolver.Invalidate(target.IntegrationId);
    }

    /// <summary>Runs each sample through the document and compares the expected canonical fields (07 §1: activation is refused if any sample fails).</summary>
    public static IReadOnlyList<MappingValidationError> VerifySamples(MappingDocument doc, IReadOnlyList<MappingSample> samples, Guid integrationId)
    {
        var errors = new List<MappingValidationError>();
        for (var i = 0; i < samples.Count; i++)
        {
            var sample = samples[i];
            var label = $"$.samples[{i}]" + (sample.Name is null ? string.Empty : $" ({sample.Name})");
            try
            {
                var input = new RawInput(Encoding.UTF8.GetBytes(sample.Body.ToJsonString()), sample.Headers ?? new Dictionary<string, string>());
                var result = MappingEngine.Normalise(doc, input, new MappingContext(integrationId, Guid.Empty, DateTimeOffset.UnixEpoch, doc.Version));
                foreach (var (field, expected) in sample.Expected)
                {
                    var actual = field switch
                    {
                        "fingerprint" => result.Event.Fingerprint ?? string.Empty,
                        "delivery_key" => result.Event.DeliveryKey,
                        _ => ValueCoercion.AsDisplayString(result.Fields.GetValueOrDefault(field)),
                    };
                    if (!string.Equals(actual, expected, StringComparison.Ordinal))
                    {
                        errors.Add(new MappingValidationError($"{label}.expected.{field}", $"expected '{expected}' but mapping produced '{actual}'"));
                    }
                }
            }
            catch (MappingException ex)
            {
                errors.Add(new MappingValidationError(label, $"mapping failed: {ex.Message}"));
            }
        }
        return errors;
    }

    public static IReadOnlyList<MappingSample> ParseSamples(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        var arr = JsonNode.Parse(json) as JsonArray;
        if (arr is null) return [];
        var list = new List<MappingSample>();
        foreach (var item in arr.OfType<JsonObject>())
        {
            var body = item["body"] ?? new JsonObject();
            var headers = item["headers"] is JsonObject h ? h.ToDictionary(kv => kv.Key, kv => kv.Value?.ToString() ?? string.Empty, StringComparer.OrdinalIgnoreCase) : null;
            var expected = item["expected"] is JsonObject e ? e.ToDictionary(kv => kv.Key, kv => ValueCoercion.AsDisplayString(kv.Value), StringComparer.Ordinal) : new Dictionary<string, string>();
            list.Add(new MappingSample(item["name"]?.ToString(), body.DeepClone(), headers, expected));
        }
        return list;
    }
}

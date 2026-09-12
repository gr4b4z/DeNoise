using System.Text.Json;
using System.Text.Json.Nodes;
using AlertHub.Application.Abstractions;
using AlertHub.Application.Audit;
using AlertHub.Application.Mapping;
using AlertHub.Domain.Audit;
using AlertHub.Domain.Common;
using AlertHub.Domain.Notifications;
using AlertHub.Domain.Ops;

namespace AlertHub.Application.Notifications.Templates;

public interface ITemplateRepository
{
    Task<WebhookTemplate?> GetAsync(Guid templateId, int version, CancellationToken ct = default);
    Task<WebhookTemplate?> GetActiveAsync(Guid templateId, CancellationToken ct = default);
    Task<IReadOnlyList<WebhookTemplate>> ListActiveAsync(CancellationToken ct = default);
    Task<IReadOnlyList<WebhookTemplate>> ListVersionsAsync(Guid templateId, CancellationToken ct = default);
    Task<int> NextVersionAsync(Guid templateId, CancellationToken ct = default);
    void Add(WebhookTemplate template);
    /// <summary>The most recent outbox payload staged for an episode — a real notification model for the render preview.</summary>
    Task<string?> LatestPayloadForEpisodeAsync(Guid episodeId, CancellationToken ct = default);
}

public sealed record CreateTemplateVersion(string Name, string Format, string Body, string? ContentType = null, string? Description = null, Guid? TemplateId = null, JsonObject? SampleEvent = null);

/// <summary>
/// Webhook body templates (ADR-7, 06 §7): versioned like policies, built-ins seeded read-only, a version must render the sample before it is
/// stored, activation is the single switch. Rendering for the dispatcher resolves the destination's active template, or generic-json.
/// </summary>
public sealed class TemplateService(ITemplateRepository templates, TemplateRenderer renderer, IAuditWriter audit, IUnitOfWork uow, TimeProvider time)
{
    public Task<IReadOnlyList<WebhookTemplate>> ListActiveAsync(CancellationToken ct = default) => templates.ListActiveAsync(ct);
    public Task<IReadOnlyList<WebhookTemplate>> ListVersionsAsync(Guid id, CancellationToken ct = default) => templates.ListVersionsAsync(id, ct);
    public Task<WebhookTemplate?> GetAsync(Guid id, int version, CancellationToken ct = default) => templates.GetAsync(id, version, ct);

    /// <summary>Seeds the four built-ins when missing (API start, seed-dev, tests). Idempotent.</summary>
    public async Task<int> EnsureBuiltInsAsync(CancellationToken ct = default)
    {
        var now = time.GetUtcNow();
        var added = 0;
        foreach (var builtin in BuiltInTemplates.Create(now))
        {
            if (await templates.GetAsync(builtin.TemplateId, 1, ct) is not null) continue;
            var sample = NotificationModel.Sample(now);
            builtin.SampleOutput = (await renderer.RenderAsync(builtin, sample, ct)).Body;
            templates.Add(builtin);
            added++;
        }
        if (added > 0) await uow.CommitAsync(ct);
        return added;
    }

    /// <summary>New version: parses, renders the sample (stored as <c>sample_output</c>), enforces the size cap. Inactive until activated.</summary>
    public async Task<WebhookTemplate> CreateVersionAsync(CreateTemplateVersion request, Actor actor, CancellationToken ct = default)
    {
        var errors = new List<MappingValidationError>();
        if (string.IsNullOrWhiteSpace(request.Name)) errors.Add(new MappingValidationError("$.name", "required"));
        var format = request.Format.Trim().ToLowerInvariant();
        TemplateRenderer.Validate(format, request.Body, errors);
        if (errors.Count > 0) throw new MappingValidationException(errors);

        var templateId = request.TemplateId ?? Ids.New(time);
        if (request.TemplateId is { } existingId)
        {
            var versions = await templates.ListVersionsAsync(existingId, ct);
            if (versions.Count == 0) throw new KeyNotFoundException($"Template {existingId} not found.");
            if (versions.Any(v => v.Builtin)) throw new InvalidOperationException("Built-in templates are read-only; clone it into a new template instead.");
        }
        var now = time.GetUtcNow();
        var contentType = string.IsNullOrWhiteSpace(request.ContentType) ? (format == TemplateFormats.Json ? "application/json" : "text/plain; charset=utf-8") : request.ContentType.Trim();
        var version = new WebhookTemplate
        {
            TemplateId = templateId,
            Version = await templates.NextVersionAsync(templateId, ct),
            Name = request.Name.Trim(),
            Description = request.Description,
            Format = format,
            Body = request.Body,
            ContentType = contentType,
            CreatedBy = actor.Id,
            CreatedAt = now,
        };
        RenderedBody rendered;
        try
        {
            rendered = await renderer.RenderAsync(version, request.SampleEvent ?? NotificationModel.Sample(now), ct);
        }
        catch (TemplateRenderException ex)
        {
            throw new MappingValidationException([new MappingValidationError("$.body", ex.Message)]);
        }
        version.SampleOutput = rendered.Body;
        templates.Add(version);
        audit.Record(Audit(actor, "template.create_version", version, now, new { version.Version, version.Format, version.ContentType }));
        await uow.CommitAsync(ct);
        return version;
    }

    public async Task ActivateAsync(Guid templateId, int versionNumber, Actor actor, CancellationToken ct = default)
    {
        var target = await templates.GetAsync(templateId, versionNumber, ct) ?? throw new KeyNotFoundException($"Template {templateId} v{versionNumber} not found.");
        if (target.IsActive) return;
        // A version must still render its sample at activation (the built-ins may have changed the model since).
        try
        {
            await renderer.RenderAsync(target, NotificationModel.Sample(time.GetUtcNow()), ct);
        }
        catch (TemplateRenderException ex)
        {
            throw new MappingValidationException([new MappingValidationError("$.body", ex.Message)]);
        }
        var now = time.GetUtcNow();
        var current = await templates.GetActiveAsync(templateId, ct);
        if (current is not null) current.DeactivatedAt = now;
        target.ActivatedAt = now;
        target.DeactivatedAt = null;
        audit.Record(Audit(actor, "template.activate", target, now, new { from = current?.Version, to = versionNumber }));
        await uow.CommitAsync(ct);
    }

    /// <summary>Preview: a stored version rendered against a real recent payload of an episode, a supplied sample, or the built-in sample. Never sends.</summary>
    public async Task<RenderedBody> RenderPreviewAsync(Guid templateId, int version, JsonObject? sampleEvent, Guid? episodeId, CancellationToken ct = default)
    {
        var template = await templates.GetAsync(templateId, version, ct) ?? throw new KeyNotFoundException($"Template {templateId} v{version} not found.");
        var model = sampleEvent;
        if (model is null && episodeId is { } eid && await templates.LatestPayloadForEpisodeAsync(eid, ct) is { } payload)
        {
            model = JsonNode.Parse(payload) as JsonObject;
        }
        model ??= NotificationModel.Sample(time.GetUtcNow());
        model["deliveryId"] ??= Guid.NewGuid().ToString();
        model["sentAt"] ??= time.GetUtcNow().ToString("O");
        return await RenderAsync(template.Format == TemplateFormats.Json ? template : template, model, ct);
    }

    /// <summary>Renders the body a destination receives: its active template, or the model as-is (generic-json) when none is set.</summary>
    public async Task<RenderedBody> RenderForDestinationAsync(Destination destination, JsonObject model, CancellationToken ct = default)
    {
        if (destination.BodyTemplateId is not { } templateId) return new RenderedBody(model.ToJsonString(JsonDefaults.Stored), "application/json");
        var template = await templates.GetActiveAsync(templateId, ct) ?? throw new TemplateRenderException($"destination template {templateId} has no active version");
        return await RenderAsync(template, model, ct);
    }

    /// <summary>Renders a free-form draft (editor preview before saving).</summary>
    public Task<RenderedBody> RenderDraftAsync(string format, string body, string? contentType, JsonObject? sample, CancellationToken ct = default)
    {
        var errors = new List<MappingValidationError>();
        TemplateRenderer.Validate(format, body, errors);
        if (errors.Count > 0) throw new MappingValidationException(errors);
        var model = sample ?? NotificationModel.Sample(time.GetUtcNow());
        return renderer.RenderAsync(format, body, contentType ?? (format == TemplateFormats.Json ? "application/json" : "text/plain; charset=utf-8"), model, null, ct);
    }

    private Task<RenderedBody> RenderAsync(WebhookTemplate template, JsonObject model, CancellationToken ct) => renderer.RenderAsync(template, model, ct);

    private static AuditEntry Audit(Actor actor, string action, WebhookTemplate template, DateTimeOffset now, object after) => new()
    {
        Id = Guid.CreateVersion7(now),
        At = now,
        ActorType = actor.Type,
        ActorId = actor.Id,
        ActorDisplay = actor.Display,
        Action = action,
        TargetType = "template",
        TargetId = template.TemplateId.ToString(),
        After = JsonSerializer.Serialize(after, JsonDefaults.Stored),
        CorrelationId = actor.CorrelationId,
        RequestIp = actor.Ip,
    };
}

/// <summary>Kept for symmetry with policies: the dispatcher asks for a body per outbox row.</summary>
public static class OutboxModel
{
    public static JsonObject Of(OutboxMessage message, DateTimeOffset sentAt)
    {
        JsonObject model;
        try
        {
            model = JsonNode.Parse(message.Payload) as JsonObject ?? new JsonObject();
        }
        catch (JsonException)
        {
            model = new JsonObject { ["raw"] = message.Payload };
        }
        model["event"] = message.Type;
        model["deliveryId"] = message.OutboxId.ToString();
        model["sentAt"] = sentAt.ToString("O");
        model.Remove("_fallbackOf");
        return model;
    }
}

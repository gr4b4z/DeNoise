using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DeNoise.Api.Auth;
using DeNoise.Application.Abstractions;
using DeNoise.Application.Auth;
using DeNoise.Application.Integrations;
using DeNoise.Application.Mapping;
using DeNoise.Application.Ops;
using DeNoise.Application.Processing;
using DeNoise.Application.Replay;
using DeNoise.Contracts;
using DeNoise.Domain.Common;
using DeNoise.Domain.Integrations;
using DeNoise.Domain.Users;

namespace DeNoise.Api.Endpoints;

/// <summary>06 §4 "Integrations, mappings, replay": mapping versions, preview, activation, the failure queue and replay jobs.</summary>
public static class MappingEndpoints
{
    public static IEndpointRouteBuilder MapMappings(this IEndpointRouteBuilder app)
    {
        var integrations = app.MapGroup("/api/v1/integrations").WithTags("Mappings").RequireAuthorization().AddEndpointFilter<MustChangePasswordFilter>();

        integrations.MapGet("/reference-mappings/{type}", (string type) =>
        {
            var refs = ReferenceMappings.For(type);
            return refs.Count == 0 && !IntegrationTypes.All.Contains(type)
                ? Results.NotFound()
                : Results.Ok(refs.Select(r => new ReferenceMappingDto(r.Name, r.Order, r.Yaml, SamplesElement(r.Samples))).ToList());
        }).RequirePermission(Permissions.IntegrationRead).WithName("GetReferenceMappings").Produces<List<ReferenceMappingDto>>().Produces(404);

        integrations.MapGet("/{id:guid}/mappings", async (Guid id, HttpContext http, IIntegrationRepository repo, IMappingRepository mappings) =>
        {
            var integration = await VisibleAsync(id, http, repo);
            if (integration is null) return Results.NotFound();
            var versions = await mappings.ListAsync(id, http.RequestAborted);
            return Results.Ok(versions.OrderBy(v => v.Order).ThenBy(v => v.MappingId).ThenByDescending(v => v.Version).Select(ToDto).ToList());
        }).RequirePermission(Permissions.IntegrationRead).WithName("ListMappingVersions").Produces<List<MappingVersionDto>>().Produces(404);

        integrations.MapPost("/{id:guid}/mappings", async (Guid id, CreateMappingRequest request, HttpContext http, IIntegrationRepository repo, MappingService service) =>
        {
            var integration = await VisibleAsync(id, http, repo);
            if (integration is null) return Results.NotFound();
            var samples = request.Samples is { ValueKind: JsonValueKind.Array } s ? MappingService.ParseSamples(s.GetRawText()) : null;
            var created = await service.CreateVersionAsync(new CreateMappingVersion(id, request.Yaml, request.MappingId, request.Name, request.Order ?? 100, samples), Actor(http), http.RequestAborted);
            return Results.Created($"/api/v1/integrations/{id}/mappings/{created.MappingId}/{created.Version}", ToDto(created));
        }).RequirePermission(Permissions.MappingManage).AddEndpointFilter<CsrfFilter>().WithName("CreateMappingVersion").Produces<MappingVersionDto>(201).ProducesProblem(400).Produces(404);

        integrations.MapGet("/{id:guid}/mappings/{mappingId:guid}/{version:int}", async (Guid id, Guid mappingId, int version, HttpContext http, IIntegrationRepository repo, IMappingRepository mappings) =>
        {
            var integration = await VisibleAsync(id, http, repo);
            if (integration is null) return Results.NotFound();
            var stored = await mappings.GetAsync(mappingId, version, http.RequestAborted);
            return stored is null || stored.IntegrationId != id ? Results.NotFound() : Results.Ok(ToDto(stored));
        }).RequirePermission(Permissions.IntegrationRead).WithName("GetMappingVersion").Produces<MappingVersionDto>().Produces(404);

        // Preview (spec §17.5 preview mode, 06 §4): a stored version or a draft against raw events and/or a pasted body — no state change.
        integrations.MapPost("/{id:guid}/mappings/preview", async (Guid id, MappingPreviewRequest request, HttpContext http, IIntegrationRepository repo, MappingPreviewService preview) =>
        {
            var integration = await VisibleAsync(id, http, repo);
            if (integration is null) return Results.NotFound();
            if (string.IsNullOrWhiteSpace(request.Yaml)) throw new ArgumentException("yaml is required for a draft preview (use /mappings/{mappingId}/{version}/preview for a stored version)");
            var doc = MappingPreviewService.Draft(request.Yaml);
            return Results.Ok(await RunPreviewAsync(integration, doc, request, preview, http.RequestAborted));
        }).RequirePermission(Permissions.ReplayPreview).AddEndpointFilter<CsrfFilter>().WithName("PreviewMappingDraft").Produces<MappingPreviewResponse>().ProducesProblem(400).Produces(404);

        integrations.MapPost("/{id:guid}/mappings/{mappingId:guid}/{version:int}/preview", async (Guid id, Guid mappingId, int version, MappingPreviewRequest request, HttpContext http, IIntegrationRepository repo, MappingPreviewService preview) =>
        {
            var integration = await VisibleAsync(id, http, repo);
            if (integration is null) return Results.NotFound();
            var doc = await preview.ResolveAsync(mappingId, version, http.RequestAborted);
            return Results.Ok(await RunPreviewAsync(integration, doc, request, preview, http.RequestAborted));
        }).RequirePermission(Permissions.ReplayPreview).AddEndpointFilter<CsrfFilter>().WithName("PreviewMappingVersion").Produces<MappingPreviewResponse>().ProducesProblem(400).Produces(404);

        integrations.MapPost("/{id:guid}/mappings/{mappingId:guid}/{version:int}/activate", async (Guid id, Guid mappingId, int version, HttpContext http, IIntegrationRepository repo, IMappingRepository mappings, MappingService service) =>
        {
            var integration = await VisibleAsync(id, http, repo);
            if (integration is null) return Results.NotFound();
            var stored = await mappings.GetAsync(mappingId, version, http.RequestAborted);
            if (stored is null || stored.IntegrationId != id) return Results.NotFound();
            await service.ActivateAsync(mappingId, version, Actor(http), http.RequestAborted);
            return Results.NoContent();
        }).RequirePermission(Permissions.MappingManage).AddEndpointFilter<CsrfFilter>().WithName("ActivateMappingVersion").Produces(204).ProducesProblem(400).Produces(404);

        // Failure queue (alert.mapping_failure): quarantined events with the error and the offending field; a raw excerpt for the operator.
        integrations.MapGet("/{id:guid}/failures", async (Guid id, bool? all, int? limit, HttpContext http, IIntegrationRepository repo, IMappingFailureStore failures, IRawEventReader raw) =>
        {
            var integration = await VisibleAsync(id, http, repo);
            if (integration is null) return Results.NotFound();
            var rows = await failures.ListAsync(id, quarantinedOnly: all != true, Math.Clamp(limit ?? 100, 1, 500), http.RequestAborted);
            var result = new List<MappingFailureDto>(rows.Count);
            foreach (var f in rows)
            {
                string? excerpt = null;
                if (http.Principal().Permissions.Contains(Permissions.EpisodeRawPayloadRead) && await raw.GetAsync(f.EventId, f.RawReceivedAt, http.RequestAborted) is { } r)
                {
                    var text = Encoding.UTF8.GetString(r.Body);
                    excerpt = text.Length > 2048 ? text[..2048] + "…" : text;
                }
                result.Add(new MappingFailureDto(f.Id, f.EventId, f.RawReceivedAt, f.MappingVersion, f.Error, f.Field, f.Quarantined, f.ResolvedAt, excerpt));
            }
            return Results.Ok(result);
        }).RequirePermission(Permissions.IntegrationRead).WithName("ListMappingFailures").Produces<List<MappingFailureDto>>().Produces(404);

        integrations.MapPost("/{id:guid}/failures/{failureId:guid}/dismiss", async (Guid id, Guid failureId, HttpContext http, IIntegrationRepository repo, IMappingFailureStore failures, Application.Audit.IAuditWriter audit, IUnitOfWork uow, TimeProvider time) =>
        {
            var integration = await VisibleAsync(id, http, repo);
            if (integration is null) return Results.NotFound();
            var actor = Actor(http);
            var now = time.GetUtcNow();
            if (!await failures.DismissAsync(failureId, now, http.Principal().UserId, http.RequestAborted)) return Results.NotFound();
            audit.Record(new Domain.Audit.AuditEntry
            {
                Id = Ids.New(time),
                At = now,
                ActorType = actor.Type,
                ActorId = actor.Id,
                ActorDisplay = actor.Display,
                Action = "mapping_failure.dismiss",
                TargetType = "mapping_failure",
                TargetId = failureId.ToString(),
                AccessScope = integration.AccessScope,
                CorrelationId = actor.CorrelationId,
                RequestIp = actor.Ip,
            });
            await uow.CommitAsync(http.RequestAborted);
            return Results.NoContent();
        }).RequirePermission(Permissions.MappingManage).AddEndpointFilter<CsrfFilter>().WithName("DismissMappingFailure").Produces(204).Produces(404);

        var replay = app.MapGroup("/api/v1/replay").WithTags("Replay").RequireAuthorization().AddEndpointFilter<MustChangePasswordFilter>();
        replay.MapPost("", async (StartReplayRequest request, HttpContext http, IIntegrationRepository repo, ReplayService service) =>
        {
            var integration = await VisibleAsync(request.IntegrationId, http, repo);
            if (integration is null) return Results.NotFound();
            var needed = request.Mode switch
            {
                ReplayModes.RetryFailed => Permissions.ReplayRetry,
                ReplayModes.Historical => Permissions.ReplayHistorical,
                _ => throw new ArgumentException($"mode must be one of {string.Join(", ", ReplayModes.Jobs)} (preview is the synchronous mapping preview)"),
            };
            if (!http.Principal().Permissions.Contains(needed)) throw new ForbiddenException(needed);
            var job = await service.StartAsync(new StartReplay(request.Mode, request.IntegrationId, request.EventIds, request.From, request.To, request.MappingVersion, request.Limit), Actor(http), http.RequestAborted);
            return Results.Accepted($"/api/v1/replay/{job.JobId}", ToStatus(job));
        }).RequirePermission(Permissions.ReplayPreview).AddEndpointFilter<CsrfFilter>().WithName("StartReplay").Produces<ReplayStatus>(202).ProducesProblem(400).ProducesProblem(403).Produces(404);

        replay.MapGet("/{jobId:guid}", async (Guid jobId, HttpContext http, IJobQueue jobs, IIntegrationRepository repo) =>
        {
            var job = await jobs.GetAsync(jobId, http.RequestAborted);
            if (job is null || job.Kind != Domain.Ops.JobKinds.Replay || job.IntegrationId is null) return Results.NotFound();
            var integration = await VisibleAsync(job.IntegrationId.Value, http, repo);
            return integration is null ? Results.NotFound() : Results.Ok(ToStatus(job));
        }).RequirePermission(Permissions.ReplayPreview).WithName("GetReplay").Produces<ReplayStatus>().Produces(404);

        return app;
    }

    private static async Task<MappingPreviewResponse> RunPreviewAsync(Integration integration, MappingDocument doc, MappingPreviewRequest request, MappingPreviewService preview, CancellationToken ct)
    {
        var inputs = new List<PreviewInput>();
        if (request.RawEventIds is { Length: > 0 } ids) inputs.AddRange(await preview.LoadRawAsync(integration.IntegrationId, ids, ct));
        if (request.Body is { ValueKind: not JsonValueKind.Undefined and not JsonValueKind.Null } body)
        {
            inputs.Add(new PreviewInput("body", JsonNode.Parse(body.GetRawText()) ?? new JsonObject(), request.Headers));
        }
        if (inputs.Count == 0) throw new ArgumentException("nothing to preview: pass rawEventIds and/or a body");
        var items = await preview.PreviewAsync(integration, doc, inputs, request.IncludeRouting, ct);
        return new MappingPreviewResponse(doc.Version, items.Select(i => new MappingPreviewItemDto(i.Source, i.Applies, i.Ok, i.Error, i.ErrorField, new Dictionary<string, string?>(i.Fields),
            i.Identity.Select(c => new IdentityComponentDto(c.Name, c.Value)).ToList(), i.Fingerprint, i.DeliveryKey, i.LifecycleProfileHint,
            i.Routing is null ? null : new RoutingPreviewDto(i.Routing.TeamId, i.Routing.TeamName, i.Routing.RuleId, i.Routing.RuleName, i.Routing.CorrectionRequired, i.Routing.Why))).ToList());
    }

    private static async Task<Integration?> VisibleAsync(Guid id, HttpContext http, IIntegrationRepository repo)
    {
        var integration = await repo.GetCurrentAsync(id, http.RequestAborted);
        return integration is null || !http.Principal().CanSeeScope(integration.AccessScope) ? null : integration;
    }

    private static MappingVersionDto ToDto(MappingVersion v)
        => new(v.MappingId, v.Version, v.IntegrationId, v.Name, v.Order, v.IsActive, v.ActivatedAt, v.DeactivatedAt, v.IdentityVersion, v.CreatedBy, v.CreatedAt, v.SourceYaml, ParseElement(v.Samples));

    private static ReplayStatus ToStatus(Domain.Ops.Job job)
    {
        var payload = JsonSerializer.Deserialize<ReplayJobPayload>(job.Payload, JsonDefaults.Stored);
        return new ReplayStatus(job.JobId, job.Status, payload?.Mode ?? "?", job.IntegrationId ?? Guid.Empty, job.Attempts, job.LastError, job.CreatedAt, job.UpdatedAt, ParseElement(job.Result));
    }

    private static JsonElement? ParseElement(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JsonElement SamplesElement(IReadOnlyList<MappingSample> samples)
    {
        var json = JsonSerializer.Serialize(samples.Select(s => new { name = s.Name, body = s.Body, headers = s.Headers, expected = s.Expected }), JsonDefaults.Stored);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static Actor Actor(HttpContext http)
    {
        var p = http.Principal();
        return new Actor(Domain.Audit.ActorTypes.User, p.UserId.ToString(), p.Username, http.CorrelationId(), http.ClientIp());
    }
}

using AlertHub.Api.Auth;
using AlertHub.Application.Heartbeats;
using AlertHub.Application.Integrations;
using AlertHub.Application.Mapping;
using AlertHub.Application.Routing;
using AlertHub.Application.Teams;
using AlertHub.Contracts;
using AlertHub.Domain.Heartbeats;
using AlertHub.Domain.Users;
using Microsoft.Extensions.Options;

namespace AlertHub.Api.Endpoints;

/// <summary>Registered heartbeats (06 §4 "Heartbeats", spec §13.3.2): everything the UI can do, the API can do; declarable as YAML.</summary>
public static class HeartbeatEndpoints
{
    public static IEndpointRouteBuilder MapHeartbeats(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/heartbeats").WithTags("Heartbeats").RequireAuthorization().AddEndpointFilter<MustChangePasswordFilter>();

        group.MapGet("", async (string? state, Guid? team, string? scope, Guid? integration, string? q, HttpContext http, HeartbeatService service, ITeamRepository teams, IIntegrationRepository integrations) =>
        {
            var p = http.Principal();
            var list = (await service.ListAsync(new HeartbeatFilter(state, team, scope, integration, q), http.RequestAborted)).Where(h => p.CanSeeScope(h.AccessScope)).ToList();
            var teamMap = (await teams.ListAsync(http.RequestAborted)).ToDictionary(t => t.TeamId, t => t.Name);
            var integrationMap = (await integrations.ListCurrentAsync(http.RequestAborted)).ToDictionary(i => i.IntegrationId, i => i.Name);
            return Results.Ok(list.Select(h => ToSummary(h, teamMap, integrationMap)).ToList());
        }).RequirePermission(Permissions.EpisodeRead).WithName("ListHeartbeats").Produces<List<HeartbeatSummary>>();

        group.MapPost("", async (HeartbeatRequest request, HttpContext http, HeartbeatService service, ITeamRepository teams, IIntegrationRepository integrations) =>
        {
            var created = await service.CreateAsync(ToDefinition(request), http.Principal(), http.CorrelationId(), http.RequestAborted);
            return Results.Created($"/api/v1/heartbeats/{created.Heartbeat.HeartbeatId}", new HeartbeatCreatedResponse(await SummaryAsync(created.Heartbeat, teams, integrations, http.RequestAborted), created.PingUrl));
        }).RequirePermission(Permissions.HeartbeatManage).AddEndpointFilter<CsrfFilter>().WithName("CreateHeartbeat").Produces<HeartbeatCreatedResponse>(201).ProducesProblem(400);

        group.MapPost("/preview", (SchedulePreviewRequest request, TimeProvider time) =>
        {
            var errors = new List<MappingValidationError>();
            var interval = EscalationPolicyDocument.ParseDuration(request.Schedule.Interval is null ? null : System.Text.Json.Nodes.JsonValue.Create(request.Schedule.Interval), "$.schedule.interval", errors);
            HeartbeatSchedule.Validate(request.Schedule.Kind, interval, request.Schedule.Cron, request.Schedule.Timezone, TimeSpan.Zero, errors);
            if (errors.Count > 0) throw new MappingValidationException(errors);
            var now = time.GetUtcNow();
            var runs = HeartbeatSchedule.Preview(request.Schedule.Kind, interval, request.Schedule.Cron, request.Schedule.Timezone, now, Math.Clamp(request.Count, 1, 20));
            var probe = new Heartbeat { Name = "preview", AccessScope = "", ScheduleKind = request.Schedule.Kind, Interval = interval, Cron = request.Schedule.Cron, ScheduleTz = request.Schedule.Timezone, SeverityOnMiss = "high", KeyId = "", TokenHash = "" };
            return Results.Ok(new SchedulePreviewResponse(runs, HeartbeatSchedule.Describe(probe)));
        }).RequirePermission(Permissions.EpisodeRead).WithName("PreviewHeartbeatSchedule").Produces<SchedulePreviewResponse>().ProducesProblem(400);

        group.MapGet("/export", async (Guid? team, HttpContext http, HeartbeatService service) =>
            Results.Text(await service.ExportAsync(team, http.Principal(), http.RequestAborted), "application/yaml"))
            .RequirePermission(Permissions.EpisodeRead).WithName("ExportHeartbeats").Produces(200, contentType: "application/yaml");

        group.MapPost("/import", async (HeartbeatImportRequest request, HttpContext http, HeartbeatService service) =>
        {
            var result = await service.ImportAsync(request.Yaml, request.DryRun, http.Principal(), http.CorrelationId(), http.RequestAborted);
            return Results.Ok(new HeartbeatImportResponse(result.DryRun, result.Entries.Select(e => new HeartbeatImportEntryDto(e.Name, e.Action, e.HeartbeatId, e.Changes, e.PingUrl)).ToList(), result.Errors));
        }).RequirePermission(Permissions.HeartbeatManage).AddEndpointFilter<CsrfFilter>().WithName("ImportHeartbeats").Produces<HeartbeatImportResponse>();

        group.MapGet("/{id:guid}", async (Guid id, HttpContext http, HeartbeatService service, ITeamRepository teams, IIntegrationRepository integrations, TimeProvider time) =>
        {
            var hb = await service.GetAsync(id, http.RequestAborted);
            if (hb is null || !http.Principal().CanSeeScope(hb.AccessScope)) return NotFound(http, id);
            var runs = (await service.RunsAsync(id, 100, http.RequestAborted)).Select(r => new HeartbeatRunDto(r.Seq, r.StartedAt, r.FinishedAt, r.Kind, r.ExitCode, r.Body, r.SourceIp?.ToString())).ToList();
            var next = hb.IsPaused ? [] : HeartbeatSchedule.Preview(hb.ScheduleKind, hb.Interval, hb.Cron, hb.ScheduleTz, hb.LastPingAt ?? time.GetUtcNow(), 5);
            http.Response.Headers.ETag = $"\"{hb.Version}\"";
            return Results.Ok(new HeartbeatDetail(await SummaryAsync(hb, teams, integrations, http.RequestAborted), runs, next));
        }).RequirePermission(Permissions.EpisodeRead).WithName("GetHeartbeat").Produces<HeartbeatDetail>().ProducesProblem(404);

        group.MapPut("/{id:guid}", async (Guid id, HeartbeatRequest request, HttpContext http, HeartbeatService service, ITeamRepository teams, IIntegrationRepository integrations) =>
        {
            var hb = await service.UpdateAsync(id, IfMatchFilter.Version(http), ToDefinition(request), http.Principal(), http.CorrelationId(), http.RequestAborted);
            http.Response.Headers.ETag = $"\"{hb.Version}\"";
            return Results.Ok(await SummaryAsync(hb, teams, integrations, http.RequestAborted));
        }).RequirePermission(Permissions.HeartbeatManage).AddEndpointFilter<CsrfFilter>().AddEndpointFilter<IfMatchFilter>().WithName("UpdateHeartbeat").Produces<HeartbeatSummary>().ProducesProblem(400).ProducesProblem(404).ProducesProblem(409).ProducesProblem(428);

        group.MapDelete("/{id:guid}", async (Guid id, HttpContext http, HeartbeatService service) =>
        {
            await service.DeleteAsync(id, IfMatchFilter.Version(http), http.Principal(), http.CorrelationId(), http.RequestAborted);
            return Results.NoContent();
        }).RequirePermission(Permissions.HeartbeatManage).AddEndpointFilter<CsrfFilter>().AddEndpointFilter<IfMatchFilter>().WithName("DeleteHeartbeat").Produces(204).ProducesProblem(404).ProducesProblem(409).ProducesProblem(428);

        group.MapPost("/{id:guid}/pause", async (Guid id, PauseHeartbeatRequest request, HttpContext http, HeartbeatService service, ITeamRepository teams, IIntegrationRepository integrations) =>
        {
            var hb = await service.PauseAsync(id, IfMatchFilter.Version(http), request.Reason, http.Principal(), http.CorrelationId(), http.RequestAborted);
            return Results.Ok(await SummaryAsync(hb, teams, integrations, http.RequestAborted));
        }).RequirePermission(Permissions.HeartbeatManage).AddEndpointFilter<CsrfFilter>().AddEndpointFilter<IfMatchFilter>().WithName("PauseHeartbeat").Produces<HeartbeatSummary>().ProducesProblem(400).ProducesProblem(404).ProducesProblem(409).ProducesProblem(428);

        group.MapPost("/{id:guid}/resume", async (Guid id, HttpContext http, HeartbeatService service, ITeamRepository teams, IIntegrationRepository integrations) =>
        {
            var hb = await service.ResumeAsync(id, IfMatchFilter.Version(http), http.Principal(), http.CorrelationId(), http.RequestAborted);
            return Results.Ok(await SummaryAsync(hb, teams, integrations, http.RequestAborted));
        }).RequirePermission(Permissions.HeartbeatManage).AddEndpointFilter<CsrfFilter>().AddEndpointFilter<IfMatchFilter>().WithName("ResumeHeartbeat").Produces<HeartbeatSummary>().ProducesProblem(404).ProducesProblem(409).ProducesProblem(428);

        group.MapPost("/{id:guid}/rotate-token", async (Guid id, HttpContext http, HeartbeatService service) =>
        {
            var rotated = await service.RotateTokenAsync(id, IfMatchFilter.Version(http), http.Principal(), http.CorrelationId(), http.RequestAborted);
            return Results.Ok(new PingUrlResponse(rotated.PingUrl));
        }).RequirePermission(Permissions.HeartbeatManage).AddEndpointFilter<CsrfFilter>().AddEndpointFilter<IfMatchFilter>().WithName("RotateHeartbeatToken").Produces<PingUrlResponse>().ProducesProblem(404).ProducesProblem(409).ProducesProblem(428);

        return app;
    }

    private static HeartbeatDefinition ToDefinition(HeartbeatRequest r)
    {
        var errors = new List<MappingValidationError>();
        var interval = EscalationPolicyDocument.ParseDuration(r.Schedule.Interval is null ? null : System.Text.Json.Nodes.JsonValue.Create(r.Schedule.Interval), "$.schedule.interval", errors);
        var grace = EscalationPolicyDocument.ParseDuration(System.Text.Json.Nodes.JsonValue.Create(r.Grace), "$.grace", errors) ?? TimeSpan.FromMinutes(5);
        if (errors.Count > 0) throw new MappingValidationException(errors);
        return new HeartbeatDefinition(r.Name, r.Description, r.OwningTeamId, r.AssigneeId, r.Schedule.Kind?.ToLowerInvariant() ?? ScheduleKinds.Interval, interval, r.Schedule.Cron, r.Schedule.Timezone,
            grace, r.SeverityOnMiss?.ToLowerInvariant() ?? "high", r.RoutingPolicyId, r.BindsToIntegrationId, r.RecoverySuccessesRequired, r.AutoPauseDuringMaintenance, r.AccessScope);
    }

    private static async Task<HeartbeatSummary> SummaryAsync(Heartbeat hb, ITeamRepository teams, IIntegrationRepository integrations, CancellationToken ct)
    {
        var team = await teams.GetAsync(hb.OwningTeamId, ct);
        var bound = hb.BindsToIntegrationId is { } b ? await integrations.GetCurrentAsync(b, ct) : null;
        return ToSummary(hb, team is null ? [] : new Dictionary<Guid, string> { [team.TeamId] = team.Name }, bound is null ? [] : new Dictionary<Guid, string> { [bound.IntegrationId] = bound.Name });
    }

    internal static HeartbeatSummary ToSummary(Heartbeat h, IReadOnlyDictionary<Guid, string> teams, IReadOnlyDictionary<Guid, string> integrations) => new(
        h.HeartbeatId, h.Name, h.Description, h.AccessScope, teams.TryGetValue(h.OwningTeamId, out var teamName) ? new TeamRef(h.OwningTeamId, teamName) : null, h.AssigneeId,
        new HeartbeatScheduleDto(h.ScheduleKind, h.Interval?.ToString(), h.Cron, h.ScheduleTz, HeartbeatSchedule.Describe(h)), h.Grace.ToString(), h.SeverityOnMiss, h.RoutingPolicyId,
        h.BindsToIntegrationId, h.BindsToIntegrationId is { } bi && integrations.TryGetValue(bi, out var integrationName) ? integrationName : null, h.RecoverySuccessesRequired, h.AutoPauseDuringMaintenance,
        h.State, h.ExpectedNext, h.LastPingAt, h.LastPingIp?.ToString(), h.LastRunDuration?.ToString(), h.PausedBy, h.PausedAt, h.PauseReason, h.PausedByMaintenance, h.MissEpisodeId, h.KeyId, h.TokenRotatedAt,
        h.Version, h.CreatedAt, h.UpdatedAt);

    private static IResult NotFound(HttpContext http, Guid id) => Problems.Result(http, 404, "not-found", "Heartbeat not found", $"No heartbeat {id} is visible to you.");
}

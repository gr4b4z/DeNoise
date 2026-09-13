using System.Text.Json;
using System.Text.Json.Nodes;
using DeNoise.Api.Auth;
using DeNoise.Application.Abstractions;
using DeNoise.Application.Auth;
using DeNoise.Application.Episodes;
using DeNoise.Application.Suppressions;
using DeNoise.Application.Teams;
using DeNoise.Contracts;
using DeNoise.Domain.Policies;
using DeNoise.Domain.Users;
using DeNoise.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using FromQueryAttribute = Microsoft.AspNetCore.Mvc.FromQueryAttribute;

namespace DeNoise.Api.Endpoints;

public sealed record HistoryQuery(
    [property: FromQuery(Name = "severity")] string[]? Severity, [property: FromQuery(Name = "team")] Guid? Team, [property: FromQuery(Name = "scope")] string? Scope,
    [property: FromQuery(Name = "environment")] string[]? Environment, [property: FromQuery(Name = "service")] string? Service, [property: FromQuery(Name = "integration")] Guid? Integration,
    [property: FromQuery(Name = "q")] string? Q, [property: FromQuery(Name = "closureReason")] string? ClosureReason, [property: FromQuery(Name = "evidence")] string? Evidence,
    [property: FromQuery(Name = "sort")] string? Sort, [property: FromQuery(Name = "limit")] int? Limit, [property: FromQuery(Name = "cursor")] string? Cursor,
    [property: FromQuery(Name = "includeTotal")] bool? IncludeTotal);

/// <summary>06 §4: silences and maintenance windows (<c>/suppressions</c>), closed-episode history (<c>/history</c>) and group lookups.</summary>
public static class SuppressionEndpoints
{
    public static IEndpointRouteBuilder MapSuppressions(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/suppressions").WithTags("Suppressions").RequireAuthorization().AddEndpointFilter<MustChangePasswordFilter>();

        group.MapGet("", async (bool? all, HttpContext http, ISuppressionRepository repo, TimeProvider time) =>
        {
            var now = time.GetUtcNow();
            var rows = await repo.ListAsync(all == true, now, http.RequestAborted);
            return Results.Ok(rows.Select(s => ToDto(s, now)).ToList());
        }).RequirePermission(Permissions.EpisodeRead).WithName("ListSuppressions").Produces<List<SuppressionDto>>();

        group.MapGet("/{id:guid}", async (Guid id, HttpContext http, ISuppressionRepository repo, TimeProvider time) =>
        {
            var s = await repo.GetAsync(id, http.RequestAborted);
            return s is null ? Results.NotFound() : Results.Ok(ToDto(s, time.GetUtcNow()));
        }).RequirePermission(Permissions.EpisodeRead).WithName("GetSuppression").Produces<SuppressionDto>().Produces(404);

        group.MapPost("", async (CreateSuppressionRequest request, HttpContext http, SuppressionService service, TimeProvider time) =>
        {
            var p = http.Principal();
            var scope = JsonNode.Parse(request.Scope.GetRawText()) ?? throw new ArgumentException("scope is required");
            var now = time.GetUtcNow();
            var starts = request.StartsAt ?? (request.StartsLocal is { } sl ? WallClock.ToInstant(sl, WallClock.Zone(request.TimeZone)) : now);
            var ends = request.EndsAt ?? (request.EndsLocal is { } el ? WallClock.ToInstant(el, WallClock.Zone(request.TimeZone)) : (DateTimeOffset?)null);
            if (ends is { } e && e - starts > TimeSpan.FromHours(24) && !p.Has(Permissions.SuppressionCreateLong))
            {
                throw new ForbiddenException(Permissions.SuppressionCreateLong, "windows longer than 24 h need integration_admin");
            }
            var created = await service.CreateAsync(new CreateSuppression(request.Kind, scope, request.TimeZone, request.Reason, request.StartsAt, request.EndsAt, request.StartsLocal, request.EndsLocal, request.AutoPauseHeartbeats, request.Name), Actor(http), http.RequestAborted);
            return Results.Created($"/api/v1/suppressions/{created.SuppressionId}", ToDto(created, time.GetUtcNow()));
        }).RequirePermission(Permissions.SuppressionCreate).AddEndpointFilter<CsrfFilter>().WithName("CreateSuppression").Produces<SuppressionDto>(201).ProducesProblem(400).ProducesProblem(403);

        // DELETE = end now (spec §16.3): muted notifications are coalesced and the summary goes out, as at a natural end.
        group.MapDelete("/{id:guid}", async (Guid id, HttpContext http, SuppressionService service) =>
        {
            var ended = await service.CancelAsync(id, Actor(http), http.RequestAborted);
            return ended is null ? Results.NoContent() : Results.Ok(new SuppressionEndedResponse(ended.EpisodesReleased, ended.NotificationsMuted, ended.TeamsSummarised.Count, ended.SummaryRows));
        }).RequirePermission(Permissions.SuppressionCreate).AddEndpointFilter<CsrfFilter>().WithName("CancelSuppression").Produces<SuppressionEndedResponse>().Produces(204).ProducesProblem(404);

        group.MapPost("/preview-scope", (JsonElement scope) =>
        {
            var predicate = SuppressionService.ParseScope(JsonNode.Parse(scope.GetRawText()) ?? new JsonObject());
            return Results.Ok(new { text = PredicateText.Describe(predicate) });
        }).RequirePermission(Permissions.EpisodeRead).AddEndpointFilter<CsrfFilter>().WithName("PreviewSuppressionScope").ProducesProblem(400);

        var history = app.MapGroup("/api/v1/history").WithTags("History").RequireAuthorization().AddEndpointFilter<MustChangePasswordFilter>();
        history.MapGet("", async ([Microsoft.AspNetCore.Http.AsParameters] HistoryQuery query, HttpContext http, IEpisodeQueries queries, ITeamMemberRepository members) =>
        {
            var filter = new EpisodeFilter(View: QueueViews.Closed, Severity: query.Severity ?? [], TeamId: query.Team, Scope: query.Scope, Environment: query.Environment ?? [], Service: query.Service,
                IntegrationId: query.Integration, Query: query.Q, ClosureReason: query.ClosureReason, Evidence: query.Evidence, Sort: query.Sort, Limit: query.Limit ?? 50, Cursor: query.Cursor, IncludeTotal: query.IncludeTotal ?? false);
            var p = http.Principal();
            var myTeams = (await members.ListForUserAsync(p.UserId, http.RequestAborted)).Select(m => m.TeamId).ToList();
            return Results.Ok(await queries.ListAsync(p, filter, myTeams, http.RequestAborted));
        }).RequirePermission(Permissions.HistoryRead).WithName("ListHistory").Produces<PagedResponse<EpisodeListItem>>();

        var groups = app.MapGroup("/api/v1/groups").WithTags("Groups").RequireAuthorization().AddEndpointFilter<MustChangePasswordFilter>();
        groups.MapGet("/{id:guid}", async (Guid id, HttpContext http, DeNoiseDbContext db) =>
        {
            var g = await db.AlertGroups.AsNoTracking().SingleOrDefaultAsync(x => x.GroupId == id, http.RequestAborted);
            if (g is null || !http.Principal().CanSeeScope(g.AccessScope)) return Results.NotFound();
            using var key = JsonDocument.Parse(g.KeyValues);
            return Results.Ok(new AlertGroupDto(g.GroupId, g.AccessScope, g.RuleId, key.RootElement.Clone(), g.OpenedAt, g.WindowEndsAt, g.Severity, g.MemberCount, g.ClosedAt));
        }).RequirePermission(Permissions.EpisodeRead).WithName("GetGroup").Produces<AlertGroupDto>().Produces(404);

        return app;
    }

    private static SuppressionDto ToDto(Suppression s, DateTimeOffset now)
    {
        using var scope = JsonDocument.Parse(s.Scope);
        string text;
        try
        {
            text = PredicateText.Describe(SuppressionService.ParseScope(JsonNode.Parse(s.Scope)!));
        }
        catch (Exception ex) when (ex is JsonException or Application.Mapping.MappingValidationException)
        {
            text = s.Scope;
        }
        return new SuppressionDto(s.SuppressionId, s.Kind, s.Name, scope.RootElement.Clone(), text, s.StartsAt, s.EndsAt, s.TimeZone, SuppressionText.Window(s), s.Reason, s.AutoPauseHeartbeats, s.CreatedBy, s.CreatedAt,
            s.CancelledAt, s.StartedAt, s.SummarySentAt, s.IsActiveAt(now));
    }

    private static Actor Actor(HttpContext http)
    {
        var p = http.Principal();
        return new Actor(Domain.Audit.ActorTypes.User, p.UserId.ToString(), p.Username, http.CorrelationId(), http.ClientIp());
    }
}

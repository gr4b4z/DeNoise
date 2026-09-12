using AlertHub.Api.Auth;
using AlertHub.Application.Abstractions;
using AlertHub.Application.Auth;
using AlertHub.Application.Episodes;
using AlertHub.Application.Integrations;
using AlertHub.Application.Notifications;
using AlertHub.Application.Policies;
using AlertHub.Application.Teams;
using AlertHub.Contracts;
using AlertHub.Domain.Integrations;
using AlertHub.Domain.Notifications;
using AlertHub.Domain.Policies;
using AlertHub.Domain.Users;

namespace AlertHub.Api.Endpoints;

/// <summary>Teams, integrations, policies and destinations: the configuration surface the later UI milestones build on. DB is truth; YAML in, YAML out (ADR-6).</summary>
public static class ConfigEndpoints
{
    public static IEndpointRouteBuilder MapConfig(this IEndpointRouteBuilder app)
    {
        var teams = app.MapGroup("/api/v1/teams").WithTags("Teams").RequireAuthorization().AddEndpointFilter<MustChangePasswordFilter>();
        teams.MapGet("", async (HttpContext http, ITeamRepository repo, ITeamMemberRepository members) =>
        {
            var list = new List<TeamSummary>();
            foreach (var t in await repo.ListAsync(http.RequestAborted))
            {
                list.Add(new TeamSummary(t.TeamId, t.Name, t.AccessScopes, t.IsTriage, t.FallbackTeamId, t.DefaultEscalationPolicyId, (await members.ListForTeamAsync(t.TeamId, http.RequestAborted)).Count, t.Version));
            }
            return Results.Ok(list);
        }).RequirePermission(Permissions.EpisodeRead).WithName("ListTeams").Produces<List<TeamSummary>>();
        teams.MapPost("", async (CreateTeamRequest request, HttpContext http, TeamService service) =>
        {
            var team = await service.CreateAsync(new CreateTeam(request.Name, request.AccessScopes, request.IsTriage, request.FallbackTeamId, request.DefaultEscalationPolicyId), Actor(http), http.RequestAborted);
            return Results.Created($"/api/v1/teams/{team.TeamId}", new TeamSummary(team.TeamId, team.Name, team.AccessScopes, team.IsTriage, team.FallbackTeamId, team.DefaultEscalationPolicyId, 0, team.Version));
        }).RequirePermission(Permissions.TeamManage).AddEndpointFilter<CsrfFilter>().WithName("CreateTeam").Produces<TeamSummary>(201).ProducesProblem(400).ProducesProblem(409);
        teams.MapGet("/{id:guid}", async (Guid id, HttpContext http, ITeamRepository repo, ITeamMemberRepository members) =>
        {
            var t = await repo.GetAsync(id, http.RequestAborted);
            return t is null ? Results.NotFound() : Results.Ok(new TeamSummary(t.TeamId, t.Name, t.AccessScopes, t.IsTriage, t.FallbackTeamId, t.DefaultEscalationPolicyId, (await members.ListForTeamAsync(t.TeamId, http.RequestAborted)).Count, t.Version));
        }).RequirePermission(Permissions.EpisodeRead).WithName("GetTeam").Produces<TeamSummary>().Produces(404);
        teams.MapGet("/{id:guid}/overview", async (Guid id, HttpContext http, IEpisodeQueries queries) =>
        {
            var overview = await queries.TeamOverviewAsync(http.Principal(), id, http.RequestAborted);
            return overview is null ? Results.NotFound() : Results.Ok(overview);
        }).RequirePermission(Permissions.EpisodeRead).WithName("GetTeamOverview").Produces<TeamOverview>().Produces(404);
        teams.MapPost("/{id:guid}/members/{userId:guid}", async (Guid id, Guid userId, HttpContext http, ITeamMemberRepository members, IUnitOfWork uow) =>
        {
            if ((await members.ListForTeamAsync(id, http.RequestAborted)).Any(m => m.UserId == userId)) return Results.NoContent();
            members.Add(new TeamMember { TeamId = id, UserId = userId });
            await uow.CommitAsync(http.RequestAborted);
            return Results.NoContent();
        }).RequirePermission(Permissions.TeamManage).AddEndpointFilter<CsrfFilter>().WithName("AddTeamMember").Produces(204);
        teams.MapDelete("/{id:guid}/members/{userId:guid}", async (Guid id, Guid userId, HttpContext http, ITeamMemberRepository members) =>
            await members.RemoveAsync(id, userId, http.RequestAborted) > 0 ? Results.NoContent() : Results.NotFound())
            .RequirePermission(Permissions.TeamManage).AddEndpointFilter<CsrfFilter>().WithName("RemoveTeamMember").Produces(204).Produces(404);

        var integrations = app.MapGroup("/api/v1/integrations").WithTags("Integrations").RequireAuthorization().AddEndpointFilter<MustChangePasswordFilter>();
        integrations.MapGet("", async (HttpContext http, IIntegrationRepository repo) =>
        {
            var p = http.Principal();
            return Results.Ok((await repo.ListCurrentAsync(http.RequestAborted)).Where(i => p.CanSeeScope(i.AccessScope)).Select(ToSummary).ToList());
        }).RequirePermission(Permissions.IntegrationRead).WithName("ListIntegrations").Produces<List<IntegrationSummary>>();
        integrations.MapGet("/{id:guid}", async (Guid id, HttpContext http, IIntegrationRepository repo) =>
        {
            var i = await repo.GetCurrentAsync(id, http.RequestAborted);
            return i is null || !http.Principal().CanSeeScope(i.AccessScope) ? Results.NotFound() : Results.Ok(ToSummary(i));
        }).RequirePermission(Permissions.IntegrationRead).WithName("GetIntegration").Produces<IntegrationSummary>().Produces(404);
        integrations.MapGet("/{id:guid}/health", async (Guid id, HttpContext http, IIntegrationRepository repo, Infrastructure.ReadModels.HealthQueries health) =>
        {
            var i = await repo.GetCurrentAsync(id, http.RequestAborted);
            if (i is null || !http.Principal().CanSeeScope(i.AccessScope)) return Results.NotFound();
            return Results.Ok(await health.IntegrationAsync(i, http.RequestAborted));
        }).RequirePermission(Permissions.IntegrationRead).WithName("GetIntegrationHealth").Produces<IntegrationHealth>().Produces(404);
        integrations.MapPost("", async (CreateIntegrationRequest request, HttpContext http, IntegrationService service) =>
        {
            var created = await service.CreateAsync(new CreateIntegration(request.Name, request.Type, request.AccessScope, request.OwnerTeamId), Actor(http), http.RequestAborted);
            return Results.Created($"/api/v1/integrations/{created.Integration.IntegrationId}", new IntegrationCreatedResponse(ToSummary(created.Integration), created.IngestPath, created.IngestToken));
        }).RequirePermission(Permissions.IntegrationManage).AddEndpointFilter<CsrfFilter>().WithName("CreateIntegration").Produces<IntegrationCreatedResponse>(201).ProducesProblem(400);
        integrations.MapPost("/{id:guid}/rotate-ingest-token", async (Guid id, HttpContext http, IntegrationService service) =>
        {
            var rotated = await service.RotateIngestTokenAsync(id, Actor(http), http.RequestAborted);
            return Results.Ok(new IntegrationCreatedResponse(ToSummary(rotated.Integration), rotated.IngestPath, rotated.IngestToken));
        }).RequirePermission(Permissions.IntegrationManage).AddEndpointFilter<CsrfFilter>().WithName("RotateIngestToken").Produces<IntegrationCreatedResponse>();

        var policies = app.MapGroup("/api/v1/policies/{kind}").WithTags("Policies").RequireAuthorization().AddEndpointFilter<MustChangePasswordFilter>();
        policies.MapGet("", async (string kind, HttpContext http, IPolicyRepository repo) =>
        {
            if (!PolicyKinds.All.Contains(kind)) return Problems.Result(http, 400, "validation", "Unknown policy kind", null);
            var active = await repo.ListActiveAsync(kind, http.RequestAborted);
            return Results.Ok(active.Select(v => ToDto(v, includeYaml: true)).ToList());
        }).RequirePermission(Permissions.EpisodeRead).WithName("ListActivePolicies").Produces<List<PolicyVersionDto>>();
        policies.MapGet("/{id:guid}/versions", async (string kind, Guid id, HttpContext http, IPolicyRepository repo) =>
            Results.Ok((await repo.ListVersionsAsync(kind, id, http.RequestAborted)).Select(v => ToDto(v, includeYaml: true)).ToList()))
            .RequirePermission(Permissions.EpisodeRead).WithName("ListPolicyVersions").Produces<List<PolicyVersionDto>>();
        policies.MapPost("", async (string kind, CreatePolicyRequest request, HttpContext http, PolicyService service) =>
        {
            var v = await service.CreateVersionAsync(new CreatePolicyVersion(kind, request.Yaml, request.PolicyId, request.Name), Actor(http), http.RequestAborted);
            return Results.Created($"/api/v1/policies/{kind}/{v.PolicyId}/{v.Version}", ToDto(v, includeYaml: true));
        }).RequirePermission(Permissions.PolicyManage).AddEndpointFilter<CsrfFilter>().WithName("CreatePolicyVersion").Produces<PolicyVersionDto>(201).ProducesProblem(400);
        policies.MapPost("/{id:guid}/{version:int}/activate", async (string kind, Guid id, int version, HttpContext http, PolicyService service) =>
        {
            await service.ActivateAsync(kind, id, version, Actor(http), http.RequestAborted);
            return Results.NoContent();
        }).RequirePermission(Permissions.PolicyManage).AddEndpointFilter<CsrfFilter>().WithName("ActivatePolicyVersion").Produces(204).ProducesProblem(409);
        policies.MapPost("/{id:guid}/{version:int}/impact", async (string kind, Guid id, int version, HttpContext http, PolicyImpactService impact) =>
        {
            var result = await impact.PreviewAsync(kind, id, version, http.RequestAborted);
            return Results.Ok(new PolicyImpactDto(result.Kind, result.PolicyId, result.Version, result.AffectedOpenEpisodes,
                result.Sample.Select(x => new PolicyImpactSampleDto(x.EpisodeId, x.Summary, x.Severity, x.LastSeen, x.CurrentAutoResolveAt, x.ProposedAutoResolveAt, x.Note)).ToList(), result.Explanation));
        }).RequirePermission(Permissions.PolicyManage).AddEndpointFilter<CsrfFilter>().WithName("PreviewPolicyImpact").Produces<PolicyImpactDto>().ProducesProblem(404);
        policies.MapPost("/{id:guid}/rollback", async (string kind, Guid id, RollbackRequest request, HttpContext http, PolicyService service) =>
        {
            await service.RollbackAsync(kind, id, request.ToVersion, Actor(http), http.RequestAborted);
            return Results.NoContent();
        }).RequirePermission(Permissions.PolicyManage).AddEndpointFilter<CsrfFilter>().WithName("RollbackPolicy").Produces(204).ProducesProblem(409);

        var destinations = app.MapGroup("/api/v1/destinations").WithTags("Destinations").RequireAuthorization().AddEndpointFilter<MustChangePasswordFilter>();
        destinations.MapGet("", async (HttpContext http, IDestinationRepository repo, DestinationService service) =>
            Results.Ok((await repo.ListAsync(http.RequestAborted)).Select(d => ToSummary(d, service)).ToList()))
            .RequirePermission(Permissions.EpisodeRead).WithName("ListDestinations").Produces<List<DestinationSummary>>();
        destinations.MapPost("", async (CreateDestinationRequest request, HttpContext http, DestinationService service) =>
        {
            var created = await service.CreateAsync(ToCreate(request), Actor(http), http.RequestAborted);
            return Results.Created($"/api/v1/destinations/{created.Destination.DestinationId}", new DestinationCreatedResponse(ToSummary(created.Destination, service), created.SigningSecret));
        }).RequirePermission(Permissions.DestinationManage).AddEndpointFilter<CsrfFilter>().WithName("CreateDestination").Produces<DestinationCreatedResponse>(201).ProducesProblem(400);
        destinations.MapPost("/pair", async (CreateDestinationPairRequest request, HttpContext http, DestinationService service) =>
        {
            var (primary, fallback) = await service.CreatePairAsync(ToCreate(request.Primary), ToCreate(request.Fallback), Actor(http), http.RequestAborted);
            return Results.Created($"/api/v1/destinations/{primary.Destination.DestinationId}", new DestinationPairResponse(
                new DestinationCreatedResponse(ToSummary(primary.Destination, service), primary.SigningSecret), new DestinationCreatedResponse(ToSummary(fallback.Destination, service), fallback.SigningSecret)));
        }).RequirePermission(Permissions.DestinationManage).AddEndpointFilter<CsrfFilter>().WithName("CreateDestinationPair").Produces<DestinationPairResponse>(201).ProducesProblem(400);
        destinations.MapGet("/{id:guid}", async (Guid id, HttpContext http, IDestinationRepository repo, DestinationService service) =>
        {
            var d = await repo.GetAsync(id, http.RequestAborted);
            if (d is null) return Results.NotFound();
            http.Response.Headers.ETag = $"\"{d.Version}\"";
            return Results.Ok(ToSummary(d, service));
        }).RequirePermission(Permissions.EpisodeRead).WithName("GetDestination").Produces<DestinationSummary>().Produces(404);
        destinations.MapPut("/{id:guid}", async (Guid id, UpdateDestinationRequest request, HttpContext http, DestinationService service) =>
        {
            var updated = await service.UpdateAsync(id, IfMatchFilter.Version(http), new UpdateDestination(request.Name, request.TeamId, request.FallbackDestinationId, request.Url, request.Method, request.Headers,
                request.RotateSigningSecret, request.Timeout is null ? null : TimeSpan.Parse(request.Timeout, System.Globalization.CultureInfo.InvariantCulture), request.EventTypes, request.EmailTo,
                request.BodyTemplateId, request.ClearBodyTemplate, request.Active), Actor(http), http.RequestAborted);
            http.Response.Headers.ETag = $"\"{updated.Version}\"";
            return Results.Ok(new DestinationUpdatedResponse(ToSummary(updated, service), service.TakeRotatedSecret()));
        }).RequirePermission(Permissions.DestinationManage).AddEndpointFilter<CsrfFilter>().AddEndpointFilter<IfMatchFilter>().WithName("UpdateDestination").Produces<DestinationUpdatedResponse>().ProducesProblem(400).ProducesProblem(404).ProducesProblem(409).ProducesProblem(428);
        destinations.MapPost("/{id:guid}/test", async (Guid id, HttpContext http, DestinationService service) =>
        {
            var r = await service.TestSendAsync(id, Actor(http), http.RequestAborted);
            return Results.Ok(new TestSendResponse(r.Outcome, r.HttpStatus, r.LatencyMs, r.Error, r.ResponseExcerpt, r.RenderedBody, r.ContentType));
        }).RequirePermission(Permissions.DestinationManage).AddEndpointFilter<CsrfFilter>().WithName("TestDestination").Produces<TestSendResponse>().ProducesProblem(404);
        destinations.MapPost("/{id:guid}/reveal", async (Guid id, RevealUrlRequest request, HttpContext http, IDestinationRepository repo, DestinationService service, IUserRepository users, ISecretHasher hasher) =>
        {
            // 08 §3.7b: revealing the URL and header values requires re-typing the password; PATs cannot reveal.
            var p = http.Principal();
            var user = p.Credential == CredentialKind.Session ? await users.GetAsync(p.UserId, http.RequestAborted) : null;
            if (user?.PasswordHash is null || !hasher.Verify(request.Password ?? string.Empty, user.PasswordHash)) return Problems.Result(http, 403, "forbidden", "Password required", "Re-type your password to reveal destination secrets.");
            var d = await repo.GetAsync(id, http.RequestAborted);
            if (d is null) return Results.NotFound();
            return Results.Ok(new RevealUrlResponse(service.RevealUrl(d), service.RevealHeaders(d)));
        }).RequirePermission(Permissions.DestinationManage).AddEndpointFilter<CsrfFilter>().WithName("RevealDestination").Produces<RevealUrlResponse>().ProducesProblem(403).Produces(404);
        destinations.MapGet("/{id:guid}/deliveries", async (Guid id, int? limit, HttpContext http, Infrastructure.Persistence.AlertHubDbContext db) =>
        {
            var rows = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToListAsync(
                (from a in db.DeliveryAttempts join o in db.Outbox on a.OutboxId equals o.OutboxId where o.DestinationId == id orderby a.AttemptedAt descending select a).Take(Math.Clamp(limit ?? 50, 1, 200)), http.RequestAborted);
            return Results.Ok(rows.Select(a => new DeliveryAttemptDto(a.Id, a.OutboxId, a.AttemptedAt, a.Channel, a.Outcome, a.HttpStatus, a.LatencyMs, a.Error, a.UsedFallback, a.ResponseExcerpt)).ToList());
        }).RequirePermission(Permissions.DestinationManage).WithName("ListDestinationDeliveries").Produces<List<DeliveryAttemptDto>>();

        return app;
    }

    private static Application.Abstractions.Actor Actor(HttpContext http)
    {
        var p = http.Principal();
        return new Application.Abstractions.Actor(Domain.Audit.ActorTypes.User, p.UserId.ToString(), p.Username, http.CorrelationId(), http.ClientIp());
    }

    private static IntegrationSummary ToSummary(Integration i) => new(i.IntegrationId, i.Name, i.Type, i.AccessScope, i.OwnerTeamId, i.IngestKeyId, i.Active, i.Shadow, i.Version, i.ActivatedAt);

    private static PolicyVersionDto ToDto(PolicyVersion v, bool includeYaml) => new(v.PolicyId, v.Kind, v.Version, v.Name, v.IsActive, v.ActivatedAt, v.DeactivatedAt, v.CreatedBy, v.CreatedAt, includeYaml ? v.SourceYaml : null);

    private static DestinationSummary ToSummary(Destination d, DestinationService service)
    {
        var url = service.RevealUrl(d);
        string? masked = null;
        if (url is not null && Uri.TryCreate(url, UriKind.Absolute, out var uri)) masked = $"{uri.Scheme}://{uri.Host}/…";
        return new DestinationSummary(d.DestinationId, d.Name, d.ChannelType, d.TeamId, d.Method, d.EventTypes, d.FallbackDestinationId, d.Active, d.LastSuccessAt, d.LastFailureAt, d.ConsecutiveFailures, d.EmailTo, masked, d.Version,
            d.BodyTemplateId, d.Timeout.ToString(), d.HeadersEnc is not null, d.SigningSecretEnc is not null);
    }

    private static CreateDestination ToCreate(CreateDestinationRequest r) => new(r.Name, r.ChannelType, r.TeamId, r.FallbackDestinationId, r.Url, r.Method, r.Headers, r.SigningSecret,
        r.Timeout is null ? null : TimeSpan.Parse(r.Timeout, System.Globalization.CultureInfo.InvariantCulture), r.EventTypes, r.EmailTo, null);
}

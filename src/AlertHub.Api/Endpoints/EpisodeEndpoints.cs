using System.Text.Json;
using AlertHub.Api.Auth;
using AlertHub.Application.Abstractions;
using AlertHub.Application.Auth;
using AlertHub.Application.Episodes;
using AlertHub.Contracts;
using AlertHub.Domain.Episodes;
using AlertHub.Domain.Users;

namespace AlertHub.Api.Endpoints;

public static class EpisodeEndpoints
{
    public static IEndpointRouteBuilder MapEpisodes(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/episodes").WithTags("Episodes").RequireAuthorization().AddEndpointFilter<MustChangePasswordFilter>();

        group.MapGet("", async (HttpContext http, IEpisodeQueries queries, ITeamMemberRepository members) =>
        {
            var q = http.Request.Query;
            var filter = new EpisodeFilter(
                View: q["view"].FirstOrDefault(), Severity: q["severity"].Where(s => s is not null).Select(s => s!).ToList(), Handling: q["handling"].Where(s => s is not null).Select(s => s!).ToList(),
                Condition: q["condition"].Where(s => s is not null).Select(s => s!).ToList(), TeamId: Guid.TryParse(q["team"], out var team) ? team : null, Scope: q["scope"].FirstOrDefault(),
                Environment: q["environment"].FirstOrDefault(), Service: q["service"].FirstOrDefault(), IntegrationId: Guid.TryParse(q["integration"], out var integ) ? integ : null,
                Query: q["q"].FirstOrDefault(), ClosureReason: q["closureReason"].FirstOrDefault(), Evidence: q["evidence"].FirstOrDefault(), Sort: q["sort"].FirstOrDefault(),
                Limit: int.TryParse(q["limit"], out var limit) ? limit : 50, Cursor: q["cursor"].FirstOrDefault(), IncludeTotal: q["includeTotal"] == "true");
            if (filter.View is not null && !QueueViews.All.Contains(filter.View)) return Problems.Result(http, 400, "validation", "Unknown view", $"view must be one of {string.Join(", ", QueueViews.All)}");
            var p = http.Principal();
            var myTeams = (await members.ListForUserAsync(p.UserId, http.RequestAborted)).Select(m => m.TeamId).ToList();
            return Results.Ok(await queries.ListAsync(p, filter, myTeams, http.RequestAborted));
        }).RequirePermission(Permissions.EpisodeRead).WithName("ListEpisodes");

        group.MapGet("/counts", async (HttpContext http, IEpisodeQueries queries, ITeamMemberRepository members) =>
        {
            var p = http.Principal();
            var myTeams = (await members.ListForUserAsync(p.UserId, http.RequestAborted)).Select(m => m.TeamId).ToList();
            return Results.Ok(await queries.ViewCountsAsync(p, myTeams, http.RequestAborted));
        }).RequirePermission(Permissions.EpisodeRead).WithName("GetEpisodeViewCounts");

        group.MapGet("/{id:guid}", async (Guid id, HttpContext http, IEpisodeQueries queries) =>
        {
            var detail = await queries.GetAsync(http.Principal(), id, http.RequestAborted);
            if (detail is null) return NotFound(http, id);
            http.Response.Headers.ETag = $"\"{detail.Item.Version}\"";
            return Results.Ok(detail);
        }).RequirePermission(Permissions.EpisodeRead).WithName("GetEpisode");

        group.MapGet("/{id:guid}/timeline", async (Guid id, int? limit, string? cursor, string? kind, HttpContext http, IEpisodeQueries queries) =>
        {
            if (await queries.GetAsync(http.Principal(), id, http.RequestAborted) is null) return NotFound(http, id);
            return Results.Ok(await queries.TimelineAsync(http.Principal(), id, limit ?? 50, cursor, kind, http.RequestAborted));
        }).RequirePermission(Permissions.EpisodeRead).WithName("GetEpisodeTimeline");

        group.MapGet("/{id:guid}/related", async (Guid id, HttpContext http, IEpisodeQueries queries) =>
        {
            var related = await queries.RelatedAsync(http.Principal(), id, http.RequestAborted);
            return related is null ? NotFound(http, id) : Results.Ok(related);
        }).RequirePermission(Permissions.EpisodeRead).WithName("GetRelatedEpisodes");

        group.MapGet("/{id:guid}/raw/{eventId:guid}", async (Guid id, Guid eventId, HttpContext http, IEpisodeQueries queries) =>
        {
            var raw = await queries.RawPayloadAsync(http.Principal(), id, eventId, http.RequestAborted);
            if (raw is null) return NotFound(http, id);
            http.Response.Headers["X-Received-At"] = raw.Value.ReceivedAt.ToString("O");
            return Results.Bytes(raw.Value.Body, raw.Value.ContentType ?? "application/octet-stream");
        }).RequirePermission(Permissions.EpisodeRawPayloadRead).WithName("GetEpisodeRawPayload");

        // Actions: auth → permission → If-Match → Idempotency-Key → handler (ADR-3 filter pipeline).
        Action(group, "/{id:guid}/ack", Permissions.EpisodeAck, async (id, body, http, actions) =>
            await actions.AcknowledgeAsync(http.Principal(), id, IfMatchFilter.Version(http), Read<AckRequest>(body)?.Force ?? false, http.CorrelationId(), http.RequestAborted), "AcknowledgeEpisode");
        Action(group, "/{id:guid}/assign", Permissions.EpisodeAssign, async (id, body, http, actions) =>
        {
            var r = Read<AssignRequest>(body) ?? new AssignRequest(null, null);
            return await actions.AssignAsync(http.Principal(), id, IfMatchFilter.Version(http), r.TeamId, r.UserId, http.CorrelationId(), http.RequestAborted);
        }, "AssignEpisode");
        Action(group, "/{id:guid}/note", Permissions.EpisodeNote, async (id, body, http, actions) =>
            await actions.NoteAsync(http.Principal(), id, IfMatchFilter.Version(http), Read<NoteRequest>(body)?.Text ?? string.Empty, http.CorrelationId(), http.RequestAborted), "AddEpisodeNote");
        Action(group, "/{id:guid}/close", Permissions.EpisodeClose, async (id, body, http, actions) =>
            await actions.CloseAsync(http.Principal(), id, IfMatchFilter.Version(http), Read<CloseRequest>(body)?.Reason ?? string.Empty, http.CorrelationId(), http.RequestAborted), "CloseEpisode");
        Action(group, "/{id:guid}/restore", Permissions.EpisodeRestore, async (id, body, http, actions) =>
            await actions.RestoreAsync(http.Principal(), id, IfMatchFilter.Version(http), Read<RestoreRequest>(body)?.Reason, http.CorrelationId(), http.RequestAborted), "RestoreEpisode");
        Action(group, "/{id:guid}/silence", Permissions.EpisodeSilence, async (id, body, http, actions) =>
        {
            var r = Read<SilenceRequest>(body) ?? throw new ArgumentException("until and reason are required");
            return await actions.SilenceAsync(http.Principal(), id, IfMatchFilter.Version(http), r.Until, r.Reason, http.CorrelationId(), http.RequestAborted);
        }, "SilenceEpisode");

        group.MapPost("/{id:guid}/refresh-state", async (Guid id, HttpContext http, EpisodeActionService actions, IEpisodeQueries queries) =>
        {
            await actions.RefreshStateAsync(http.Principal(), id, http.CorrelationId(), http.RequestAborted);
            return Results.Accepted($"/api/v1/episodes/{id}");
        }).RequirePermission(Permissions.EpisodeRead).AddEndpointFilter<CsrfFilter>().AddEndpointFilter<IdempotencyFilter>().WithName("RefreshEpisodeState");

        group.MapPost("/bulk", async (BulkRequest request, HttpContext http, EpisodeActionService actions, IEpisodeQueries queries) =>
        {
            if (request.Ids.Count is 0 or > 200) return Problems.Result(http, 400, "validation", "Between 1 and 200 ids are required", null);
            var p = http.Principal();
            var results = new List<BulkItemResult>();
            foreach (var id in request.Ids.Distinct())
            {
                try
                {
                    var detail = await queries.GetAsync(p, id, http.RequestAborted);
                    if (detail is null) { results.Add(new BulkItemResult(id, false, new ProblemDto("urn:alerthub:error:not-found", 404, "Not found", null))); continue; }
                    var version = detail.Item.Version;
                    var prm = request.Params;
                    switch (request.Action)
                    {
                        case "ack": await actions.AcknowledgeAsync(p, id, version, Param(prm, "force")?.GetBoolean() ?? false, http.CorrelationId(), http.RequestAborted); break;
                        case "assign": await actions.AssignAsync(p, id, version, Param(prm, "teamId")?.GetGuid(), Param(prm, "userId")?.GetGuid(), http.CorrelationId(), http.RequestAborted); break;
                        case "close": await actions.CloseAsync(p, id, version, Param(prm, "reason")?.GetString() ?? string.Empty, http.CorrelationId(), http.RequestAborted); break;
                        case "note": await actions.NoteAsync(p, id, version, Param(prm, "text")?.GetString() ?? string.Empty, http.CorrelationId(), http.RequestAborted); break;
                        case "silence": await actions.SilenceAsync(p, id, version, Param(prm, "until")?.GetDateTimeOffset() ?? default, Param(prm, "reason")?.GetString() ?? string.Empty, http.CorrelationId(), http.RequestAborted); break;
                        default: return Problems.Result(http, 400, "validation", "Unknown bulk action", "action must be ack, assign, close, note or silence");
                    }
                    results.Add(new BulkItemResult(id, true, null));
                }
                catch (Exception ex) when (ex is ForbiddenException or OutOfScopeException or InvalidEpisodeTransitionException or ArgumentException or VersionConflictException or KeyNotFoundException)
                {
                    var (status, code, title) = Classify(ex);
                    results.Add(new BulkItemResult(id, false, new ProblemDto($"urn:alerthub:error:{code}", status, title, ex.Message)));
                }
            }
            return Results.Ok(new BulkResponse(results));
        }).RequirePermission(Permissions.EpisodeBulk).AddEndpointFilter<CsrfFilter>().AddEndpointFilter<IdempotencyFilter>().WithName("BulkEpisodeAction");

        return app;
    }

    private static void Action(RouteGroupBuilder group, string pattern, string permission, Func<Guid, JsonElement?, HttpContext, EpisodeActionService, Task<ActionResult>> run, string name)
    {
        group.MapPost(pattern, async (Guid id, HttpContext http, EpisodeActionService actions, IEpisodeQueries queries) =>
        {
            JsonElement? body = null;
            if (http.Request.ContentLength is > 0)
            {
                http.Request.EnableBuffering();
                http.Request.Body.Position = 0;
                body = await JsonSerializer.DeserializeAsync<JsonElement>(http.Request.Body, JsonDefaults.Stored, http.RequestAborted);
            }
            try
            {
                await run(id, body, http, actions);
            }
            catch (VersionConflictException ex)
            {
                var current = await queries.GetAsync(http.Principal(), id, http.RequestAborted);
                return Results.Json(Problems.Body(http, 409, "version-conflict", "Episode was updated by someone else", $"Current version is {ex.CurrentVersion}; refresh and retry.", new { current }), JsonDefaults.Stored, Problems.ContentType, 409);
            }
            var detail = await queries.GetAsync(http.Principal(), id, http.RequestAborted);
            if (detail is null) return NotFound(http, id);
            http.Response.Headers.ETag = $"\"{detail.Item.Version}\"";
            return Results.Ok(detail);
        }).RequirePermission(permission).AddEndpointFilter<CsrfFilter>().AddEndpointFilter<IfMatchFilter>().AddEndpointFilter<IdempotencyFilter>().WithName(name);
    }

    private static T? Read<T>(JsonElement? body) where T : class => body is { ValueKind: JsonValueKind.Object } e ? e.Deserialize<T>(JsonDefaults.Stored) : null;

    private static JsonElement? Param(JsonElement? prm, string name)
        => prm is { ValueKind: JsonValueKind.Object } e && e.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? v : null;

    internal static IResult NotFound(HttpContext http, Guid id) => Problems.Result(http, 404, "not-found", "Episode not found", $"No episode {id} is visible to you.");

    internal static (int Status, string Code, string Title) Classify(Exception ex) => ex switch
    {
        ForbiddenException => (403, "forbidden", "Forbidden"),
        OutOfScopeException => (404, "not-found", "Not found"),
        VersionConflictException => (409, "version-conflict", "Version conflict"),
        InvalidEpisodeTransitionException => (409, "invalid-transition", "Invalid transition"),
        KeyNotFoundException => (404, "not-found", "Not found"),
        ArgumentException => (400, "validation", "Validation failed"),
        _ => (500, "internal", "Internal error"),
    };
}

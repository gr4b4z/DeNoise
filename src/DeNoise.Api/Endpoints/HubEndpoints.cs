using DeNoise.Api.Auth;
using DeNoise.Application.Ops;
using DeNoise.Application.Retention;
using DeNoise.Contracts;
using DeNoise.Domain.Users;
using DeNoise.Infrastructure.ReadModels;

namespace DeNoise.Api.Endpoints;

/// <summary>Hub health and the failure queue (06 §4 "hub health", spec §13.7).</summary>
public static class HubEndpoints
{
    public static IEndpointRouteBuilder MapHub(this IEndpointRouteBuilder app)
    {
        var hub = app.MapGroup("/api/v1/hub").WithTags("Hub").RequireAuthorization().AddEndpointFilter<MustChangePasswordFilter>();

        hub.MapGet("/health", async (HttpContext http, HealthQueries queries) => Results.Ok(await queries.HubAsync(http.RequestAborted)))
            .RequirePermission(Permissions.HubHealthRead).WithName("GetHubHealth").Produces<HubHealth>();

        hub.MapGet("/failures", async (int? limit, HttpContext http, HealthQueries queries) => Results.Ok(await queries.FailuresAsync(Math.Clamp(limit ?? 100, 1, 500), http.RequestAborted)))
            .RequirePermission(Permissions.HubAdmin).WithName("ListHubFailures").Produces<List<HubFailure>>();

        hub.MapPost("/failures/{id:guid}/retry", async (Guid id, HttpContext http, IJobQueue jobs, HealthQueries queries, Application.Abstractions.IUnitOfWork uow) =>
        {
            var retried = await jobs.RetryFailedAsync(id, http.RequestAborted) || await queries.RetryOutboxAsync(id, http.RequestAborted);
            if (!retried) return Problems.Result(http, 404, "not-found", "Failure not found", $"No failed job or outbox row {id}.");
            await uow.CommitAsync(http.RequestAborted);
            return Results.NoContent();
        }).RequirePermission(Permissions.HubAdmin).AddEndpointFilter<CsrfFilter>().WithName("RetryHubFailure").Produces(204).ProducesProblem(404);

        // Milestone 11: retention (05 §7, spec §18.2) — what is configured, where the raw boundary is, when it last ran; run on demand.
        hub.MapGet("/retention", async (HttpContext http, IRetentionRunner retention) => Results.Ok(ToDto(await retention.StatusAsync(http.RequestAborted))))
            .RequirePermission(Permissions.HubHealthRead).WithName("GetRetentionStatus").Produces<RetentionStatusDto>();
        hub.MapPost("/retention/run", async (HttpContext http, IRetentionRunner retention) => Results.Ok(ToDto(await retention.RunAsync(http.RequestAborted))))
            .RequirePermission(Permissions.HubAdmin).AddEndpointFilter<CsrfFilter>().WithName("RunRetention").Produces<RetentionReportDto>();

        return app;
    }

    private static RetentionReportDto ToDto(RetentionReport r) => new(r.At, r.RawBoundary, r.DroppedPartitions, r.Deleted, r.DurationMs, r.TotalDeleted);

    private static RetentionStatusDto ToDto(RetentionStatus s)
    {
        var o = s.Options;
        return new RetentionStatusDto(new RetentionSettingsDto(o.RawDays, o.NormalisedClosedDays, o.EpisodeClosedMonths, o.DeliveryAttemptDays, o.AuditMonths, o.SessionExpiredDays, o.LoginAttemptDays, o.BatchSize, o.RunAtUtcHour),
            s.RawBoundary, s.OldestPartition, s.NewestPartition, s.PartitionCount, s.LastRun is null ? null : ToDto(s.LastRun), s.NextRunAt);
    }
}

using AlertHub.Api.Auth;
using AlertHub.Application.Ops;
using AlertHub.Contracts;
using AlertHub.Domain.Users;
using AlertHub.Infrastructure.ReadModels;

namespace AlertHub.Api.Endpoints;

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

        return app;
    }
}

using DeNoise.Api.Auth;
using DeNoise.Application.Environments;
using DeNoise.Contracts;
using DeNoise.Domain.Environments;
using DeNoise.Domain.Users;

namespace DeNoise.Api.Endpoints;

/// <summary>
/// The environment dictionary (ADR-17, spec §15.5). Every signed-in user reads it (the queue's filter chips come from here);
/// changing it is a policy decision, so it needs <c>policy.manage</c>. Keys are immutable; update and delete need <c>If-Match</c>.
/// </summary>
public static class EnvironmentEndpoints
{
    public static IEndpointRouteBuilder MapEnvironments(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/environments").WithTags("Environments").RequireAuthorization().AddEndpointFilter<MustChangePasswordFilter>();

        group.MapGet("", async (HttpContext http, EnvironmentService service) =>
            Results.Ok((await service.ListAsync(http.RequestAborted)).Select(ToDto).ToList()))
            .RequirePermission(Permissions.EpisodeRead).WithName("ListEnvironments").Produces<List<EnvironmentDto>>();

        group.MapPost("", async (CreateEnvironmentRequest request, HttpContext http, EnvironmentService service) =>
        {
            var created = await service.CreateAsync(new CreateEnvironment(request.Key, request.Label, request.SortOrder), Actor(http), http.RequestAborted);
            http.Response.Headers.ETag = $"\"{created.Version}\"";
            return Results.Created($"/api/v1/environments/{created.Key}", ToDto(created));
        }).RequirePermission(Permissions.PolicyManage).AddEndpointFilter<CsrfFilter>().WithName("CreateEnvironment").Produces<EnvironmentDto>(201).ProducesProblem(400).ProducesProblem(409);

        group.MapPut("/{key}", async (string key, UpdateEnvironmentRequest request, HttpContext http, EnvironmentService service) =>
        {
            var updated = await service.UpdateAsync(key, IfMatchFilter.Version(http), new UpdateEnvironment(request.Label, request.SortOrder), Actor(http), http.RequestAborted);
            http.Response.Headers.ETag = $"\"{updated.Version}\"";
            return Results.Ok(ToDto(updated));
        }).RequirePermission(Permissions.PolicyManage).AddEndpointFilter<CsrfFilter>().AddEndpointFilter<IfMatchFilter>().WithName("UpdateEnvironment")
            .Produces<EnvironmentDto>().ProducesProblem(400).Produces(404).ProducesProblem(409).ProducesProblem(428);

        group.MapDelete("/{key}", async (string key, HttpContext http, EnvironmentService service) =>
        {
            await service.DeleteAsync(key, IfMatchFilter.Version(http), Actor(http), http.RequestAborted);
            return Results.NoContent();
        }).RequirePermission(Permissions.PolicyManage).AddEndpointFilter<CsrfFilter>().AddEndpointFilter<IfMatchFilter>().WithName("DeleteEnvironment")
            .Produces(204).Produces(404).ProducesProblem(409).ProducesProblem(428);

        return app;
    }

    private static EnvironmentDto ToDto(EnvironmentClass e)
        => new(e.Key, e.Label, e.SortOrder, e.IsBuiltIn, EnvironmentClasses.DefaultView.Contains(e.Key), e.Version);

    private static Application.Abstractions.Actor Actor(HttpContext http)
    {
        var p = http.Principal();
        return new Application.Abstractions.Actor(Domain.Audit.ActorTypes.User, p.UserId.ToString(), p.Username, http.CorrelationId(), http.ClientIp());
    }
}

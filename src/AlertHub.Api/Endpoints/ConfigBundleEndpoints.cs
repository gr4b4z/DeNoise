using AlertHub.Api.Auth;
using AlertHub.Application.Abstractions;
using AlertHub.Application.Policies;
using AlertHub.Contracts;
using AlertHub.Domain.Users;
using AlertHub.Infrastructure.ReadModels;
using FromQueryAttribute = Microsoft.AspNetCore.Mvc.FromQueryAttribute;

namespace AlertHub.Api.Endpoints;

public sealed record AuditListQuery(
    [property: FromQuery(Name = "targetType")] string? TargetType, [property: FromQuery(Name = "target")] string? Target, [property: FromQuery(Name = "actor")] string? Actor,
    [property: FromQuery(Name = "action")] string? Action, [property: FromQuery(Name = "from")] DateTimeOffset? From, [property: FromQuery(Name = "to")] DateTimeOffset? To,
    [property: FromQuery(Name = "limit")] int? Limit, [property: FromQuery(Name = "cursor")] string? Cursor);

/// <summary>06 §8 config-as-code (<c>/config/export</c>, <c>/config/import</c>) and the audit log (<c>/audit</c>). Import is dry-run by default and never deletes.</summary>
public static class ConfigBundleEndpoints
{
    public static IEndpointRouteBuilder MapConfigBundle(this IEndpointRouteBuilder app)
    {
        var config = app.MapGroup("/api/v1/config").WithTags("Config").RequireAuthorization().AddEndpointFilter<MustChangePasswordFilter>();

        config.MapGet("/export", async (HttpContext http, ConfigBundleService bundles) =>
            Results.Text(await bundles.ExportAsync(http.RequestAborted), "application/yaml"))
            .RequirePermission(Permissions.EpisodeRead).WithName("ExportConfig").Produces(200, contentType: "application/yaml");

        config.MapPost("/import", async (ConfigImportRequest request, HttpContext http, ConfigBundleService bundles) =>
        {
            if (string.IsNullOrWhiteSpace(request.Yaml)) return Problems.Result(http, 400, "validation", "Validation failed", "yaml is required");
            var result = await bundles.ImportAsync(request.Yaml, request.DryRun, Actor(http), http.RequestAborted);
            return Results.Ok(new ConfigImportResponse(result.DryRun, result.Entries.Select(e => new ConfigImportEntryDto(e.Kind, e.PolicyId, e.Name, e.Action, e.Version, e.Changes)).ToList(), result.Errors, result.HasChanges));
        }).RequirePermission(Permissions.PolicyManage).AddEndpointFilter<CsrfFilter>().WithName("ImportConfig").Produces<ConfigImportResponse>().ProducesProblem(400);

        var audit = app.MapGroup("/api/v1/audit").WithTags("Audit").RequireAuthorization().AddEndpointFilter<MustChangePasswordFilter>();
        audit.MapGet("", async ([Microsoft.AspNetCore.Http.AsParameters] AuditListQuery query, HttpContext http, AuditQueries queries) =>
            Results.Ok(await queries.ListAsync(new AuditFilter(query.TargetType, query.Target, query.Actor, query.Action, query.From, query.To, query.Limit ?? 100, query.Cursor), http.RequestAborted)))
            .RequirePermission(Permissions.AuditRead).WithName("ListAudit").Produces<PagedResponse<AuditEntryDto>>();

        return app;
    }

    private static Actor Actor(HttpContext http)
    {
        var p = http.Principal();
        return new Actor(Domain.Audit.ActorTypes.User, p.UserId.ToString(), p.Username, http.CorrelationId(), http.ClientIp());
    }
}

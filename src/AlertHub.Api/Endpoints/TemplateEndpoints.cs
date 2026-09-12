using System.Text.Json.Nodes;
using AlertHub.Api.Auth;
using AlertHub.Application.Abstractions;
using AlertHub.Application.Notifications.Templates;
using AlertHub.Contracts;
using AlertHub.Domain.Notifications;
using AlertHub.Domain.Users;

namespace AlertHub.Api.Endpoints;

/// <summary>Webhook body templates (06 §7): list, versions, create (validated by rendering the sample), preview render, activate.</summary>
public static class TemplateEndpoints
{
    public static IEndpointRouteBuilder MapTemplates(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/webhook-templates").WithTags("Templates").RequireAuthorization().AddEndpointFilter<MustChangePasswordFilter>();

        group.MapGet("", async (HttpContext http, TemplateService service) =>
            Results.Ok((await service.ListActiveAsync(http.RequestAborted)).Select(t => ToDto(t, includeBody: false)).ToList()))
            .RequirePermission(Permissions.EpisodeRead).WithName("ListTemplates").Produces<List<WebhookTemplateDto>>();

        group.MapGet("/{id:guid}", async (Guid id, HttpContext http, TemplateService service) =>
        {
            var versions = await service.ListVersionsAsync(id, http.RequestAborted);
            return versions.Count == 0 ? Results.NotFound() : Results.Ok(versions.Select(t => ToDto(t, includeBody: true)).ToList());
        }).RequirePermission(Permissions.EpisodeRead).WithName("ListTemplateVersions").Produces<List<WebhookTemplateDto>>().Produces(404);

        group.MapGet("/{id:guid}/{version:int}", async (Guid id, int version, HttpContext http, TemplateService service) =>
        {
            var t = await service.GetAsync(id, version, http.RequestAborted);
            return t is null ? Results.NotFound() : Results.Ok(ToDto(t, includeBody: true));
        }).RequirePermission(Permissions.EpisodeRead).WithName("GetTemplateVersion").Produces<WebhookTemplateDto>().Produces(404);

        group.MapPost("", async (CreateTemplateRequest request, HttpContext http, TemplateService service) =>
        {
            var created = await service.CreateVersionAsync(new CreateTemplateVersion(request.Name, request.Format, request.Body, request.ContentType, request.Description, request.TemplateId, Sample(request.SampleEvent)), Actor(http), http.RequestAborted);
            return Results.Created($"/api/v1/webhook-templates/{created.TemplateId}/{created.Version}", ToDto(created, includeBody: true));
        }).RequirePermission(Permissions.DestinationManage).AddEndpointFilter<CsrfFilter>().WithName("CreateTemplateVersion").Produces<WebhookTemplateDto>(201).ProducesProblem(400).ProducesProblem(409);

        group.MapPost("/render", async (RenderDraftRequest request, HttpContext http, TemplateService service) =>
        {
            var r = await service.RenderDraftAsync(request.Format, request.Body, request.ContentType, Sample(request.SampleEvent), http.RequestAborted);
            return Results.Ok(new RenderedTemplateResponse(r.Body, r.ContentType));
        }).RequirePermission(Permissions.DestinationManage).AddEndpointFilter<CsrfFilter>().WithName("RenderTemplateDraft").Produces<RenderedTemplateResponse>().ProducesProblem(400);

        group.MapPost("/{id:guid}/{version:int}/render", async (Guid id, int version, RenderTemplateRequest request, HttpContext http, TemplateService service) =>
        {
            var r = await service.RenderPreviewAsync(id, version, Sample(request.SampleEvent), request.EpisodeId, http.RequestAborted);
            return Results.Ok(new RenderedTemplateResponse(r.Body, r.ContentType));
        }).RequirePermission(Permissions.EpisodeRead).AddEndpointFilter<CsrfFilter>().WithName("RenderTemplateVersion").Produces<RenderedTemplateResponse>().ProducesProblem(400).ProducesProblem(404);

        group.MapPost("/{id:guid}/{version:int}/activate", async (Guid id, int version, HttpContext http, TemplateService service) =>
        {
            await service.ActivateAsync(id, version, Actor(http), http.RequestAborted);
            return Results.NoContent();
        }).RequirePermission(Permissions.DestinationManage).AddEndpointFilter<CsrfFilter>().WithName("ActivateTemplateVersion").Produces(204).ProducesProblem(400).ProducesProblem(404);

        return app;
    }

    private static JsonObject? Sample(System.Text.Json.JsonElement? element)
        => element is { ValueKind: System.Text.Json.JsonValueKind.Object } e ? JsonNode.Parse(e.GetRawText()) as JsonObject : null;

    private static WebhookTemplateDto ToDto(WebhookTemplate t, bool includeBody)
        => new(t.TemplateId, t.Version, t.Name, t.Description, t.Format, t.ContentType, t.Builtin, t.IsActive, t.ActivatedAt, t.DeactivatedAt, t.CreatedBy, t.CreatedAt, includeBody ? t.Body : null, includeBody ? t.SampleOutput : null);

    private static Actor Actor(HttpContext http)
    {
        var p = http.Principal();
        return new Actor(Domain.Audit.ActorTypes.User, p.UserId.ToString(), p.Username, http.CorrelationId(), http.ClientIp());
    }
}

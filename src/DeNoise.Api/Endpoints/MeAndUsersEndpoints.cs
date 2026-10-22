using DeNoise.Api.Auth;
using DeNoise.Application.Auth;
using DeNoise.Application.Teams;
using DeNoise.Contracts;
using DeNoise.Domain.Common;
using DeNoise.Domain.Users;

namespace DeNoise.Api.Endpoints;

public static class MeAndUsersEndpoints
{
    public static IEndpointRouteBuilder MapMeAndUsers(this IEndpointRouteBuilder app)
    {
        var me = app.MapGroup("/api/v1/me").WithTags("Me").RequireAuthorization();

        me.MapGet("", async (HttpContext http, ITeamMemberRepository members, ITeamRepository teams, IUserRepository users) =>
        {
            var p = http.Principal();
            var user = await users.GetAsync(p.UserId, http.RequestAborted);
            var memberships = await members.ListForUserAsync(p.UserId, http.RequestAborted);
            var refs = new List<TeamRef>();
            foreach (var m in memberships)
            {
                var t = await teams.GetAsync(m.TeamId, http.RequestAborted);
                if (t is not null) refs.Add(new TeamRef(t.TeamId, t.Name));
            }
            return Results.Ok(new MeResponse(p.UserId, p.Username, p.DisplayName, user?.Email, p.Roles, p.Scopes, p.Permissions.Order().ToList(), refs, p.MustChangePassword, p.Credential.ToString().ToLowerInvariant()));
        }).WithName("GetMe").Produces<MeResponse>();

        me.MapGet("/tokens", async (HttpContext http, PersonalAccessTokenService tokens) =>
            Results.Ok((await tokens.ListAsync(http.Principal().UserId, http.RequestAborted)).Select(ToSummary).ToList()))
            .RequirePermission(Permissions.MeTokens).AddEndpointFilter<MustChangePasswordFilter>().WithName("ListMyTokens").Produces<List<TokenSummary>>();

        me.MapPost("/tokens", async (CreateTokenRequest request, HttpContext http, PersonalAccessTokenService tokens) =>
        {
            if (string.IsNullOrWhiteSpace(request.Name)) return Problems.Result(http, 400, "validation", "Name is required", null);
            var (token, plaintext) = await tokens.CreateAsync(http.Principal(), request.Name, request.Scopes ?? [], request.ExpiresAt, http.CorrelationId(), http.RequestAborted);
            return Results.Created($"/api/v1/me/tokens/{token.TokenId}", new TokenCreatedResponse(ToSummary(token), plaintext));
        }).RequirePermission(Permissions.MeTokens).AddEndpointFilter<CsrfFilter>().AddEndpointFilter<MustChangePasswordFilter>().WithName("CreateMyToken").Produces<TokenCreatedResponse>(201).ProducesProblem(400);

        me.MapDelete("/tokens/{id:guid}", async (Guid id, HttpContext http, PersonalAccessTokenService tokens) =>
            await tokens.RevokeAsync(http.Principal(), id, http.CorrelationId(), http.RequestAborted) ? Results.NoContent() : Results.NotFound())
            .RequirePermission(Permissions.MeTokens).AddEndpointFilter<CsrfFilter>().AddEndpointFilter<MustChangePasswordFilter>().WithName("RevokeMyToken").Produces(204).Produces(404);

        me.MapGet("/sessions", async (HttpContext http, AuthService auth) =>
        {
            var p = http.Principal();
            var sessions = await auth.ListSessionsAsync(p.UserId, http.RequestAborted);
            return Results.Ok(sessions.Select(s => new SessionSummary(s.SessionId[..12], s.CreatedAt, s.LastSeenAt, s.ExpiresAt, s.Ip?.ToString(), s.UserAgent, s.SessionId == p.SessionId)).ToList());
        }).RequirePermission(Permissions.MeSessions).WithName("ListMySessions").Produces<List<SessionSummary>>();

        me.MapDelete("/sessions/{id}", async (string id, HttpContext http, AuthService auth) =>
        {
            var p = http.Principal();
            var sessions = await auth.ListSessionsAsync(p.UserId, http.RequestAborted);
            var target = sessions.FirstOrDefault(s => s.SessionId.StartsWith(id, StringComparison.Ordinal));
            return target is not null && await auth.RevokeSessionAsync(p.UserId, target.SessionId, http.CorrelationId(), http.RequestAborted) ? Results.NoContent() : Results.NotFound();
        }).RequirePermission(Permissions.MeSessions).AddEndpointFilter<CsrfFilter>().WithName("RevokeMySession").Produces(204).Produces(404);

        me.MapGet("/filters", async (HttpContext http, ISavedFilterRepository filters) =>
            Results.Ok((await filters.ListForUserAsync(http.Principal().UserId, http.RequestAborted)).Select(f => new SavedFilterDto(f.FilterId, f.Name, f.Query)).ToList()))
            .AddEndpointFilter<MustChangePasswordFilter>().WithName("ListMyFilters").Produces<List<SavedFilterDto>>();

        me.MapPost("/filters", async (SaveFilterRequest request, HttpContext http, ISavedFilterRepository filters, Application.Abstractions.IUnitOfWork uow, TimeProvider time) =>
        {
            if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 80) return Problems.Result(http, 400, "validation", "Name is required (≤ 80 chars)", null);
            var filter = new SavedFilter { FilterId = Ids.New(time), UserId = http.Principal().UserId, Name = request.Name.Trim(), Query = request.Query ?? string.Empty, CreatedAt = time.GetUtcNow() };
            filters.Add(filter);
            await uow.CommitAsync(http.RequestAborted);
            return Results.Created($"/api/v1/me/filters/{filter.FilterId}", new SavedFilterDto(filter.FilterId, filter.Name, filter.Query));
        }).AddEndpointFilter<CsrfFilter>().AddEndpointFilter<MustChangePasswordFilter>().WithName("SaveMyFilter").Produces<SavedFilterDto>(201);

        me.MapDelete("/filters/{id:guid}", async (Guid id, HttpContext http, ISavedFilterRepository filters, Application.Abstractions.IUnitOfWork uow) =>
        {
            var filter = await filters.GetAsync(id, http.RequestAborted);
            if (filter is null || filter.UserId != http.Principal().UserId) return Results.NotFound();
            filters.Remove(filter);
            await uow.CommitAsync(http.RequestAborted);
            return Results.NoContent();
        }).AddEndpointFilter<CsrfFilter>().AddEndpointFilter<MustChangePasswordFilter>().WithName("DeleteMyFilter").Produces(204).Produces(404);

        var users = app.MapGroup("/api/v1/users").WithTags("Users").RequireAuthorization().AddEndpointFilter<MustChangePasswordFilter>();

        // Directory lookup for assign (any authenticated user, minimal fields); full admin listing needs user.manage.
        users.MapGet("", async (string? q, int? limit, HttpContext http, UserService service) =>
        {
            var p = http.Principal();
            var list = await service.ListAsync(q, limit ?? 50, http.RequestAborted);
            return p.Has(Permissions.UserManage)
                ? Results.Ok(list.Select(u => (object)ToSummary(u, http)).ToList())
                : Results.Ok(list.Where(u => !u.Disabled).Select(u => (object)new UserRef(u.UserId, u.Username, u.DisplayName)).ToList());
        }).WithName("ListUsers").Produces<List<UserSummary>>();

        users.MapPost("", async (CreateUserRequest request, HttpContext http, UserService service) =>
        {
            var (user, temporary) = await service.CreateAsync(new CreateUser(request.Username, request.DisplayName, request.Email, request.Roles, request.Scopes), http.Principal(), http.CorrelationId(), http.RequestAborted);
            return Results.Created($"/api/v1/users/{user.UserId}", new TemporaryPasswordResponse(ToSummary(user, http), temporary));
        }).RequirePermission(Permissions.UserManage).AddEndpointFilter<CsrfFilter>().WithName("CreateUser").Produces<TemporaryPasswordResponse>(201).ProducesProblem(400).ProducesProblem(409);

        users.MapGet("/{id:guid}", async (Guid id, HttpContext http, UserService service) =>
        {
            var user = await service.GetAsync(id, http.RequestAborted);
            return user is null ? Results.NotFound() : Results.Ok(ToSummary(user, http));
        }).RequirePermission(Permissions.UserManage).WithName("GetUser").Produces<UserSummary>().Produces(404);

        users.MapPut("/{id:guid}", async (Guid id, UpdateUserRequest request, HttpContext http, UserService service) =>
            Results.Ok(ToSummary(await service.UpdateAsync(id, new UpdateUser(request.DisplayName, request.Email, request.Roles, request.Scopes), http.Principal(), http.CorrelationId(), http.RequestAborted), http)))
            .RequirePermission(Permissions.UserManage).AddEndpointFilter<CsrfFilter>().WithName("UpdateUser").Produces<UserSummary>().ProducesProblem(400);

        users.MapPost("/{id:guid}/reset-password", async (Guid id, HttpContext http, UserService service) =>
        {
            var temporary = await service.ResetPasswordAsync(id, http.Principal(), http.CorrelationId(), http.RequestAborted);
            var user = await service.GetAsync(id, http.RequestAborted);
            return Results.Ok(new TemporaryPasswordResponse(ToSummary(user!, http), temporary));
        }).RequirePermission(Permissions.UserManage).AddEndpointFilter<CsrfFilter>().WithName("ResetUserPassword").Produces<TemporaryPasswordResponse>();

        users.MapPost("/{id:guid}/disable", async (Guid id, HttpContext http, UserService service) => { await service.SetDisabledAsync(id, true, http.Principal(), http.CorrelationId(), http.RequestAborted); return Results.NoContent(); })
            .RequirePermission(Permissions.UserManage).AddEndpointFilter<CsrfFilter>().WithName("DisableUser").Produces(204);
        users.MapPost("/{id:guid}/enable", async (Guid id, HttpContext http, UserService service) => { await service.SetDisabledAsync(id, false, http.Principal(), http.CorrelationId(), http.RequestAborted); return Results.NoContent(); })
            .RequirePermission(Permissions.UserManage).AddEndpointFilter<CsrfFilter>().WithName("EnableUser").Produces(204);
        users.MapPost("/{id:guid}/unlock", async (Guid id, HttpContext http, UserService service) => { await service.UnlockAsync(id, http.Principal(), http.CorrelationId(), http.RequestAborted); return Results.NoContent(); })
            .RequirePermission(Permissions.UserManage).AddEndpointFilter<CsrfFilter>().WithName("UnlockUser").Produces(204);

        return app;
    }

    private static TokenSummary ToSummary(PersonalAccessToken t) => new(t.TokenId, t.Name, t.KeyId, t.Scopes, t.ExpiresAt, t.LastUsedAt, t.CreatedAt);

    private static UserSummary ToSummary(User u, HttpContext http)
    {
        var now = http.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow();
        return new UserSummary(u.UserId, u.Username, u.DisplayName, u.Email, u.RoleNames, u.Scopes, u.AuthProvider, u.Disabled, u.IsLocked(now), u.MustChangePassword, u.LastLoginAt, u.CreatedAt, u.Version);
    }
}

using System.Threading.RateLimiting;
using AlertHub.Api.Auth;
using AlertHub.Api.Endpoints;
using AlertHub.Application.Abstractions;
using AlertHub.Application.Auth;
using AlertHub.Application.Episodes;
using AlertHub.Application.Mapping;
using AlertHub.Infrastructure;
using AlertHub.Infrastructure.Health;
using AlertHub.Infrastructure.Hosting;
using AlertHub.Infrastructure.Observability;
using AlertHub.Infrastructure.Realtime;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.OpenApi;

var builder = WebApplication.CreateBuilder(args);
builder.AddAlertHubCore("api");
builder.Services.AddAlertHubInfrastructure(builder.Configuration);
builder.Services.AddHealthChecks().AddCheck<OutboxLagHealthCheck>("outbox-lag", tags: [HealthEndpoints.ReadyTag]);
builder.Services.AddHostedService<PgChangeListener>();
builder.Services.AddSingleton<CsrfTokens>();
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
    // Strict numbers: otherwise the OpenAPI document types every integer as ["integer","string"] and the generated TypeScript client loses `number`.
    o.SerializerOptions.NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.Strict;
    o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter(System.Text.Json.JsonNamingPolicy.CamelCase));
});
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    o.KnownIPNetworks.Clear();
    o.KnownProxies.Clear();
});

builder.Services.AddAuthentication(AlertHubAuthenticationHandler.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, AlertHubAuthenticationHandler>(AlertHubAuthenticationHandler.SchemeName, null);
builder.Services.AddAuthorization();

// 06 §1: 600 req/min per user (bearer/cookie identity, else IP); 06 §4: 10 login attempts / min / IP.
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.OnRejected = (ctx, _) =>
    {
        ctx.HttpContext.Response.Headers.RetryAfter = ctx.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retry) ? ((int)Math.Ceiling(retry.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture) : "10";
        return ValueTask.CompletedTask;
    };
    o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
    {
        if (!ctx.Request.Path.StartsWithSegments("/api")) return RateLimitPartition.GetNoLimiter("none");
        var key = ctx.Request.Cookies[AlertHubAuthenticationHandler.CookieName] ?? ctx.Request.Headers.Authorization.ToString();
        key = key.Length == 0 ? "ip:" + ctx.ClientIp() : "cred:" + key.GetHashCode(StringComparison.Ordinal);
        var perMinute = ctx.RequestServices.GetRequiredService<IConfiguration>().GetValue("Limits:ApiPerMinutePerUser", 600);
        return RateLimitPartition.GetSlidingWindowLimiter(key, _ => new SlidingWindowRateLimiterOptions { PermitLimit = perMinute, Window = TimeSpan.FromMinutes(1), SegmentsPerWindow = 6, QueueLimit = 0 });
    });
    o.AddPolicy("login", ctx => RateLimitPartition.GetSlidingWindowLimiter("login:" + ctx.ClientIp(), _ => new SlidingWindowRateLimiterOptions
    {
        PermitLimit = ctx.RequestServices.GetRequiredService<IConfiguration>().GetValue("Limits:LoginPerMinutePerIp", 10),
        Window = TimeSpan.FromMinutes(1),
        SegmentsPerWindow = 6,
        QueueLimit = 0,
    }));
});

builder.Services.AddOpenApi("v1", o =>
{
    o.AddDocumentTransformer((doc, _, _) =>
    {
        doc.Info.Title = "Alert Hub application API";
        doc.Info.Version = "v1";
        doc.Info.Description = "Authenticated operator API (docs/06-api-contract.md). Session cookie for the UI or `Authorization: Bearer ah_pat_<keyId>.<secret>` for API clients.";
        doc.Components ??= new OpenApiComponents();
        doc.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        doc.Components.SecuritySchemes["session"] = new OpenApiSecurityScheme { Type = SecuritySchemeType.ApiKey, In = ParameterLocation.Cookie, Name = AlertHubAuthenticationHandler.CookieName, Description = "Server-side session cookie set by POST /auth/login." };
        doc.Components.SecuritySchemes["pat"] = new OpenApiSecurityScheme { Type = SecuritySchemeType.Http, Scheme = "bearer", Description = "Personal access token ah_pat_<keyId>.<secret>." };
        return Task.CompletedTask;
    });
    o.AddOperationTransformer((op, ctx, _) =>
    {
        var path = ctx.Description.RelativePath ?? string.Empty;
        var anonymous = ctx.Description.ActionDescriptor.EndpointMetadata.OfType<Microsoft.AspNetCore.Authorization.IAllowAnonymous>().Any();
        if (!anonymous && !path.StartsWith("healthz", StringComparison.Ordinal))
        {
            op.Security ??= [];
            op.Security.Add(new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference("session", ctx.Document)] = [] });
            op.Security.Add(new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference("pat", ctx.Document)] = [] });
        }
        return Task.CompletedTask;
    });
});

var app = builder.Build();

app.UseForwardedHeaders();
app.UseAlertHubRequestLogging();
app.Use(async (ctx, next) =>
{
    ctx.Response.OnStarting(() =>
    {
        ctx.Response.Headers["X-Trace-Id"] = ctx.CorrelationId();
        return Task.CompletedTask;
    });
    await next();
});
app.UseExceptionHandler(handler => handler.Run(async ctx =>
{
    var feature = ctx.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>();
    var ex = feature?.Error;
    var (status, code, title) = ex switch
    {
        ForbiddenException => (403, "forbidden", "Forbidden"),
        OutOfScopeException => (404, "not-found", "Not found"),
        VersionConflictException => (409, "version-conflict", "Version conflict"),
        AlertHub.Domain.Episodes.InvalidEpisodeTransitionException => (409, "invalid-transition", "Invalid transition"),
        MappingValidationException => (400, "validation", "Validation failed"),
        KeyNotFoundException => (404, "not-found", "Not found"),
        InvalidOperationException => (409, "conflict", "Conflict"),
        ArgumentException => (400, "validation", "Validation failed"),
        NotSupportedException => (400, "not-supported", "Not supported"),
        Microsoft.AspNetCore.Http.BadHttpRequestException => (400, "bad-request", "Bad request"),
        _ => (500, "internal", "Internal error"),
    };
    object? extra = ex is MappingValidationException mv ? new { errors = mv.Errors.Select(e => new { path = e.Path, message = e.Message }) } : null;
    var detail = status == 500 ? "An unexpected error occurred; the trace id identifies it in the logs." : ex?.Message;
    if (status == 500) app.Logger.LogError(ex, "Unhandled exception (trace {TraceId})", ctx.CorrelationId());
    await Problems.WriteAsync(ctx, status, code, title, detail, extra);
}));
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapAlertHubHealth();
app.MapOpenApi("/openapi/{documentName}.json").AllowAnonymous();
app.MapAuth();
app.MapMeAndUsers();
app.MapEpisodes();
app.MapConfig();
app.MapHub();
app.MapHeartbeats();
app.MapTemplates();
app.MapMappings();
app.MapSuppressions();
app.MapEvents();

// Bootstrap admin on first start (06 §6): a generated password is printed once to stdout, never logged.
await using (var scope = app.Services.CreateAsyncScope())
{
    try
    {
        var seededTemplates = await scope.ServiceProvider.GetRequiredService<AlertHub.Application.Notifications.Templates.TemplateService>().EnsureBuiltInsAsync();
        if (seededTemplates > 0) app.Logger.LogInformation("Seeded {Count} built-in webhook template(s)", seededTemplates);
        var generated = await scope.ServiceProvider.GetRequiredService<AuthService>().BootstrapAdminIfEmptyAsync();
        if (generated is { Length: > 0 })
        {
            Console.Out.WriteLine($"Bootstrap admin created. Username: admin  Password: {generated}  (change it at first login)");
        }
        else if (generated is not null)
        {
            app.Logger.LogInformation("Bootstrap admin created from Auth:Local:BootstrapPassword; must change password at first login");
        }
    }
    catch (Exception ex)
    {
        // The database may not be reachable yet; readiness reports it and the next start retries the bootstrap.
        app.Logger.LogWarning(ex, "Bootstrap admin check skipped: database unavailable at startup");
    }
}

app.Run();

namespace AlertHub.Api
{
    /// <summary>Assembly marker for <c>WebApplicationFactory</c> in tests.</summary>
    public sealed class ApiHost;
}

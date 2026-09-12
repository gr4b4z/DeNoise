using AlertHub.Application.Abstractions;
using AlertHub.Application.Audit;
using AlertHub.Application.Auth;
using AlertHub.Application.Episodes;
using AlertHub.Application.Ingest;
using AlertHub.Application.Integrations;
using AlertHub.Application.Mapping;
using AlertHub.Application.Notifications;
using AlertHub.Application.Ops;
using AlertHub.Application.Policies;
using AlertHub.Application.Processing;
using AlertHub.Application.Realtime;
using AlertHub.Application.Routing;
using AlertHub.Application.Scheduling;
using AlertHub.Application.Teams;
using AlertHub.Infrastructure.Audit;
using AlertHub.Infrastructure.Auth;
using AlertHub.Infrastructure.Ingest;
using AlertHub.Infrastructure.Integrations;
using AlertHub.Infrastructure.Mapping;
using AlertHub.Infrastructure.Notifications;
using AlertHub.Infrastructure.Ops;
using AlertHub.Infrastructure.Persistence;
using AlertHub.Infrastructure.Processing;
using AlertHub.Infrastructure.ReadModels;
using AlertHub.Infrastructure.Realtime;
using AlertHub.Infrastructure.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AlertHub.Infrastructure;

public static class InfrastructureServiceCollectionExtensions
{
    /// <summary>Repositories, unit of work, audit, secret hashing, queue and the application services they back.</summary>
    public static IServiceCollection AddAlertHubInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddMemoryCache();
        services.AddSingleton<ISecretHasher, Argon2SecretHasher>();
        services.AddScoped<IUnitOfWork, EfUnitOfWork>();
        services.AddScoped<IAuditWriter, EfAuditWriter>();
        services.AddScoped<IIntegrationRepository, EfIntegrationRepository>();
        services.AddScoped<IntegrationService>();
        services.AddScoped<IJobQueue, JobQueue>();
        services.AddOptions<JobQueueOptions>().Bind(configuration.GetSection(JobQueueOptions.Section));
        services.AddOptions<IngestOptions>().Bind(configuration.GetSection(IngestOptions.Section));
        services.AddScoped<IIngestStore, EfIngestStore>();
        services.AddScoped<IIngestAuthenticator, IntegrationIngestAuthenticator>();
        services.AddScoped<IngestService>();

        // Processing (milestone 2)
        services.AddScoped<IMappingRepository, EfMappingRepository>();
        services.AddScoped<IMappingResolver, CachedMappingResolver>();
        services.AddScoped<MappingService>();
        services.AddScoped<IRawEventReader, EfRawEventReader>();
        services.AddSingleton<IProcessingUnitOfWork, EfProcessingUnitOfWork>();
        services.TryAddScoped<IEpisodeChangePublisher, NoOpEpisodeChangePublisher>();
        services.AddScoped<EventProcessor>();
        services.AddScoped<IJobHandler, NormaliseJobHandler>();

        // Ownership, routing, notifications (milestone 3)
        services.AddOptions<NotificationOptions>().Bind(configuration.GetSection(NotificationOptions.Section));
        services.AddOptions<SmtpOptions>().Bind(configuration.GetSection(SmtpOptions.Section));
        var keysPath = configuration["DataProtection:KeysPath"];
        var dataProtection = services.AddDataProtection().SetApplicationName("AlertHub");
        if (!string.IsNullOrWhiteSpace(keysPath)) dataProtection.PersistKeysToFileSystem(new DirectoryInfo(keysPath));
        services.AddSingleton<ISecretProtector, DataProtectionSecretProtector>();
        services.AddScoped<ITeamRepository, EfTeamRepository>();
        services.AddScoped<TeamService>();
        services.AddScoped<IDestinationRepository, EfDestinationRepository>();
        services.AddScoped<DestinationService>();
        services.AddScoped<IPolicyRepository, EfPolicyRepository>();
        services.AddSingleton<IPolicyValidator, RoutingPolicyValidator>();
        services.AddSingleton<IPolicyValidator, EscalationPolicyValidator>();
        services.AddScoped<PolicyService>();
        services.AddScoped<ITransitionHook, NotificationTransitionHook>();
        services.AddScoped<EscalationJobHandler>();
        services.AddScoped<IJobHandler, AckDeadlineJobHandler>();
        services.AddScoped<IJobHandler, EscalationStepJobHandler>();

        // Milestone 5: lifecycle timers, coverage state machine, policy impact.
        services.AddOptions<AlertHub.Application.Lifecycle.LifecycleOptions>().Bind(configuration.GetSection(AlertHub.Application.Lifecycle.LifecycleOptions.Section));
        services.AddScoped<AlertHub.Application.Lifecycle.LifecycleScheduler>();
        services.AddScoped<AlertHub.Application.Lifecycle.LifecycleJobHandler>();
        services.AddScoped<IJobHandler, AlertHub.Application.Lifecycle.AutoResolveJobHandler>();
        services.AddScoped<IJobHandler, AlertHub.Application.Lifecycle.VerifyStateJobHandler>();
        services.AddScoped<IJobHandler, AlertHub.Application.Lifecycle.StaleReviewJobHandler>();
        services.AddScoped<IJobHandler, AlertHub.Application.Lifecycle.AdminExpiryJobHandler>();
        services.AddScoped<IJobHandler, AlertHub.Application.Lifecycle.InformationalExpiryJobHandler>();
        services.AddScoped<IPolicyValidator, AlertHub.Application.Lifecycle.LifecyclePolicyValidator>();
        services.TryAddScoped<IStateQueryAdapter, NoStateQueryAdapter>();
        services.AddScoped<AlertHub.Application.Coverage.CoverageEvaluator>();
        services.AddScoped<AlertHub.Application.Coverage.ICoverageSignalSink>(sp => sp.GetRequiredService<AlertHub.Application.Coverage.CoverageEvaluator>());
        services.AddScoped<IPolicyImpactQueries, ReadModels.PolicyImpactQueries>();
        services.AddScoped<ReadModels.HealthQueries>();
        services.AddScoped<PolicyImpactService>();
        services.AddScoped<IPolicyActivationHook, LifecycleActivationHook>();

        // Milestone 6: registered heartbeats.
        services.AddOptions<AlertHub.Application.Heartbeats.HeartbeatOptions>().Bind(configuration.GetSection(AlertHub.Application.Heartbeats.HeartbeatOptions.Section));
        services.AddScoped<AlertHub.Application.Heartbeats.IHeartbeatRepository, Heartbeats.EfHeartbeatRepository>();
        services.AddScoped<AlertHub.Application.Heartbeats.IHeartbeatPingAuthenticator, Heartbeats.HeartbeatPingAuthenticator>();
        services.TryAddScoped<AlertHub.Application.Heartbeats.IMaintenanceWindows, AlertHub.Application.Heartbeats.NoMaintenanceWindows>();
        services.AddScoped<AlertHub.Application.Heartbeats.HeartbeatEffects>();
        services.AddScoped<AlertHub.Application.Heartbeats.HeartbeatPingService>();
        services.AddScoped<AlertHub.Application.Heartbeats.HeartbeatMonitor>();
        services.AddScoped<AlertHub.Application.Heartbeats.HeartbeatService>();
        // The Helm chart exposes the public URLs under AlertHub:* (03 §"Helm values"); honour them when the section-specific keys are absent.
        services.PostConfigure<AlertHub.Application.Heartbeats.HeartbeatOptions>(o =>
        {
            if (configuration["Heartbeats:IngestPublicBaseUrl"] is null && configuration["AlertHub:IngestPublicBaseUrl"] is { Length: > 0 } ingest) o.IngestPublicBaseUrl = ingest;
        });
        services.PostConfigure<NotificationOptions>(o =>
        {
            if (configuration["Notifications:PublicBaseUrl"] is null && configuration["AlertHub:PublicBaseUrl"] is { Length: > 0 } url) o.PublicBaseUrl = url;
        });
        services.AddScoped<IOutboxQueue, EfOutboxQueue>();
        services.AddScoped<IEpisodeReader, EfEpisodeReader>();
        services.AddHttpClient(WebhookChannel.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
        {
            AllowAutoRedirect = false, // ADR-7: redirects are not followed
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(10),
        });
        services.AddScoped<INotificationChannel, WebhookChannel>();
        services.AddScoped<INotificationChannel, SmtpEmailChannel>();
        services.AddScoped<OutboxDispatcher>();
        services.AddSingleton<AlertHub.Application.Notifications.Templates.TemplateRenderer>();
        services.AddScoped<AlertHub.Application.Notifications.Templates.ITemplateRepository, EfTemplateRepository>();
        services.AddScoped<AlertHub.Application.Notifications.Templates.TemplateService>();

        // Users, auth, application API read models, realtime (milestone 4)
        services.AddOptions<LocalAuthOptions>().Bind(configuration.GetSection(LocalAuthOptions.Section));
        services.AddScoped<IUserRepository, EfUserRepository>();
        services.AddScoped<ISessionRepository, EfSessionRepository>();
        services.AddScoped<IPersonalAccessTokenRepository, EfPersonalAccessTokenRepository>();
        services.AddScoped<ILoginAttemptRepository, EfLoginAttemptRepository>();
        services.AddScoped<IIdempotencyStore, EfIdempotencyStore>();
        services.AddScoped<ITeamMemberRepository, EfTeamMemberRepository>();
        services.AddScoped<ISavedFilterRepository, EfSavedFilterRepository>();
        services.AddScoped<IIdentityProvider, LocalPasswordProvider>();
        services.AddScoped<AuthService>();
        services.AddScoped<UserService>();
        services.AddScoped<PersonalAccessTokenService>();
        services.AddScoped<IEpisodeQueries, EpisodeQueries>();
        services.AddScoped<EpisodeActionService>();
        services.AddSingleton<SseHub>();
        services.AddScoped<IChangeBroadcaster, PgChangeBroadcaster>();
        services.Replace(ServiceDescriptor.Scoped<IEpisodeChangePublisher, BroadcastingEpisodeChangePublisher>());
        return services;
    }
}

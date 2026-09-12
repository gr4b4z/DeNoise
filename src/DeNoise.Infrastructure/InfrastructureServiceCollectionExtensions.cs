using DeNoise.Application.Abstractions;
using DeNoise.Application.Audit;
using DeNoise.Application.Auth;
using DeNoise.Application.Episodes;
using DeNoise.Application.Ingest;
using DeNoise.Application.Integrations;
using DeNoise.Application.Mapping;
using DeNoise.Application.Notifications;
using DeNoise.Application.Ops;
using DeNoise.Application.Policies;
using DeNoise.Application.Processing;
using DeNoise.Application.Realtime;
using DeNoise.Application.Routing;
using DeNoise.Application.Scheduling;
using DeNoise.Application.Teams;
using DeNoise.Infrastructure.Audit;
using DeNoise.Infrastructure.Auth;
using DeNoise.Infrastructure.Ingest;
using DeNoise.Infrastructure.Integrations;
using DeNoise.Infrastructure.Mapping;
using DeNoise.Infrastructure.Notifications;
using DeNoise.Infrastructure.Ops;
using DeNoise.Infrastructure.Persistence;
using DeNoise.Infrastructure.Processing;
using DeNoise.Infrastructure.ReadModels;
using DeNoise.Infrastructure.Realtime;
using DeNoise.Infrastructure.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DeNoise.Infrastructure;

public static class InfrastructureServiceCollectionExtensions
{
    /// <summary>Repositories, unit of work, audit, secret hashing, queue and the application services they back.</summary>
    public static IServiceCollection AddDeNoiseInfrastructure(this IServiceCollection services, IConfiguration configuration)
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
        var dataProtection = services.AddDataProtection().SetApplicationName("DeNoise");
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
        services.AddOptions<DeNoise.Application.Lifecycle.LifecycleOptions>().Bind(configuration.GetSection(DeNoise.Application.Lifecycle.LifecycleOptions.Section));
        services.AddScoped<DeNoise.Application.Lifecycle.LifecycleScheduler>();
        services.AddScoped<DeNoise.Application.Lifecycle.LifecycleJobHandler>();
        services.AddScoped<IJobHandler, DeNoise.Application.Lifecycle.AutoResolveJobHandler>();
        services.AddScoped<IJobHandler, DeNoise.Application.Lifecycle.VerifyStateJobHandler>();
        services.AddScoped<IJobHandler, DeNoise.Application.Lifecycle.StaleReviewJobHandler>();
        services.AddScoped<IJobHandler, DeNoise.Application.Lifecycle.AdminExpiryJobHandler>();
        services.AddScoped<IJobHandler, DeNoise.Application.Lifecycle.InformationalExpiryJobHandler>();
        services.AddScoped<IPolicyValidator, DeNoise.Application.Lifecycle.LifecyclePolicyValidator>();
        services.AddSingleton<Integrations.AtlasStateQueryAdapter>();
        services.TryAddScoped<IStateQueryAdapter, Integrations.StateQueryAdapterRouter>();
        services.AddScoped<DeNoise.Application.Replay.IMappingFailureStore, Processing.EfMappingFailureStore>();
        services.AddScoped<DeNoise.Application.Replay.ReplayService>();
        services.AddScoped<IJobHandler, DeNoise.Application.Replay.ReplayJobHandler>();
        services.AddScoped<DeNoise.Application.Mapping.MappingPreviewService>();
        services.AddSingleton<DeNoise.Application.Ingest.IIngestSignatureVerifier, DeNoise.Application.Ingest.IngestSignatureVerifier>();
        services.AddScoped<DeNoise.Application.Coverage.CoverageEvaluator>();
        services.AddScoped<DeNoise.Application.Coverage.ICoverageSignalSink>(sp => sp.GetRequiredService<DeNoise.Application.Coverage.CoverageEvaluator>());
        services.AddScoped<IPolicyImpactQueries, ReadModels.PolicyImpactQueries>();
        services.AddScoped<ReadModels.HealthQueries>();
        services.AddScoped<PolicyImpactService>();
        services.AddScoped<IPolicyActivationHook, LifecycleActivationHook>();

        // Milestone 6: registered heartbeats.
        services.AddOptions<DeNoise.Application.Heartbeats.HeartbeatOptions>().Bind(configuration.GetSection(DeNoise.Application.Heartbeats.HeartbeatOptions.Section));
        services.AddScoped<DeNoise.Application.Heartbeats.IHeartbeatRepository, Heartbeats.EfHeartbeatRepository>();
        services.AddScoped<DeNoise.Application.Heartbeats.IHeartbeatPingAuthenticator, Heartbeats.HeartbeatPingAuthenticator>();
        services.AddScoped<DeNoise.Application.Suppressions.ISuppressionRepository, Policies.EfSuppressionRepository>();
        services.AddScoped<DeNoise.Application.Suppressions.SuppressionService>();
        services.AddScoped<IJobHandler, DeNoise.Application.Suppressions.SuppressionJobHandler>();
        services.TryAddScoped<DeNoise.Application.Heartbeats.IMaintenanceWindows, DeNoise.Application.Suppressions.SuppressionMaintenanceWindows>();
        services.AddScoped<DeNoise.Application.Grouping.GroupingService>();

        // Milestone 10: config-as-code and the audit read model.
        services.AddScoped<ConfigBundleService>();
        services.AddScoped<ReadModels.AuditQueries>();

        // Milestone 11: retention.
        services.AddOptions<DeNoise.Application.Retention.RetentionOptions>().Bind(configuration.GetSection(DeNoise.Application.Retention.RetentionOptions.Section));
        services.AddScoped<DeNoise.Application.Retention.IRetentionRunner, Retention.RetentionService>();

        // Milestone 12: shadow-mode divergence report.
        services.AddOptions<DeNoise.Application.Divergence.PilotOptions>().Bind(configuration.GetSection(DeNoise.Application.Divergence.PilotOptions.Section));
        services.AddScoped<DeNoise.Application.Divergence.IDivergenceReporter, Integrations.DivergenceReporter>();
        services.AddSingleton<IPolicyValidator, DeNoise.Application.Grouping.GroupingPolicyValidator>();
        services.AddScoped<IJobHandler, DeNoise.Application.Grouping.GroupWindowCloseJobHandler>();
        services.AddScoped<DeNoise.Application.Heartbeats.HeartbeatEffects>();
        services.AddScoped<DeNoise.Application.Heartbeats.HeartbeatPingService>();
        services.AddScoped<DeNoise.Application.Heartbeats.HeartbeatMonitor>();
        services.AddScoped<DeNoise.Application.Heartbeats.HeartbeatService>();
        // The Helm chart exposes the public URLs under DeNoise:* (03 §"Helm values"); honour them when the section-specific keys are absent.
        services.PostConfigure<DeNoise.Application.Heartbeats.HeartbeatOptions>(o =>
        {
            if (configuration["Heartbeats:IngestPublicBaseUrl"] is null && configuration["DeNoise:IngestPublicBaseUrl"] is { Length: > 0 } ingest) o.IngestPublicBaseUrl = ingest;
        });
        services.PostConfigure<NotificationOptions>(o =>
        {
            if (configuration["Notifications:PublicBaseUrl"] is null && configuration["DeNoise:PublicBaseUrl"] is { Length: > 0 } url) o.PublicBaseUrl = url;
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
        services.AddSingleton<DeNoise.Application.Notifications.Templates.TemplateRenderer>();
        services.AddScoped<DeNoise.Application.Notifications.Templates.ITemplateRepository, EfTemplateRepository>();
        services.AddScoped<DeNoise.Application.Notifications.Templates.TemplateService>();

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

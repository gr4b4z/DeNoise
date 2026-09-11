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

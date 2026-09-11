using AlertHub.Application.Abstractions;
using AlertHub.Application.Audit;
using AlertHub.Application.Ingest;
using AlertHub.Application.Integrations;
using AlertHub.Application.Mapping;
using AlertHub.Application.Ops;
using AlertHub.Application.Processing;
using AlertHub.Infrastructure.Audit;
using AlertHub.Infrastructure.Ingest;
using AlertHub.Infrastructure.Integrations;
using AlertHub.Infrastructure.Mapping;
using AlertHub.Infrastructure.Ops;
using AlertHub.Infrastructure.Persistence;
using AlertHub.Infrastructure.Processing;
using AlertHub.Infrastructure.Security;
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
        services.TryAddScoped<ITransitionHook, NoOpTransitionHook>();
        services.TryAddScoped<IEpisodeChangePublisher, NoOpEpisodeChangePublisher>();
        services.AddScoped<EventProcessor>();
        services.AddScoped<IJobHandler, NormaliseJobHandler>();
        return services;
    }
}

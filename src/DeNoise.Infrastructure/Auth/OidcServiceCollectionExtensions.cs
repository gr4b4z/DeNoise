using DeNoise.Application.Auth;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace DeNoise.Infrastructure.Auth;

public static class OidcServiceCollectionExtensions
{
    /// <summary>
    /// OIDC sign-in (M11b) for the Api host only: options from <c>Auth:Oidc</c>, validated at start-up when enabled, the
    /// provider, its metadata cache and the sign-in service. With <c>Auth:Oidc:Enabled</c> false or unset nothing is contacted.
    /// </summary>
    public static IServiceCollection AddDeNoiseOidc(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<OidcAuthOptions>().Bind(configuration.GetSection(OidcAuthOptions.Section)).ValidateOnStart();
        services.AddSingleton<IValidateOptions<OidcAuthOptions>, OidcAuthOptionsValidator>();
        services.AddHttpClient(OidcIdentityProvider.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(15));
        services.AddSingleton<OidcMetadataSource>();
        services.TryAddSingleton<IClientAssertionProvider, ManagedIdentityClientAssertionProvider>();
        services.AddSingleton<IExternalIdentityProvider, OidcIdentityProvider>();
        services.AddScoped<ExternalSignInService>();
        return services;
    }

    private sealed class OidcAuthOptionsValidator : IValidateOptions<OidcAuthOptions>
    {
        public ValidateOptionsResult Validate(string? name, OidcAuthOptions options)
        {
            var problems = options.Validate();
            return problems.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(problems);
        }
    }
}

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AlertHub.Integration.Tests.Support;

/// <summary>In-process host over a per-test database, with the clock replaced by <see cref="FakeTimeProvider"/> when supplied.</summary>
public sealed class HostFactory<TEntryPoint>(string connectionString, TimeProvider? time = null, Action<IServiceCollection>? configure = null)
    : WebApplicationFactory<TEntryPoint> where TEntryPoint : class
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:AlertHub", connectionString);
        builder.ConfigureServices(services =>
        {
            if (time is not null)
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton(time);
            }
            configure?.Invoke(services);
        });
    }
}

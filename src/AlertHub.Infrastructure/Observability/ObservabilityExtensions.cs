using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Compact;

namespace AlertHub.Infrastructure.Observability;

public static class ObservabilityExtensions
{
    /// <summary>
    /// Serilog → console JSON, OpenTelemetry traces + metrics with the OTLP exporter when
    /// <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> (or <c>Otel:Endpoint</c>) is configured (ADR-9).
    /// </summary>
    public static IHostApplicationBuilder AddAlertHubObservability(this IHostApplicationBuilder builder, string serviceName)
    {
        var version = typeof(ObservabilityExtensions).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        var instance = Environment.GetEnvironmentVariable("HOSTNAME") ?? Environment.MachineName;

        builder.Services.AddSerilog((_, cfg) => cfg
            .ReadFrom.Configuration(builder.Configuration)
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.EntityFrameworkCore.Database.Command", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .Enrich.WithProperty("service", serviceName)
            .Enrich.WithProperty("instance", instance)
            .WriteTo.Console(new CompactJsonFormatter()));

        builder.Services.AddSingleton<AlertHubMetrics>();

        var otlpEndpoint = builder.Configuration["Otel:Endpoint"] ?? builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];
        var otel = builder.Services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService(serviceName, serviceVersion: version, serviceInstanceId: instance))
            .WithTracing(t =>
            {
                t.AddAspNetCoreInstrumentation(o => o.Filter = ctx => !ctx.Request.Path.StartsWithSegments("/healthz"))
                 .AddHttpClientInstrumentation()
                 .AddNpgsql();
                if (otlpEndpoint is not null) t.AddOtlpExporter(o => o.Endpoint = new Uri(otlpEndpoint));
            })
            .WithMetrics(m =>
            {
                m.AddMeter(AlertHubMetrics.MeterName)
                 .AddAspNetCoreInstrumentation()
                 .AddHttpClientInstrumentation()
                 .AddRuntimeInstrumentation()
                 .AddMeter("Npgsql");
                if (otlpEndpoint is not null) m.AddOtlpExporter(o => o.Endpoint = new Uri(otlpEndpoint));
            });
        _ = otel;
        return builder;
    }

    public static WebApplication UseAlertHubRequestLogging(this WebApplication app)
    {
        app.UseSerilogRequestLogging(o =>
        {
            o.GetLevel = (ctx, _, ex) => ex is not null || ctx.Response.StatusCode >= 500 ? LogEventLevel.Error
                : ctx.Request.Path.StartsWithSegments("/healthz") ? LogEventLevel.Verbose
                : LogEventLevel.Information;
            o.EnrichDiagnosticContext = (dc, ctx) => dc.Set("TraceId", System.Diagnostics.Activity.Current?.TraceId.ToString() ?? ctx.TraceIdentifier);
        });
        return app;
    }
}

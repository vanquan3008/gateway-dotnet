using Core.Gateway.Api.Authentication.Metrics;
using Core.Gateway.Api.Errors;
using Core.Gateway.Api.RateLimiting.Distributed;
using Core.Gateway.Api.RateLimiting.Local;
using Core.Gateway.Api.RateLimiting.Shared;
using Core.Gateway.Api.Security.Waf;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Core.Gateway.Api.Observability;

public static class GatewayObservabilityExtensions
{
    /// <summary>
    /// Registers OpenTelemetry tracing and metrics, with an OTLP exporter when enabled.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">The application configuration.</param>
    /// <returns>The same service collection.</returns>
    public static IServiceCollection AddGatewayObservability(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var section = configuration.GetSection(GatewayObservabilityOptions.SectionName);
        var settings = section.Get<GatewayObservabilityOptions>() ?? new();

        services.AddOptions<GatewayObservabilityOptions>()
            .Bind(section)
            .ValidateDataAnnotations()
            .Validate(options => !options.OtlpExporterEnabled ||
                    (Uri.TryCreate(options.OtlpEndpoint, UriKind.Absolute, out var endpoint) &&
                    (endpoint.Scheme == Uri.UriSchemeHttp || endpoint.Scheme == Uri.UriSchemeHttps)),
                    "Observability:OtlpEndpoint must be an HTTP(S) endpoint when export is enabled.")
            .ValidateOnStart();

        var openTelemetry = services
            .AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(settings.ServiceName))
            .WithTracing(tracing => tracing
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation());

        openTelemetry.WithMetrics(metrics =>
        {
            metrics.AddAspNetCoreInstrumentation();
            metrics.AddHttpClientInstrumentation();
            metrics.AddRuntimeInstrumentation();

            metrics.AddMeter(
                "Microsoft.AspNetCore.RateLimiting",
                "Microsoft.AspNetCore.Hosting",
                GatewayRateLimitingMetrics.MeterName,
                GatewayConcurrencyMetricNames.MeterName,
                GatewayForwarderErrorMiddleware.MeterName,
                GatewayAuthenticationMetrics.MeterName,
                GatewaySanitizationMiddleware.MeterName);
        });

        if (settings.OtlpExporterEnabled)
        {
            openTelemetry.WithTracing(tracing =>
            {
                tracing.AddOtlpExporter(options =>
                    ConfigureExporter(options, settings));
            });

            openTelemetry.WithMetrics(metrics =>
            {
                metrics.AddOtlpExporter(options =>
                    ConfigureExporter(options, settings));
            });
        }
        return services;
    }

    /// <summary>
    /// Applies timeout, batching and endpoint to an OTLP exporter.
    /// </summary>
    /// <param name="exporter">The exporter options.</param>
    /// <param name="settings">The observability settings.</param>
    private static void ConfigureExporter(
        OtlpExporterOptions exporter,
        GatewayObservabilityOptions settings)
    {
        exporter.TimeoutMilliseconds = settings.OtlpTimeoutMilliseconds;
        exporter.ExportProcessorType = OpenTelemetry.ExportProcessorType.Batch;

        exporter.BatchExportProcessorOptions.MaxQueueSize = 2_048;
        exporter.BatchExportProcessorOptions.MaxExportBatchSize = 512;

        exporter.BatchExportProcessorOptions.ExporterTimeoutMilliseconds = settings.OtlpTimeoutMilliseconds;
        if (settings.OtlpEndpoint is not null)
        {
            exporter.Endpoint = new Uri(settings.OtlpEndpoint);
        }
    }
}

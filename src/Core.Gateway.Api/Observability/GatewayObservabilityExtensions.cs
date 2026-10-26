using Core.Gateway.Api.RateLimiting.Distributed;
using Core.Gateway.Api.RateLimiting.Local;
using Core.Gateway.Api.Resilience;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Core.Gateway.Api.Observability;

public static class GatewayObservabilityExtensions
{
    public static IServiceCollection AddGatewayObservability(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var section = configuration.GetSection(GatewayObservabilityOptions.SectionName);
        var settings = section.Get<GatewayObservabilityOptions>() ?? new();

        services
            .AddOptions<GatewayObservabilityOptions>()
            .Bind(section)
            .ValidateDataAnnotations()
            .Validate(options => !options.OtlpExporterEnabled ||
                Uri.TryCreate(options.OtlpEndpoint, UriKind.Absolute, out var endpoint) &&
                endpoint.Scheme is "http" or "https",
                "Observability:OtlpEndpoint must be an HTTP(S) endpoint when export is enabled.")
            .ValidateOnStart();

        var openTelemetry = services
            .AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(settings.ServiceName))
            .WithTracing(tracing => tracing
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation())
            .WithMetrics(metrics => metrics
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation()
                .AddMeter("Microsoft.AspNetCore.RateLimiting", "Microsoft.AspNetCore.Hosting",
                    DistributedRateLimitingMiddleware.MeterName, GatewayConcurrencyLimiterMiddleware.MeterName,
                    GatewayForwarderErrorMiddleware.MeterName));

        if (settings.OtlpExporterEnabled)
        {
            openTelemetry
                .WithTracing(tracing => tracing.AddOtlpExporter(options => ConfigureExporter(options, settings)))
                .WithMetrics(metrics => metrics.AddOtlpExporter(options => ConfigureExporter(options, settings)));
        }

        return services;
    }

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

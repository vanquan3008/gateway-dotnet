using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Core.Gateway.Api.HealthChecks;

public static class GatewayHealthCheckExtensions
{
    private static readonly string[] LiveTags = ["live"];
    private static readonly string[] ReadyTags = ["ready"];

    public static IServiceCollection AddGatewayHealthChecks(this IServiceCollection services)
    {
        services
            .AddOptions<GatewayHealthCheckOptions>()
            .BindConfiguration(GatewayHealthCheckOptions.SectionName)
            .ValidateDataAnnotations()
            .Validate(
                options => !string.Equals(
                    options.LivenessPath,
                    options.ReadinessPath,
                    StringComparison.OrdinalIgnoreCase),
                "HealthChecks paths must be different.")
            .ValidateOnStart();

        services
            .AddHealthChecks()
            .AddCheck(
                "self",
                () => HealthCheckResult.Healthy(),
                tags: [.. LiveTags, .. ReadyTags])
            .AddCheck<GatewayReadinessHealthCheck>("proxy-config", tags: ReadyTags);

        return services;
    }

    public static IEndpointRouteBuilder MapGatewayHealthChecks(this IEndpointRouteBuilder endpoints)
    {
        var settings = endpoints.ServiceProvider
            .GetRequiredService<IOptions<GatewayHealthCheckOptions>>()
            .Value;

        endpoints
            .MapHealthChecks(settings.LivenessPath, new HealthCheckOptions
            {
                Predicate = registration => registration.Tags.Contains("live"),
                ResponseWriter = GatewayHealthCheckResponseWriter.WriteAsync
            })
            .WithMetadata(new GatewayHealthCheckMetadata())
            .AllowAnonymous()
            .DisableRateLimiting()
            .DisableRequestTimeout();

        endpoints
            .MapHealthChecks(settings.ReadinessPath, new HealthCheckOptions
            {
                Predicate = registration => registration.Tags.Contains("ready"),
                ResponseWriter = GatewayHealthCheckResponseWriter.WriteAsync
            })
            .WithMetadata(new GatewayHealthCheckMetadata())
            .AllowAnonymous()
            .DisableRateLimiting()
            .DisableRequestTimeout();

        return endpoints;
    }
}

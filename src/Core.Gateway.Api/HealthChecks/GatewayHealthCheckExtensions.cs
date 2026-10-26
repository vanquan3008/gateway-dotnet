using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Core.Gateway.Api.HealthChecks;

public static class GatewayHealthCheckExtensions
{
    private static readonly string[] LiveTags = ["live"];
    private static readonly string[] ReadyTags = ["ready"];

    /// <summary>
    /// Registers the liveness and readiness checks and their options.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same service collection.</returns>
    public static IServiceCollection AddGatewayHealthChecks(this IServiceCollection services)
    {
        services.AddOptions<GatewayHealthCheckOptions>()
            .BindConfiguration(GatewayHealthCheckOptions.SectionName)
            .ValidateDataAnnotations()
            .Validate(
                options => !string.Equals(options.LivenessPath, options.ReadinessPath, StringComparison.OrdinalIgnoreCase),
                "HealthChecks paths must be different.")
            .ValidateOnStart();
        services
            .AddHealthChecks()
            .AddCheck("self",
                () => HealthCheckResult.Healthy(),
                tags: [.. LiveTags, .. ReadyTags])
            .AddCheck<GatewayReadinessHealthCheck>("proxy-config", tags: ReadyTags);
        return services;
    }

    /// <summary>
    /// Serves health endpoints only on the management port and everything else only on the other ports (404 otherwise).
    /// </summary>
    /// <param name="app">The application builder.</param>
    /// <returns>The same builder.</returns>
    public static IApplicationBuilder UseGatewayManagementPort(this IApplicationBuilder app)
    {
        var managementPort = app.ApplicationServices
            .GetRequiredService<IOptions<GatewayHealthCheckOptions>>()
            .Value.ManagementPort;
        if (managementPort is null)
        {
            return app;
        }

        return app.Use((context, next) =>
        {
            var isHealthEndpoint = context.GetEndpoint()?.Metadata.GetMetadata<GatewayHealthCheckMetadata>() is not null;
            if (isHealthEndpoint == (context.Connection.LocalPort == managementPort))
            {
                return next(context);
            }

            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return Task.CompletedTask;
        });
    }

    /// <summary>
    /// Maps the live and ready endpoints: anonymous, no rate limit, no timeout.
    /// </summary>
    /// <param name="endpoints">The endpoint builder.</param>
    /// <returns>The same builder.</returns>
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

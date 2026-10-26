using Core.Gateway.Api.Headers;
using Core.Gateway.Api.RateLimiting.Shared;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace Core.Gateway.Api.Security.Cors;

public static class GatewayCorsExtensions
{
    public const string PolicyName = "gateway";

    private static readonly string[] ExposedHeaders =
    [
        GatewayHeaders.CorrelationId,
        HeaderNames.RetryAfter,
        GatewayRateLimitHeaders.Limit,
        GatewayRateLimitHeaders.Remaining,
        GatewayRateLimitHeaders.Reset
    ];

    /// <summary>
    /// Registers the CORS policy from config and exposes the correlation id, Retry-After and rate limit headers.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">The application configuration.</param>
    /// <returns>The same service collection.</returns>
    public static IServiceCollection AddGatewayCors(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var section = configuration.GetSection(GatewayCorsOptions.SectionName);

        services.AddOptions<GatewayCorsOptions>()
            .Bind(section)
            .ValidateDataAnnotations()
            .Validate(o => !o.Enabled || o.AllowedOrigins.Length > 0, "Cors: AllowedOrigins is required when CORS is enabled.")
            .Validate(o => o.AllowedOrigins.All(GatewaySecurityValidators.IsValidCorsOrigin), "Cors: Invalid origin in AllowedOrigins.")
            .Validate(o => !o.Enabled || o.AllowedMethods.Length > 0, "Cors: AllowedMethods is required when CORS is enabled.")
            .Validate(o => !o.Enabled || o.AllowedHeaders.Length > 0, "Cors: AllowedHeaders is required when CORS is enabled.")
            .ValidateOnStart();

        services.AddCors();

        services.AddOptions<CorsOptions>()
            .Configure<IOptions<GatewayCorsOptions>>((options, corsOptions) =>
            {
                var settings = corsOptions.Value;

                options.AddPolicy(PolicyName, policy =>
                {
                    if (!settings.Enabled)
                    {
                        return;
                    }
                    policy.WithOrigins(settings.AllowedOrigins)
                        .WithMethods(settings.AllowedMethods)
                        .WithHeaders(settings.AllowedHeaders)
                        .WithExposedHeaders(ExposedHeaders);
                });
            });

        return services;
    }
}

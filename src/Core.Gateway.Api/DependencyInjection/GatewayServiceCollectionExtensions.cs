using Core.Gateway.Api.Authentication;
using Core.Gateway.Api.Compression;
using Core.Gateway.Api.Errors;
using Core.Gateway.Api.Headers;
using Core.Gateway.Api.HealthChecks;
using Core.Gateway.Api.Observability;
using Core.Gateway.Api.RateLimiting;
using Core.Gateway.Api.Resilience;
using Core.Gateway.Api.ReverseProxy;
using Core.Gateway.Api.Security;
using Core.Gateway.Api.Security.Cors;

namespace Core.Gateway.Api.DependencyInjection;

public static class GatewayServiceCollectionExtensions
{
    /// <summary>
    /// Registers every gateway layer: errors, observability, health, security, CORS, authentication, headers, rate limiting, resilience, compression and the reverse proxy.
    /// </summary>
    /// <param name="builder">The application builder.</param>
    /// <returns>The same builder.</returns>
    public static WebApplicationBuilder AddCoreGateway(this WebApplicationBuilder builder)
    {
        var configuration = builder.Configuration;
        var requireProductionConfiguration = !builder.Environment.IsDevelopment();

        builder.Services.Configure<HostOptions>(options =>
            options.ShutdownTimeout = TimeSpan.FromSeconds(30));

        builder.Services
            .AddGatewayErrorHandling()
            .AddGatewayResponseCompression()
            .AddGatewayObservability(configuration)
            .AddGatewayHealthChecks();

        builder.Services
            .AddGatewaySecurity(configuration, requireProductionConfiguration)
            .AddGatewayCors(configuration)
            .AddGatewayAuthentication(configuration, requireProductionConfiguration)
            .AddGatewayHeaders(configuration);

        builder.Services
            .AddGatewayRateLimiting(configuration, requireProductionConfiguration)
            .AddGatewayResilience(configuration);

        builder.Services
            .AddGatewayReverseProxy(configuration);

        return builder;
    }
}

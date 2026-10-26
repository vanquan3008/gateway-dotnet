using System.Threading.RateLimiting;
using Core.Gateway.Api.RateLimiting.Distributed;
using Core.Gateway.Api.RateLimiting.Distributed.Interfaces;
using Core.Gateway.Api.RateLimiting.Local;
using Core.Gateway.Api.RateLimiting.Options;
using Core.Gateway.Api.RateLimiting.Shared;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace Core.Gateway.Api.RateLimiting;

public static class GatewayRateLimitingExtensions
{
    public static IServiceCollection AddGatewayRateLimiting(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var section = configuration.GetSection(GatewayRateLimitingOptions.SectionName);

        services.AddSingleton<IValidateOptions<GatewayRateLimitingOptions>, GatewayRateLimitingOptionsValidator>();
        services.AddOptions<GatewayRateLimitingOptions>()
            .Bind(section)
            .ValidateDataAnnotations()
            .Validate(_ => new[] { "Instance", "Service", "Anonymous", "Authenticated", "Tenant", "ApiKey",
                "UserIdClaim", "TenantIdClaim", "ApiKeyIdClaim", "InstanceConcurrencyLimit", "ServiceConcurrencyLimit" }
                .All(name => !section.GetSection(name).Exists()),
                "Legacy RateLimiting settings must be moved into Local or Distributed; see docs/rate-limiting.md.")
            .ValidateOnStart();

        services.AddSingleton<RedisDistributedRateLimitStore>();
        services.AddSingleton<GatewayConcurrencyLimiterMiddleware>();
        services.AddSingleton<PartitionedRateLimiter<HttpContext>>(provider =>
            GatewayLocalRateLimiter.Create(provider.GetRequiredService<IOptions<GatewayRateLimitingOptions>>().Value.Local));
        services.AddSingleton<IDistributedRateLimitStore>(provider => provider.GetRequiredService<RedisDistributedRateLimitStore>());
        services.AddHostedService(provider => provider.GetRequiredService<RedisDistributedRateLimitStore>());
        services.AddHealthChecks().AddCheck<DistributedRateLimitingHealthCheck>("rate-limit-store", tags: ["ready"]);
        services.AddRateLimiter(_ => { });
        services.AddOptions<RateLimiterOptions>().Configure<IOptions<GatewayRateLimitingOptions>, IServiceProvider>((options, settings, provider) =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = GatewayRateLimitResponse.WriteRejectionAsync;
            if (settings.Value.Enabled && settings.Value.Local.Enabled)
            {
                options.GlobalLimiter = provider.GetRequiredService<PartitionedRateLimiter<HttpContext>>();
            }
        });

        return services;
    }
}

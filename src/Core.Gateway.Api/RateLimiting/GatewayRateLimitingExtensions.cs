using System.Threading.RateLimiting;
using Core.Gateway.Api.RateLimiting.Distributed;
using Core.Gateway.Api.RateLimiting.Distributed.Fallback;
using Core.Gateway.Api.RateLimiting.Distributed.Store.Interfaces;
using Core.Gateway.Api.RateLimiting.Local;
using Core.Gateway.Api.RateLimiting.Routes;
using Core.Gateway.Api.RateLimiting.Shared;
using Core.Gateway.Api.Redis;
using Core.Gateway.Api.Redis.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using static Core.Gateway.Api.RateLimiting.Shared.GatewayRateLimitingNames;

namespace Core.Gateway.Api.RateLimiting;

public static class GatewayRateLimitingExtensions
{
    /// <summary>
    /// Registers gateway rate limiting: local limits, concurrency bulkhead, Redis shared quotas, fallback and health check.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">Configuration holding the RateLimiting and Redis sections.</param>
    /// <param name="requireProductionConfiguration">True to require Redis whenever shared quotas are on.</param>
    /// <returns>The same service collection.</returns>
    /// <exception cref="OptionsValidationException">The RateLimiting section is invalid (checked on start).</exception>
    public static IServiceCollection AddGatewayRateLimiting(
        this IServiceCollection services,
        IConfiguration configuration,
        bool requireProductionConfiguration = false)
    {
        var section = configuration.GetSection(GatewayRateLimitingOptions.SectionName);

        services.AddSingleton<IValidateOptions<GatewayRateLimitingOptions>, GatewayRateLimitingOptionsValidator>();
        var rateLimiting = services.AddOptions<GatewayRateLimitingOptions>()
            .Bind(section)
            .ValidateDataAnnotations();

        if (requireProductionConfiguration)
        {
            rateLimiting.Validate<IOptions<RedisOptions>>(
                (options, redis) => !options.Enabled || !options.Distributed.Enabled || redis.Value.IsConfigured,
                "RateLimiting:Distributed:Enabled requires Redis:ConnectionString; without it every pod enforces the full quota on its own.");
        }

        rateLimiting.ValidateOnStart();

        services.AddGatewayRedis(configuration);

        services.AddSingleton<RedisDistributedRateLimitStore>();
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<ActiveInstanceCounter>();
        services.AddHostedService<RedisInstanceHeartbeatService>();
        services.AddSingleton<LocalFallbackQuota>();
        services.AddSingleton<GatewayConcurrencyLimiterMiddleware>();
        services.AddSingleton<GatewayAnonymousRateLimitingMiddleware>();
        services.AddSingleton<PartitionedRateLimiter<HttpContext>>(provider =>
            GatewayLocalRateLimiter.Create(provider.GetRequiredService<IOptions<GatewayRateLimitingOptions>>().Value.Local));
        services.AddSingleton<IDistributedRateLimitStore>(provider => provider.GetRequiredService<RedisDistributedRateLimitStore>());
        services.AddHealthChecks().AddCheck<DistributedRateLimitingHealthCheck>(HealthCheck.RateLimitStore, tags: ["ready"]);
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

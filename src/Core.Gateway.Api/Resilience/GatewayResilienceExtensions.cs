using Microsoft.AspNetCore.Http.Timeouts;
using Microsoft.Extensions.Options;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Health;

namespace Core.Gateway.Api.Resilience;

public static class GatewayResilienceExtensions
{
    public const string ApiTimeoutPolicyName = "api";

    public static IServiceCollection AddGatewayResilience(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var section = configuration.GetSection(GatewayResilienceOptions.SectionName);

        services
            .AddOptions<GatewayResilienceOptions>()
            .Bind(section)
            .ValidateDataAnnotations()
            .Validate(
                _ => !section.GetSection("BackendHealthWindowSeconds").Exists(),
                "Legacy Resilience:BackendHealthWindowSeconds must be replaced by BackendHealthDetectionWindowSeconds and BackendHealthReactivationSeconds.")
            .ValidateOnStart();

        services.AddRequestTimeouts();
        services.AddOptions<RequestTimeoutOptions>()
            .Configure<IOptions<GatewayResilienceOptions>>((timeouts, resilience) =>
            {
                var timeout = TimeSpan.FromSeconds(resilience.Value.RequestTimeoutSeconds);
                timeouts.AddPolicy(ApiTimeoutPolicyName, timeout);
                // Routes that omit TimeoutPolicy still get a bounded lifetime.
                timeouts.DefaultPolicy = new RequestTimeoutPolicy { Timeout = timeout };
            });

        services.AddOptions<TransportFailureRateHealthPolicyOptions>()
            .Configure<IOptions<GatewayResilienceOptions>>((options, resilience) =>
            {
                var settings = resilience.Value;
                options.DetectionWindowSize = TimeSpan.FromSeconds(settings.BackendHealthDetectionWindowSeconds);
                options.MinimalTotalCountThreshold = settings.BackendHealthMinimumRequestCount;
                options.DefaultFailureRateLimit = settings.BackendHealthFailureRate;
            });

        return services;
    }
}

using Microsoft.AspNetCore.Http.Timeouts;
using Microsoft.Extensions.Options;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Health;

namespace Core.Gateway.Api.Resilience;

public static class GatewayResilienceExtensions
{
    public const string ApiTimeoutPolicyName = "api";

    /// <summary>
    /// Registers request timeouts and passive health options.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">The application configuration.</param>
    /// <returns>The same service collection.</returns>
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
                options => options.TimeoutPolicies.All(pair =>
                    !string.Equals(pair.Key, ApiTimeoutPolicyName, StringComparison.OrdinalIgnoreCase) &&
                    pair.Value is >= 1 and <= 3_600),
                "Resilience:TimeoutPolicies must use names other than 'api' and 1-3600 seconds.")
            .ValidateOnStart();

        services.AddRequestTimeouts();
        services.AddOptions<RequestTimeoutOptions>()
            .Configure<IOptions<GatewayResilienceOptions>>((timeouts, resilience) =>
            {
                var timeout = TimeSpan.FromSeconds(resilience.Value.RequestTimeoutSeconds);
                timeouts.AddPolicy(ApiTimeoutPolicyName, timeout);
                foreach (var (name, seconds) in resilience.Value.TimeoutPolicies)
                {
                    timeouts.AddPolicy(name, TimeSpan.FromSeconds(seconds));
                }

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

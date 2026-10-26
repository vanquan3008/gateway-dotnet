using System.ComponentModel.DataAnnotations;

namespace Core.Gateway.Api.Resilience;

public sealed class GatewayResilienceOptions
{
    public const string SectionName = "Resilience";

    [Range(1, 300)]
    public int RequestTimeoutSeconds { get; init; } = 30;

    [Range(1, 60)]
    public int ConnectTimeoutSeconds { get; init; } = 5;

    // Applied to clusters that do not set HttpRequest:ActivityTimeout (YARP default is 100s).
    [Range(1, 300)]
    public int ActivityTimeoutSeconds { get; init; } = 20;

    // Recycles backend connections so traffic spreads to new pods behind a Kubernetes Service.
    [Range(1, 3_600)]
    public int PooledConnectionLifetimeSeconds { get; init; } = 60;

    public bool BackendHealthCheckEnabled { get; init; } = true;

    [Range(1, 300)]
    public int BackendHealthDetectionWindowSeconds { get; init; } = 10;

    [Range(1, 300)]
    public int BackendHealthReactivationSeconds { get; init; } = 11;

    [Range(1, 10_000)]
    public int BackendHealthMinimumRequestCount { get; init; } = 10;

    [Range(0.01, 1.0)]
    public double BackendHealthFailureRate { get; init; } = 0.3;
    public bool FailWhenAllDestinationsUnhealthy { get; init; } = true;
}

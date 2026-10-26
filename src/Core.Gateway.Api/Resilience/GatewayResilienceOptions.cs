using System.ComponentModel.DataAnnotations;

namespace Core.Gateway.Api.Resilience;

public sealed class GatewayResilienceOptions
{
    public const string SectionName = "Resilience";

    [Range(1, 300)]
    public int RequestTimeoutSeconds { get; init; } = 30;
    public Dictionary<string, int> TimeoutPolicies { get; init; } = [];
    [Range(0, 3)]
    public int RetryAttempts { get; init; } = 1;
    [Range(1, 60)]
    public int ConnectTimeoutSeconds { get; init; } = 5;
    [Range(1, 300)]
    public int ActivityTimeoutSeconds { get; init; } = 20;
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

    public bool ActiveHealthCheckEnabled { get; init; } = false;
    [Range(1, 300)]
    public int ActiveHealthCheckIntervalSeconds { get; init; } = 10;
    [Range(1, 60)]
    public int ActiveHealthCheckTimeoutSeconds { get; init; } = 5;
    public string ActiveHealthCheckPath { get; init; } = "/health";
}

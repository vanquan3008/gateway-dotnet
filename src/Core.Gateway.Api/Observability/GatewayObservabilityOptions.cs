using System.ComponentModel.DataAnnotations;

namespace Core.Gateway.Api.Observability;

public sealed class GatewayObservabilityOptions
{
    public const string SectionName = "Observability";

    [Required]
    public string ServiceName { get; init; } = "Core.Gateway.Api";

    public bool OtlpExporterEnabled { get; init; }

    [Url]
    public string? OtlpEndpoint { get; init; }

    [Range(100, 30_000)]
    public int OtlpTimeoutMilliseconds { get; init; } = 1_000;
}

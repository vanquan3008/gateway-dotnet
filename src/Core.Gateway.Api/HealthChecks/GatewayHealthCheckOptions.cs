using System.ComponentModel.DataAnnotations;

namespace Core.Gateway.Api.HealthChecks;

public sealed class GatewayHealthCheckOptions
{
    public const string SectionName = "HealthChecks";

    [Required]
    [RegularExpression("^/.*", ErrorMessage = "LivenessPath must start with '/'.")]
    public string LivenessPath { get; init; } = "/health/live";

    [Required]
    [RegularExpression("^/.*", ErrorMessage = "ReadinessPath must start with '/'.")]
    public string ReadinessPath { get; init; } = "/health/ready";
}

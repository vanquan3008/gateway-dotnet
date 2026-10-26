using System.ComponentModel.DataAnnotations;

namespace Core.Gateway.Api.Security.Cors;

public sealed class GatewayCorsOptions
{
    public const string SectionName = "Cors";

    public bool Enabled { get; init; }

    public string[] AllowedOrigins { get; init; } = [];

    [MinLength(1)]
    public string[] AllowedMethods { get; init; } = ["GET", "POST", "PUT", "PATCH", "DELETE"];

    [MinLength(1)]
    public string[] AllowedHeaders { get; init; } = ["Authorization", "Content-Type", "X-Correlation-ID"];
}

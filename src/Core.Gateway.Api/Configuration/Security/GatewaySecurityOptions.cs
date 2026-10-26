using System.ComponentModel.DataAnnotations;

namespace Core.Gateway.Api.Configuration.Security;

public sealed class GatewaySecurityOptions
{
    public const string SectionName = "GatewaySecurity";

    [Range(1, 100)]
    public int ForwardLimit { get; init; } = 1;

    [Range(1, 1_073_741_824)]
    public long MaxRequestBodySizeBytes { get; init; } = 10 * 1024 * 1024;

    [MinLength(1)]
    public string[] AllowedHosts { get; init; } = [];

    public string[] KnownProxies { get; init; } = [];

    public string[] KnownNetworks { get; init; } = [];

    public string[] AllowedForwardedHosts { get; init; } = [];
}

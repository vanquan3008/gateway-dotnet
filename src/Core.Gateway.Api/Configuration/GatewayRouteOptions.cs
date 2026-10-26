using System.ComponentModel.DataAnnotations;

namespace Core.Gateway.Api.Configuration;

public sealed class GatewayRouteOptions
{
    public const string SectionName = "Gateway";
    public const string ReverseProxySectionName = "ReverseProxy";
    [Required]
    public string RoutesFile { get; init; } = "Configuration/reverseproxy.json";
}

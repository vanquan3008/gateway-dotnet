using System.ComponentModel.DataAnnotations;

namespace Core.Gateway.Api.Authentication.Jwt;

public sealed class GatewayJwtOptions
{
    public const string SectionName = GatewayAuthenticationOptions.SectionName + ":Jwt";
    [Url]
    public string? Authority { get; init; }
    public string? Audience { get; init; }
    public Dictionary<string, GatewayJwtProviderOptions> Providers { get; init; } = [];
    public bool RequireHttpsMetadata { get; init; } = true;
    [Range(0, 300)]
    public int ClockSkewSeconds { get; init; } = 30;
    [Range(100, 30_000)]
    public int BackchannelTimeoutMilliseconds { get; init; } = 5_000;
}

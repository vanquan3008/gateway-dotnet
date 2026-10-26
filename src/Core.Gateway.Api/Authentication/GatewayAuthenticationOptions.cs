using System.ComponentModel.DataAnnotations;

namespace Core.Gateway.Api.Authentication;

public sealed class GatewayAuthenticationOptions
{
    public const string SectionName = "Authentication";

    public bool Enabled { get; init; }

    [Url]
    public string? Authority { get; init; }

    public string? Audience { get; init; }

    public bool RequireHttpsMetadata { get; init; } = true;

    [Range(0, 300)]
    public int ClockSkewSeconds { get; init; } = 30;

    [Range(100, 30_000)]
    public int BackchannelTimeoutMilliseconds { get; init; } = 5_000;
}

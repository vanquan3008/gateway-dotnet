using System.ComponentModel.DataAnnotations;

namespace Core.Gateway.Api.Authentication.Signature;

public sealed class GatewaySignatureOptions
{
    public const string SectionName = GatewayAuthenticationOptions.SectionName + ":Signature";

    public const string Rs256 = "RS256";
    public const string Ps256 = "PS256";

    public bool Enabled { get; init; }
    public string ClientIdHeader { get; init; } = "X-Client-Id";
    public string SignatureHeader { get; init; } = "X-Signature";
    public string TimestampHeader { get; init; } = "X-Timestamp";

    [Range(1, 3600)]
    public int MaxClockSkewSeconds { get; init; } = 300;

    [Range(1, 10_485_760)]
    public int MaxBodyBytes { get; init; } = 131_072;

    public bool RejectReplays { get; init; } = true;

    [Range(1_000, 5_000_000)]
    public int ReplayMemoryEntries { get; init; } = 500_000;

    public string Algorithm { get; init; } = Rs256;

    public Dictionary<string, GatewaySignatureClientOptions> Clients { get; init; } = [];
}

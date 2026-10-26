namespace Core.Gateway.Api.Authentication.Signature;

public sealed class GatewaySignatureClientOptions
{
    public bool Enabled { get; init; } = true;

    public string? PublicKeyFile { get; init; }
    public string[] Scopes { get; init; } = [];
    public string[] Roles { get; init; } = [];
    public string? TenantId { get; init; }
}

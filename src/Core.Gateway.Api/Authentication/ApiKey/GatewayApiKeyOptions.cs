namespace Core.Gateway.Api.Authentication.ApiKey;

public sealed class GatewayApiKeyOptions
{
    public const string SectionName = GatewayAuthenticationOptions.SectionName + ":ApiKeys";
    public bool Enabled { get; init; }
    public string HeaderName { get; init; } = "X-Api-Key";
    public Dictionary<string, GatewayApiKeyClientOptions> Clients { get; init; } = [];
}

public sealed class GatewayApiKeyClientOptions
{
    public bool Enabled { get; init; } = true;
    public string? KeyHash { get; init; }
    public string[] Scopes { get; init; } = [];
    public string[] Roles { get; init; } = [];
    public string? TenantId { get; init; }
}

namespace Core.Gateway.Api.Authentication;

public sealed class GatewayAuthenticationOptions
{
    public const string SectionName = "Authentication";
    public bool Enabled { get; init; }
}

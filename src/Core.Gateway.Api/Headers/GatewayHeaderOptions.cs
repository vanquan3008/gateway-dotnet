namespace Core.Gateway.Api.Headers;

public sealed class GatewayHeaderOptions
{
    public const string SectionName = "Headers";
    public Dictionary<string, string> ClaimsToHeaders { get; init; } = [];
}

namespace Core.Gateway.Api.Headers;

public static class GatewayHeaders
{
    public const string CorrelationId = "X-Correlation-ID";
    public const string UserId = "X-User-Id";
    public const string TenantId = "X-Tenant-Id";
    public const string MerchantId = "X-Merchant-Id";
    public const string AuthIssuer = "X-Auth-Issuer";
    public static readonly string[] TrustedIdentity = [
        UserId,
        TenantId,
        MerchantId,
        AuthIssuer
    ];
}

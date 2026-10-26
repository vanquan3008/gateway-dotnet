namespace Core.Gateway.Api.Transformations;

public static class GatewayHeaders
{
    //Trace request correlation ID, which is used for tracing requests across services.
    public const string CorrelationId = "X-Correlation-ID";
    public const string UserId = "X-User-Id";
    public const string TenantId = "X-Tenant-Id";
    public const string MerchantId = "X-Merchant-Id";
}

using System.Security.Claims;
using Core.Gateway.Api.RateLimiting.Options;

namespace Core.Gateway.Api.RateLimiting.Shared;

internal readonly record struct GatewayRateLimitPartition(string Key, string Type, FixedWindowLimit Limit)
{
    public static GatewayRateLimitPartition ForClient(HttpContext context, DistributedRateLimitingOptions settings)
    {
        if (context.User.Identity?.IsAuthenticated == true)
        {
            var userId = context.User.FindFirstValue(settings.UserIdClaim)
                ?? context.User.FindFirstValue(ClaimTypes.NameIdentifier);

            if (!string.IsNullOrWhiteSpace(userId))
            {
                return new GatewayRateLimitPartition($"user:{userId}", "user", settings.Authenticated);
            }
        }

        return ForAnonymous(context, settings.Anonymous);
    }

    public static GatewayRateLimitPartition ForAnonymous(HttpContext context, FixedWindowLimit limit)
    {
        var address = context.Connection.RemoteIpAddress;
        if (address?.IsIPv4MappedToIPv6 == true)
        {
            address = address.MapToIPv4();
        }

        var clientIp = address?.ToString() ?? "unknown";
        return new GatewayRateLimitPartition($"ip:{clientIp}", "ip", limit);
    }

    public static GatewayRateLimitPartition? ForTenant(HttpContext context, DistributedRateLimitingOptions settings)
    {
        if (settings.Tenant is null || context.User.Identity?.IsAuthenticated != true)
        {
            return null;
        }

        var tenantId = context.User.FindFirstValue(settings.TenantIdClaim);
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            return null;
        }

        return new GatewayRateLimitPartition($"tenant:{tenantId}", "tenant", settings.Tenant);
    }

    public static GatewayRateLimitPartition? ForApiKey(HttpContext context, DistributedRateLimitingOptions settings)
    {
        if (settings.ApiKey is null || context.User.Identity?.IsAuthenticated != true)
            return null;

        var apiKeyId = context.User.FindFirstValue(settings.ApiKeyIdClaim);
        return string.IsNullOrWhiteSpace(apiKeyId)
            ? null
            : new GatewayRateLimitPartition($"api-key:{apiKeyId}", "api-key", settings.ApiKey);
    }
}

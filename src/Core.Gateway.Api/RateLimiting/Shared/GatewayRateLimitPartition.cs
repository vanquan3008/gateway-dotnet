
using System.Net;
using System.Net.Sockets;
using System.Security.Claims;
using Core.Gateway.Api.Authentication;
using Core.Gateway.Api.Authentication.ApiKey.Handler;
using Core.Gateway.Api.Authentication.Signature.Handler;
using Core.Gateway.Api.RateLimiting.Distributed;
using Core.Gateway.Api.RateLimiting.Routes;
using static Core.Gateway.Api.RateLimiting.Shared.GatewayRateLimitingNames;

namespace Core.Gateway.Api.RateLimiting.Shared;

/// <summary>
/// A quota to consume.
/// </summary>
/// <param name="Key">Identifies who is counted.</param>
/// <param name="Type">The kind: user, tenant, api-key, ip or route.</param>
/// <param name="Limit">The quota.</param>
internal readonly record struct GatewayRateLimitPartition(
    string Key,
    string Type,
    QuotaLimit Limit)
{

    /// <summary>
    /// Per-client quota: by user id when authenticated, otherwise by IP.
    /// </summary>
    /// <param name="context">The current request.</param>
    /// <param name="settings">Claim names and quotas.</param>
    /// <returns>The partition to consume.</returns>
    public static GatewayRateLimitPartition ForClient(
        HttpContext context,
        DistributedRateLimitingOptions settings)
    {
        if (context.User.Identity?.IsAuthenticated == true)
        {
            var userId = context.User.FindFirstValue(settings.UserIdClaim) ?? context.User.FindFirstValue(ClaimTypes.NameIdentifier);

            if (!string.IsNullOrWhiteSpace(userId))
            {
                return new GatewayRateLimitPartition(
                    IdentityKey(context, PartitionType.User, userId),
                    PartitionType.User,
                    settings.Authenticated);
            }
        }

        return ForAnonymous(context, settings.Anonymous);
    }

    /// <summary>
    /// Quota by client IP (IPv6 grouped by /64).
    /// </summary>
    /// <param name="context">The current request.</param>
    /// <param name="limit">The quota to apply.</param>
    /// <returns>The partition to consume.</returns>
    public static GatewayRateLimitPartition ForAnonymous(
        HttpContext context,
        QuotaLimit limit)
    {
        var clientIp = GetClientIpPartitionKey(
            context.Connection.RemoteIpAddress);

        return new GatewayRateLimitPartition(
            $"{PartitionType.Ip}{PartitionKey.Separator}{clientIp}",
            PartitionType.Ip,
            limit);
    }

    /// <summary>
    /// Optional tenant quota from the tenant claim.
    /// </summary>
    /// <param name="context">The current request.</param>
    /// <param name="settings">Tenant claim name and quota.</param>
    /// <returns>The partition, or null when not configured or the caller has no tenant claim.</returns>
    public static GatewayRateLimitPartition? ForTenant(
        HttpContext context,
        DistributedRateLimitingOptions settings)
    {
        if (settings.Tenant is null ||
            context.User.Identity?.IsAuthenticated != true)
        {
            return null;
        }

        var tenantId = context.User.FindFirstValue(
            settings.TenantIdClaim);

        if (string.IsNullOrWhiteSpace(tenantId))
        {
            return null;
        }

        return new GatewayRateLimitPartition(
            IdentityKey(context, PartitionType.Tenant, tenantId),
            PartitionType.Tenant,
            settings.Tenant);
    }

    /// <summary>
    /// Optional API key quota from the validated API key claim (never the raw header).
    /// </summary>
    /// <param name="context">The current request.</param>
    /// <param name="settings">API key claim name and quota.</param>
    /// <returns>The partition, or null when not configured or the caller has no API key claim.</returns>
    public static GatewayRateLimitPartition? ForApiKey(
        HttpContext context,
        DistributedRateLimitingOptions settings)
    {
        if (settings.ApiKey is null ||
            context.User.Identity?.IsAuthenticated != true)
        {
            return null;
        }

        var apiKeyId = context.User.FindFirstValue(
            settings.ApiKeyIdClaim);

        if (string.IsNullOrWhiteSpace(apiKeyId))
        {
            return null;
        }

        return new GatewayRateLimitPartition(
            IdentityKey(context, PartitionType.ApiKey, apiKeyId),
            PartitionType.ApiKey,
            settings.ApiKey);
    }

    /// <summary>
    /// Quota for a route policy, shared by routes that use the same policy name.
    /// </summary>
    /// <param name="context">The current request.</param>
    /// <param name="policyName">Name of the policy.</param>
    /// <param name="policy">The policy limit and who it counts.</param>
    /// <param name="settings">Claim names.</param>
    /// <returns>The partition to consume.</returns>
    public static GatewayRateLimitPartition ForRoutePolicy(
        HttpContext context,
        string policyName,
        RouteRateLimitPolicyOptions policy,
        DistributedRateLimitingOptions settings)
    {
        var limit = policy.ToLimit();

        var identity = policy.Partition switch
        {
            RouteRateLimitPartition.Ip =>
                ForAnonymous(context, limit).Key,

            RouteRateLimitPartition.Tenant =>
                ClaimKey(
                    context,
                    PartitionType.Tenant,
                    settings.TenantIdClaim)
                ?? ForClient(context, settings).Key,

            RouteRateLimitPartition.ApiKey =>
                ClaimKey(
                    context,
                    PartitionType.ApiKey,
                    settings.ApiKeyIdClaim)
                ?? ForClient(context, settings).Key,

            _ => ForClient(context, settings).Key
        };

        return new GatewayRateLimitPartition(
            $"{PartitionKey.RoutePrefix}{PartitionKey.Separator}{policyName}{PartitionKey.Separator}{identity}",
            PartitionType.Route,
            limit);
    }

    /// <summary>
    /// Builds an identity key from a claim of an authenticated caller.
    /// </summary>
    /// <param name="context">The current request.</param>
    /// <param name="type">The partition type.</param>
    /// <param name="claimType">The claim to read.</param>
    /// <returns>The key, or null when not authenticated or the claim is missing.</returns>
    private static string? ClaimKey(
        HttpContext context,
        string type,
        string claimType)
    {
        if (context.User.Identity?.IsAuthenticated != true)
        {
            return null;
        }

        var id = context.User.FindFirstValue(claimType);

        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        return IdentityKey(context, type, id);
    }

    /// <summary>
    /// Builds the partition key; API key, signature and external provider logins include the scheme so different providers never share a quota.
    /// </summary>
    /// <param name="context">The current request.</param>
    /// <param name="partitionType">user, tenant or API key.</param>
    /// <param name="identityId">The id from the validated claim.</param>
    /// <returns>The partition key.</returns>
    private static string IdentityKey(
        HttpContext context,
        string partitionType,
        string identityId)
    {
        var authenticationScheme = context.User.Identity?.AuthenticationType;

        if (string.IsNullOrWhiteSpace(authenticationScheme))
        {
            return $"{partitionType}{PartitionKey.Separator}{identityId}";
        }

        bool isApiKeyAuthentication = string.Equals(
            authenticationScheme,
            GatewayApiKeyAuthenticationHandler.SchemeName,
            StringComparison.Ordinal);

        bool isSignatureAuthentication = string.Equals(
            authenticationScheme,
            GatewaySignatureAuthenticationHandler.SchemeName,
            StringComparison.Ordinal);

        bool isProviderAuthentication = authenticationScheme.StartsWith(
            GatewayAuthenticationExtensions.ProviderSchemePrefix,
            StringComparison.Ordinal);

        bool requiresAuthenticationSchemeIsolation = isApiKeyAuthentication || isSignatureAuthentication || isProviderAuthentication;
        if (!requiresAuthenticationSchemeIsolation)
        {
            return $"{partitionType}{PartitionKey.Separator}{identityId}";
        }

        return $"{partitionType}{PartitionKey.Separator}{authenticationScheme.Length}{PartitionKey.Separator}{authenticationScheme}{PartitionKey.Separator}{identityId}";
    }

    /// <summary>
    /// Normalizes an IP for counting: IPv4-mapped to IPv4, IPv6 to its /64 prefix.
    /// </summary>
    /// <param name="address">The client address.</param>
    /// <returns>The key part.</returns>
    private static string GetClientIpPartitionKey(IPAddress? address)
    {
        if (address is null)
        {
            return PartitionKey.UnknownClientIp;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return address.ToString();
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return PartitionKey.UnknownClientIp;
        }

        Span<byte> bytes = stackalloc byte[16];

        if (!address.TryWriteBytes(bytes, out var bytesWritten)
            || bytesWritten != 16)
        {
            return PartitionKey.UnknownClientIp;
        }
        bytes[8..].Clear();

        return $"{new IPAddress(bytes)}{PartitionKey.Ipv6PrefixSuffix}";
    }
}


using System.Collections.Frozen;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Core.Gateway.Api.Authentication.ApiKey.Handler;
using Core.Gateway.Api.RateLimiting;
using Microsoft.Extensions.Options;

namespace Core.Gateway.Api.Authentication.ApiKey.Clients;

internal sealed class GatewayApiKeyClients
{
    private const int MaxApiKeyLength = 512;
    private const int Sha256HashLength = 32;
    public string HeaderName { get; }
    private readonly FrozenDictionary<string, Claim[]> _claimsByKeyHash;
    /// <summary>
    /// Loads the enabled API key clients and their claims, keyed by key hash.
    /// </summary>
    /// <param name="apiKeyOptions">The API key settings.</param>
    /// <param name="rateLimitingOptions">Gives the API key and tenant claim names.</param>
    /// <exception cref="InvalidOperationException">A client has no hash, or two clients share one.</exception>
    public GatewayApiKeyClients(
        IOptions<GatewayApiKeyOptions> apiKeyOptions,
        IOptions<GatewayRateLimitingOptions> rateLimitingOptions)
    {
        ArgumentNullException.ThrowIfNull(apiKeyOptions);
        ArgumentNullException.ThrowIfNull(rateLimitingOptions);
        GatewayApiKeyOptions settings = apiKeyOptions.Value;
        GatewayRateLimitingOptions distributedSettings = rateLimitingOptions.Value;
        HeaderName = settings.HeaderName;

        Dictionary<string, Claim[]> claimsByKeyHash = new(StringComparer.Ordinal);

        foreach (KeyValuePair<string, GatewayApiKeyClientOptions> entry in settings.Clients)
        {
            string clientName = entry.Key;
            GatewayApiKeyClientOptions client = entry.Value;

            if (!client.Enabled)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(client.KeyHash))
            {
                throw new InvalidOperationException($"API key client '{clientName}' must have a configured key hash.");
            }
            string keyHash = NormalizeConfiguredHash(client.KeyHash);
            Claim[] claims = CreateClaims(
                clientName,
                client,
                distributedSettings.Distributed.ApiKeyIdClaim,
                distributedSettings.Distributed.TenantIdClaim);

            bool added = claimsByKeyHash.TryAdd(keyHash, claims);

            if (!added)
            {
                throw new InvalidOperationException("Multiple API key clients cannot use the same key hash.");
            }
        }

        _claimsByKeyHash = claimsByKeyHash.ToFrozenDictionary(StringComparer.Ordinal);
    }

    /// <summary>
    /// Looks up an API key by its SHA-256 hash and builds the caller identity.
    /// </summary>
    /// <param name="apiKey">The key sent by the caller.</param>
    /// <param name="principal">The caller identity when the key matches.</param>
    /// <returns>True when the key belongs to an enabled client.</returns>
    public bool TryCreatePrincipal(
        string? apiKey,
        out ClaimsPrincipal principal)
    {
        principal = null!;

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return false;
        }

        if (apiKey.Length > MaxApiKeyLength)
        {
            return false;
        }

        string keyHash = ComputeApiKeyHash(apiKey);

        if (!_claimsByKeyHash.TryGetValue(keyHash, out Claim[]? claims))
        {
            return false;
        }

        ClaimsIdentity identity = new(
            claims,
            GatewayApiKeyAuthenticationHandler.SchemeName,
            "sub",
            "role");

        principal = new ClaimsPrincipal(identity);

        return true;
    }

    /// <summary>
    /// Builds the claims of a client: subject, API key id, issuer, scopes, roles and tenant.
    /// </summary>
    /// <param name="clientName">The client name.</param>
    /// <param name="client">The client settings.</param>
    /// <param name="apiKeyIdClaim">Claim name for the API key id.</param>
    /// <param name="tenantIdClaim">Claim name for the tenant id.</param>
    /// <returns>The claims.</returns>
    private static Claim[] CreateClaims(
        string clientName,
        GatewayApiKeyClientOptions client,
        string apiKeyIdClaim,
        string tenantIdClaim)
    {
        List<Claim> claims =
        [
            new Claim("sub", clientName),
            new Claim(apiKeyIdClaim, clientName),
            new Claim(
                "iss",
                GatewayApiKeyAuthenticationHandler.Issuer)
        ];

        AddScopeClaim(claims, client.Scopes);
        AddRoleClaims(claims, client.Roles);
        AddTenantClaim(
            claims,
            client.TenantId,
            tenantIdClaim);

        return claims.ToArray();
    }

    /// <summary>
    /// Adds one space-separated scope claim.
    /// </summary>
    /// <param name="claims">The claim list.</param>
    /// <param name="scopes">The configured scopes.</param>
    private static void AddScopeClaim(
        List<Claim> claims,
        string[]? scopes)
    {
        if (scopes is null || scopes.Length == 0)
        {
            return;
        }

        List<string> validScopes = [];

        foreach (string scope in scopes)
        {
            if (string.IsNullOrWhiteSpace(scope))
            {
                continue;
            }

            validScopes.Add(scope.Trim());
        }

        if (validScopes.Count == 0)
        {
            return;
        }

        string scopeValue = string.Join(' ', validScopes);

        claims.Add(new Claim("scope", scopeValue));
    }

    /// <summary>
    /// Adds one role claim per role.
    /// </summary>
    /// <param name="claims">The claim list.</param>
    /// <param name="roles">The configured roles.</param>
    private static void AddRoleClaims(
        List<Claim> claims,
        string[]? roles)
    {
        if (roles is null || roles.Length == 0)
        {
            return;
        }

        foreach (string role in roles)
        {
            if (string.IsNullOrWhiteSpace(role))
            {
                continue;
            }

            claims.Add(new Claim("role", role.Trim()));
        }
    }

    /// <summary>
    /// Adds the tenant claim when the client has a tenant.
    /// </summary>
    /// <param name="claims">The claim list.</param>
    /// <param name="tenantId">The configured tenant.</param>
    /// <param name="tenantIdClaim">Claim name for the tenant id.</param>
    private static void AddTenantClaim(
        List<Claim> claims,
        string? tenantId,
        string tenantIdClaim)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            return;
        }

        claims.Add(new Claim(
            tenantIdClaim,
            tenantId.Trim()));
    }

    /// <summary>
    /// SHA-256 of the API key as lowercase hex.
    /// </summary>
    /// <param name="apiKey">The key.</param>
    /// <returns>The hash.</returns>
    private static string ComputeApiKeyHash(string apiKey)
    {
        byte[] apiKeyBytes = Encoding.UTF8.GetBytes(apiKey);

        byte[] hashBytes = SHA256.HashData(apiKeyBytes);

        return Convert.ToHexStringLower(hashBytes);
    }

    /// <summary>
    /// Checks a configured hash and turns it into lowercase hex.
    /// </summary>
    /// <param name="configuredHash">The configured hex hash.</param>
    /// <returns>The normalized hash.</returns>
    /// <exception cref="InvalidOperationException">The hash is not 32 bytes of hex.</exception>
    private static string NormalizeConfiguredHash(string configuredHash)
    {
        byte[] hashBytes;

        try
        {
            hashBytes = Convert.FromHexString(configuredHash);
        }
        catch (FormatException exception)
        {
            throw new InvalidOperationException(
                "The configured API key hash must be a valid hexadecimal SHA-256 hash.",
                exception);
        }

        if (hashBytes.Length != Sha256HashLength)
        {
            throw new InvalidOperationException(
                "The configured API key hash must contain exactly 32 bytes.");
        }

        return Convert.ToHexStringLower(hashBytes);
    }
}

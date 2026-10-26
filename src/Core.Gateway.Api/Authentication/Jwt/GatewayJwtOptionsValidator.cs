
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Core.Gateway.Api.Authentication.Jwt;

/// <summary>
/// Validates JWT settings at startup.
/// </summary>
public sealed class GatewayJwtOptionsValidator(
    bool isProduction,
    bool isOptional) : IValidateOptions<GatewayJwtOptions>
{
    private static readonly HashSet<string> HmacAlgorithms =
        new(["HS256", "HS384", "HS512"], StringComparer.Ordinal);

    private static readonly HashSet<string> RsaAlgorithms = new(["RS256", "RS384", "RS512", "PS256", "PS384", "PS512"], StringComparer.Ordinal);
    private static readonly HashSet<string> EcAlgorithms = new(["ES256", "ES384", "ES512"], StringComparer.Ordinal);
    private static readonly HashSet<string> AuthorityAlgorithms = [.. RsaAlgorithms, .. EcAlgorithms];

    /// <summary>
    /// Checks HTTPS metadata in production, the audience and authority, and each provider issuer, keys and algorithms.
    /// </summary>
    /// <param name="name">The options name (unused).</param>
    /// <param name="options">The options to check.</param>
    /// <returns>Success, or a failure listing every error.</returns>
    public ValidateOptionsResult Validate(
        string? name,
        GatewayJwtOptions options)
    {
        var errors = new List<string>();
        var section = GatewayJwtOptions.SectionName;

        if (isProduction && !options.RequireHttpsMetadata)
            errors.Add("HTTPS metadata is required in Production.");

        var isConfigured = options.Providers.Count > 0
            || !string.IsNullOrWhiteSpace(options.Authority)
            || !string.IsNullOrWhiteSpace(options.Audience);

        if (isOptional && !isConfigured)
            return errors.Count == 0
                ? ValidateOptionsResult.Success
                : ValidateOptionsResult.Fail(errors);

        if (options.Providers.Count == 0)
        {
            if (string.IsNullOrWhiteSpace(options.Audience))
                errors.Add($"{section}:Audience is required.");

            ValidateAuthority(
                options.Authority,
                $"{section}:Authority",
                options.RequireHttpsMetadata,
                errors);
        }
        else
        {
            var issuers = new HashSet<string>(StringComparer.Ordinal);
            var enabledCount = 0;

            foreach (var (providerName, provider) in options.Providers)
            {
                if (!provider.Enabled)
                    continue;

                enabledCount++;

                var path = $"{section}:Providers:{providerName}";

                if (string.IsNullOrWhiteSpace(provider.Issuer))
                {
                    errors.Add($"{path}:Issuer is required.");
                }
                else if (!issuers.Add(provider.Issuer))
                {
                    errors.Add($"{path}:Issuer must be unique.");
                }

                if (string.IsNullOrWhiteSpace(provider.Audience))
                {
                    errors.Add($"{path}:Audience is required.");
                }

                if (string.IsNullOrWhiteSpace(provider.NameClaimType))
                {
                    errors.Add($"{path}:NameClaimType is required.");
                }

                if (string.IsNullOrWhiteSpace(provider.RoleClaimType))
                {
                    errors.Add($"{path}:RoleClaimType is required.");
                }

                var hasAuthority = !string.IsNullOrWhiteSpace(provider.Authority);
                var hasSecret = !string.IsNullOrWhiteSpace(provider.SigningKey);
                var hasPublicKey = !string.IsNullOrWhiteSpace(provider.PublicKeyFile);

                var sourceCount =
                    (hasAuthority ? 1 : 0) +
                    (hasSecret ? 1 : 0) +
                    (hasPublicKey ? 1 : 0);

                if (sourceCount != 1)
                {
                    errors.Add($"{path}:Configure exactly one of Authority, SigningKey or PublicKeyFile.");
                    continue;
                }

                if (hasAuthority)
                {
                    ValidateAuthority(
                        provider.Authority,
                        $"{path}:Authority",
                        options.RequireHttpsMetadata,
                        errors);
                }

                IReadOnlyList<SecurityKey>? signingKeys = null;
                var keyLoadFailed = false;

                if (hasSecret)
                {
                    signingKeys = provider.CreateSigningKeys() ?? [];
                }
                if (hasPublicKey)
                {
                    try
                    {
                        signingKeys = provider.CreateSigningKeys() ?? [];
                    }
                    catch (IOException)
                    {
                        errors.Add($"{path}:PublicKeyFile '{provider.PublicKeyFile}' cannot be read.");
                        keyLoadFailed = true;
                    }
                    catch (UnauthorizedAccessException)
                    {
                        errors.Add($"{path}:PublicKeyFile '{provider.PublicKeyFile}' cannot be accessed.");
                        keyLoadFailed = true;
                    }
                    catch (InvalidDataException ex)
                    {
                        errors.Add($"{path}:PublicKeyFile '{provider.PublicKeyFile}' is invalid: {ex.Message}");
                        keyLoadFailed = true;
                    }
                }

                var allowedAlgorithms = hasSecret ? HmacAlgorithms : AuthorityAlgorithms;
                if (hasPublicKey)
                {
                    allowedAlgorithms = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var publicKey in signingKeys ?? [])
                    {
                        if (publicKey is RsaSecurityKey)
                            allowedAlgorithms.UnionWith(RsaAlgorithms);
                        else if (publicKey is ECDsaSecurityKey)
                            allowedAlgorithms.UnionWith(EcAlgorithms);
                    }
                }

                var algorithms = provider.ValidAlgorithms ?? [];
                var algorithmsValid =
                    algorithms.Length > 0 &&
                    algorithms.All(allowedAlgorithms.Contains);

                if (!keyLoadFailed && !algorithmsValid)
                {
                    errors.Add($"{path}:ValidAlgorithms must contain only supported algorithms for the configured key source. Allowed: {string.Join(", ", allowedAlgorithms.OrderBy(x => x, StringComparer.Ordinal))}.");
                }

                if (hasSecret && algorithmsValid && signingKeys?.FirstOrDefault() is SymmetricSecurityKey key)
                {
                    var requiredBits = algorithms
                        .Select(algorithm => algorithm switch
                        {
                            "HS512" => 512,
                            "HS384" => 384,
                            _ => 256
                        })
                        .Max();

                    if (key.KeySize < requiredBits)
                    {
                        errors.Add(
                            $"{path}:SigningKey must be at least {requiredBits / 8} bytes for the configured algorithms.");
                    }
                }
            }

            if (enabledCount == 0)
            {
                errors.Add(
                    $"{section}:Providers must contain at least one enabled provider.");
            }
        }

        return errors.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(errors);
    }

    /// <summary>
    /// Checks that an authority is an absolute HTTP(S) URL without credentials, query or fragment (HTTPS when required).
    /// </summary>
    /// <param name="value">The URL.</param>
    /// <param name="path">Option path for the error.</param>
    /// <param name="requireHttps">True to require HTTPS.</param>
    /// <param name="errors">Collects errors.</param>
    private static void ValidateAuthority(
        string? value,
        string path,
        bool requireHttps,
        List<string> errors)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
        {
            errors.Add($"{path}:Must be a valid HTTP(S) authority URL.");
            return;
        }

        if (requireHttps && uri.Scheme != Uri.UriSchemeHttps)
            errors.Add($"{path}:HTTPS is required.");
    }
}

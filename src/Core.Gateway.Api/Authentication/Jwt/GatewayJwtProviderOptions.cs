using System.Text;
using Core.Gateway.Api.Authentication.Keys;
using Microsoft.IdentityModel.Tokens;

namespace Core.Gateway.Api.Authentication.Jwt;

public sealed class GatewayJwtProviderOptions
{
    public bool Enabled { get; init; } = true;
    public string? Issuer { get; init; }
    public string? Audience { get; init; }
    public string? Authority { get; init; }
    public string? SigningKey { get; init; }
    public string? PublicKeyFile { get; init; }
    public string[] ValidAlgorithms { get; init; } = [];
    public string NameClaimType { get; init; } = "sub";
    public string RoleClaimType { get; init; } = "role";

    /// <summary>
    /// Builds the provider signing keys from SigningKey or PublicKeyFile.
    /// </summary>
    /// <returns>The keys, or null when the provider uses an Authority.</returns>
    /// <exception cref="InvalidOperationException">Both SigningKey and PublicKeyFile are set.</exception>
    internal IReadOnlyList<SecurityKey>? CreateSigningKeys()
    {
        var hasSigningKey = !string.IsNullOrWhiteSpace(SigningKey);
        var hasPublicKeyFile = !string.IsNullOrWhiteSpace(PublicKeyFile);

        if (hasSigningKey && hasPublicKeyFile)
        {
            throw new InvalidOperationException("Configure either SigningKey or PublicKeyFile, not both.");
        }

        if (hasSigningKey)
        {
            return [new SymmetricSecurityKey(Encoding.UTF8.GetBytes(SigningKey!))];
        }

        return hasPublicKeyFile ? GatewayPublicKeyLoader.Load(PublicKeyFile!) : null;
    }
}

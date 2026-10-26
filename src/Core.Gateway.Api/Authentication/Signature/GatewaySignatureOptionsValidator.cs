using Core.Gateway.Api.Authentication.Keys;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Core.Gateway.Api.Authentication.Signature;

public sealed class GatewaySignatureOptionsValidator : IValidateOptions<GatewaySignatureOptions>
{
    private const string Path = GatewaySignatureOptions.SectionName;

    /// <summary>
    /// Checks signature settings: header names, clients and their public keys.
    /// </summary>
    /// <param name="name">The options name (unused).</param>
    /// <param name="options">The options to check.</param>
    /// <returns>Success, or a failure listing every error.</returns>
    public ValidateOptionsResult Validate(string? name, GatewaySignatureOptions options)
    {
        if (!options.Enabled)
        {
            return ValidateOptionsResult.Success;
        }

        var errors = new List<string>();

        string[] headers = [options.ClientIdHeader, options.SignatureHeader, options.TimestampHeader];
        if (!headers.All(GatewayCustomHeader.IsValid))
        {
            errors.Add($"{Path}: ClientIdHeader, SignatureHeader and TimestampHeader must be custom header names (letters, digits, '-' or '_', max 64 characters) that are not reserved.");
        }
        else if (headers.Distinct(StringComparer.OrdinalIgnoreCase).Count() != headers.Length)
        {
            errors.Add($"{Path}: ClientIdHeader, SignatureHeader and TimestampHeader must be different headers.");
        }

        if (options.Algorithm is not (GatewaySignatureOptions.Rs256 or GatewaySignatureOptions.Ps256))
        {
            errors.Add($"{Path}:Algorithm must be {GatewaySignatureOptions.Rs256} or {GatewaySignatureOptions.Ps256}.");
        }

        var enabledCount = 0;
        foreach (var (clientName, client) in options.Clients)
        {
            if (!client.Enabled)
                continue;

            enabledCount++;
            var clientPath = $"{Path}:Clients:{clientName}";

            if (string.IsNullOrWhiteSpace(clientName)
                || clientName.Length > 64
                || !clientName.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'))
            {
                errors.Add($"{clientPath}:Client name must be 1-64 letters, digits, '-', '_' or '.'.");
            }

            if (client.Scopes.Concat(client.Roles).Any(value => string.IsNullOrWhiteSpace(value) || value.Any(char.IsWhiteSpace)))
            {
                errors.Add($"{clientPath}:Scopes and roles must be non-empty values without spaces.");
            }

            if (string.IsNullOrWhiteSpace(client.PublicKeyFile))
            {
                errors.Add($"{clientPath}:PublicKeyFile is required.");
                continue;
            }

            try
            {
                if (GatewayPublicKeyLoader.Load(client.PublicKeyFile).Any(key => key is not RsaSecurityKey))
                {
                    errors.Add($"{clientPath}:PublicKeyFile must contain RSA public keys only.");
                }
            }
            catch (IOException)
            {
                errors.Add($"{clientPath}:PublicKeyFile '{client.PublicKeyFile}' cannot be read.");
            }
            catch (UnauthorizedAccessException)
            {
                errors.Add($"{clientPath}:PublicKeyFile '{client.PublicKeyFile}' cannot be accessed.");
            }
            catch (InvalidDataException exception)
            {
                errors.Add($"{clientPath}:PublicKeyFile '{client.PublicKeyFile}' is invalid: {exception.Message}");
            }
        }

        if (enabledCount == 0)
        {
            errors.Add($"{Path}:Configure at least one enabled client.");
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}

using Microsoft.Extensions.Options;

namespace Core.Gateway.Api.Authentication.ApiKey;

public sealed class GatewayApiKeyOptionsValidator : IValidateOptions<GatewayApiKeyOptions>
{
    private const string Path = GatewayApiKeyOptions.SectionName;

    /// <summary>
    /// Checks API key settings: header name, unique 64-character SHA-256 hashes, scopes, roles and at least one enabled client.
    /// </summary>
    /// <param name="name">The options name (unused).</param>
    /// <param name="options">The options to check.</param>
    /// <returns>Success, or a failure listing every error.</returns>
    public ValidateOptionsResult Validate(string? name, GatewayApiKeyOptions options)
    {
        if (!options.Enabled)
        {
            return ValidateOptionsResult.Success;
        }
        var errors = new List<string>();

        if (!GatewayCustomHeader.IsValid(options.HeaderName))
        {
            errors.Add($"{Path}:HeaderName must be a custom header name (letters, digits, '-' or '_', max 64 characters) that is not reserved.");
        }

        var hashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var enabledCount = 0;

        foreach (var (clientName, client) in options.Clients)
        {
            if (!client.Enabled)
                continue;
            enabledCount++;
            var clientPath = $"{Path}:Clients:{clientName}";
            if (string.IsNullOrWhiteSpace(clientName))
            {
                errors.Add($"{clientPath}:Client name is required.");
            }
            var hash = client.KeyHash;
            if (hash is not { Length: 64 } || !hash.All(char.IsAsciiHexDigit))
            {
                errors.Add($"{clientPath}:KeyHash must be a 64-character hex SHA-256 digest.");
            }
            else if (!hashes.Add(hash))
            {
                errors.Add($"{clientPath}:KeyHash must be unique across clients.");
            }
            if (client.Scopes.Concat(client.Roles).Any(value => string.IsNullOrWhiteSpace(value) || value.Any(char.IsWhiteSpace)))
            {
                errors.Add($"{clientPath}:Scopes and roles must be non-empty values without spaces.");
            }
        }

        if (enabledCount == 0)
        {
            errors.Add($"{Path}:Configure at least one enabled client.");
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}

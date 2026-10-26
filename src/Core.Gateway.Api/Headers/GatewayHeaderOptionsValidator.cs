using Microsoft.Extensions.Options;

namespace Core.Gateway.Api.Headers;

public sealed class GatewayHeaderOptionsValidator : IValidateOptions<GatewayHeaderOptions>
{
    private static readonly string[] ReservedHeaders =
    [
        "Authorization",
        "Cookie",
        "Host",
        "Forwarded",
        "X-Forwarded-For",
        "X-Forwarded-Host",
        "X-Forwarded-Proto",
        GatewayHeaders.CorrelationId
    ];

    /// <summary>
    /// Checks the claim-to-header mappings: valid names and no reserved headers.
    /// </summary>
    /// <param name="name">The options name (unused).</param>
    /// <param name="options">The options to check.</param>
    /// <returns>Success, or a failure listing every error.</returns>
    public ValidateOptionsResult Validate(string? name, GatewayHeaderOptions options)
    {
        var errors = new List<string>();
        foreach (var (header, claim) in options.ClaimsToHeaders)
        {
            var path = $"Headers:ClaimsToHeaders:{header}";
            if (string.IsNullOrWhiteSpace(header) || header.Length > 64 ||
                !header.All(character => char.IsAsciiLetterOrDigit(character) || character == '-'))
                errors.Add($"{path}: header name is invalid.");
            else if (ReservedHeaders.Contains(header, StringComparer.OrdinalIgnoreCase))
                errors.Add($"{path}: header is reserved and cannot be overwritten.");

            if (string.IsNullOrWhiteSpace(claim))
                errors.Add($"{path}: claim type must not be empty.");
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}

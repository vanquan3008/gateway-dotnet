namespace Core.Gateway.Api.Authentication;

internal static class GatewayCustomHeader
{
    private static readonly HashSet<string> Reserved = new([
        "Authorization",
        "Cookie",
        "Host",
        "Forwarded",
        "X-Forwarded-For",
        "X-Forwarded-Host",
        "X-Forwarded-Proto",
        "X-Correlation-ID"], StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Checks a custom header name: letters, digits, - or _, at most 64 characters, and not a reserved header.
    /// </summary>
    /// <param name="name">The header name.</param>
    /// <returns>True when the name is allowed.</returns>
    public static bool IsValid(string? name) =>
        !string.IsNullOrWhiteSpace(name)
        && name.Length <= 64
        && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')
        && !Reserved.Contains(name);
}

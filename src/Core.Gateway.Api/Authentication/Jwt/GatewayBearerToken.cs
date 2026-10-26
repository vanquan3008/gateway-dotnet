namespace Core.Gateway.Api.Authentication.Jwt;

internal static class GatewayBearerToken
{
    private const string Prefix = "Bearer ";

    /// <summary>
    /// Reads the Bearer token from the Authorization header.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <param name="token">The token, or empty.</param>
    /// <returns>True when a non-empty token was found.</returns>
    public static bool TryGet(HttpRequest request, out string token)
    {
        var authorization = request.Headers.Authorization.ToString();
        token = authorization.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) ? authorization[Prefix.Length..].Trim() : string.Empty;
        return token.Length > 0;
    }
}

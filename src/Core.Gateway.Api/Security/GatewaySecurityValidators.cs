namespace Core.Gateway.Api.Security;

public static class GatewaySecurityValidators
{
    private static readonly HashSet<string> DisallowedWildcardHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "*",
        "0.0.0.0",
        "::",
        "[::]",
        "0.0.0.0/0",
        "::/0"
    };

    private static readonly System.Net.IPNetwork[] DocumentationNetworks =
    [
        System.Net.IPNetwork.Parse("192.0.2.0/24"),
        System.Net.IPNetwork.Parse("198.51.100.0/24"),
        System.Net.IPNetwork.Parse("203.0.113.0/24"),
        System.Net.IPNetwork.Parse("2001:db8::/32")
    ];

    /// <summary>
    /// Whether an IP address is in a documentation sample range.
    /// </summary>
    /// <param name="address">The address.</param>
    /// <returns>True when it is a sample address.</returns>
    public static bool IsDocumentationAddress(string? address) =>
        System.Net.IPAddress.TryParse(address, out var ip) &&
        DocumentationNetworks.Any(network => network.Contains(ip));

    /// <summary>
    /// Whether a network starts in a documentation sample range.
    /// </summary>
    /// <param name="network">The CIDR network.</param>
    /// <returns>True when it is a sample network.</returns>
    public static bool IsDocumentationNetwork(string? network) =>
        System.Net.IPNetwork.TryParse(network, out var parsed) &&
        DocumentationNetworks.Any(documentation => documentation.Contains(parsed.BaseAddress));

    /// <summary>
    /// Whether a host is specific, not a wildcard such as * or 0.0.0.0.
    /// </summary>
    /// <param name="host">The host, optionally with a port.</param>
    /// <returns>True when allowed.</returns>
    public static bool IsValidRestrictedHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        var trimmed = host.Trim();
        string hostWithoutPort;
        if (trimmed.StartsWith('[') && trimmed.Contains(']'))
        {
            hostWithoutPort = trimmed.Substring(0, trimmed.IndexOf(']') + 1);
        }
        else if (trimmed.Contains(':') && !trimmed.Contains("::"))
        {
            hostWithoutPort = trimmed.Split(':')[0];
        }
        else
        {
            hostWithoutPort = trimmed;
        }

        return !DisallowedWildcardHosts.Contains(hostWithoutPort);
    }

    /// <summary>
    /// Whether an origin is a plain http or https origin: no path, query, trailing slash or wildcard host.
    /// </summary>
    /// <param name="origin">The origin.</param>
    /// <returns>True when valid.</returns>
    public static bool IsValidCorsOrigin(string? origin)
    {
        if (string.IsNullOrWhiteSpace(origin) ||
            !string.Equals(origin, origin.Trim(), StringComparison.Ordinal) ||
            origin.EndsWith('/'))
        {
            return false;
        }

        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri))
        {
            return false;
        }
        if (!IsValidRestrictedHost(uri.Host))
        {
            return false;
        }
        var isValidScheme = uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps;
        var hasNoExtraPathOrQuery = uri.AbsolutePath == "/" &&
                                    string.IsNullOrEmpty(uri.Query) &&
                                    string.IsNullOrEmpty(uri.Fragment) &&
                                    string.IsNullOrEmpty(uri.UserInfo);

        if (!isValidScheme || !hasNoExtraPathOrQuery)
        {
            return false;
        }
        var hasRedundantDefaultPort = uri.IsDefaultPort && origin.Substring(uri.Scheme.Length + 3).Contains(':');
        return !hasRedundantDefaultPort;
    }
}

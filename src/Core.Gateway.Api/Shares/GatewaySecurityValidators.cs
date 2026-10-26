namespace Core.Gateway.Api.Shares;

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

    // RFC 5737 / RFC 3849 ranges are only used by samples; trusting them in a real cluster means
    // X-Forwarded-For is ignored and every client shares the ingress IP's rate limit partition.
    private static readonly System.Net.IPNetwork[] DocumentationNetworks =
    [
        System.Net.IPNetwork.Parse("192.0.2.0/24"),
        System.Net.IPNetwork.Parse("198.51.100.0/24"),
        System.Net.IPNetwork.Parse("203.0.113.0/24"),
        System.Net.IPNetwork.Parse("2001:db8::/32")
    ];

    public static bool IsDocumentationAddress(string? address) =>
        System.Net.IPAddress.TryParse(address, out var ip) &&
        DocumentationNetworks.Any(network => network.Contains(ip));

    public static bool IsDocumentationNetwork(string? network) =>
        System.Net.IPNetwork.TryParse(network, out var parsed) &&
        DocumentationNetworks.Any(documentation => documentation.Contains(parsed.BaseAddress));

    public static bool IsValidRestrictedHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        var hostWithoutPort = ExtractHostWithoutPort(host);
        if (DisallowedWildcardHosts.Contains(hostWithoutPort))
        {
            return false;
        }

        return true;
    }
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

        // 3. Tái sử dụng kiểm tra Host xem có dính Wildcard hay không
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
    private static string ExtractHostWithoutPort(string host)
    {
        var trimmed = host.Trim();
        if (trimmed.StartsWith('[') && trimmed.Contains(']'))
        {
            var closeBracketIndex = trimmed.IndexOf(']');
            return trimmed.Substring(0, closeBracketIndex + 1);
        }
        if (trimmed.Contains(':') && !trimmed.Contains("::"))
        {
            return trimmed.Split(':')[0];
        }
        return trimmed;
    }
}

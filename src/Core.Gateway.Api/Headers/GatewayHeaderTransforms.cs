using Core.Gateway.Api.Authentication.ApiKey;
using Core.Gateway.Api.Authentication.Signature;
using Microsoft.Extensions.Options;
using Yarp.ReverseProxy.Transforms;

namespace Core.Gateway.Api.Headers;

public static class GatewayHeaderTransforms
{
    private const int MaxHeaderLength = 256;
    /// <summary>
    /// Adds the proxy transforms: drop untrusted headers, add the correlation id and mapped claim headers, remove Server and X-Powered-By.
    /// </summary>
    /// <param name="builder">The reverse proxy builder.</param>
    /// <returns>The same builder.</returns>
    /// <exception cref="InvalidOperationException">A claim mapping or the API key header name is invalid.</exception>
    public static IReverseProxyBuilder AddGatewayHeaderTransforms(
        this IReverseProxyBuilder builder)
    {
        return builder.AddTransforms(context =>
        {
            var headers = context.Services
                .GetRequiredService<IOptions<GatewayHeaderOptions>>()
                .Value;

            var apiKeys = context.Services
                .GetRequiredService<IOptions<GatewayApiKeyOptions>>()
                .Value;

            var signature = context.Services
                .GetRequiredService<IOptions<GatewaySignatureOptions>>()
                .Value;

            var mappings = headers.ClaimsToHeaders.ToArray();
            var apiKeyHeader = apiKeys.HeaderName;

            var untrustedHeaders = new HashSet<string>(
                GatewayHeaders.TrustedIdentity,
                StringComparer.OrdinalIgnoreCase)
            {
                GatewayHeaders.CorrelationId
            };
            foreach (var mapping in mappings)
            {
                untrustedHeaders.Add(mapping.Key);
            }

            if (!string.IsNullOrWhiteSpace(apiKeyHeader))
            {
                untrustedHeaders.Add(apiKeyHeader);
            }

            untrustedHeaders.Add(signature.SignatureHeader);
            untrustedHeaders.Add(signature.TimestampHeader);
            untrustedHeaders.Add(signature.ClientIdHeader);

            if (mappings.Any(mapping =>
                    string.IsNullOrWhiteSpace(mapping.Key) ||
                    string.IsNullOrWhiteSpace(mapping.Value) ||
                    mapping.Key.Length > 64 ||
                    !mapping.Key.All(c =>
                        char.IsAsciiLetterOrDigit(c) || c == '-')))
            {
                throw new InvalidOperationException(
                    "Invalid claim-to-header mapping.");
            }

            if (!string.IsNullOrWhiteSpace(apiKeyHeader) &&
                (apiKeyHeader.Length > 64 ||
                 !apiKeyHeader.All(c =>
                     char.IsAsciiLetterOrDigit(c) || c == '-')))
            {
                throw new InvalidOperationException(
                    "Invalid API key header name.");
            }

            if (!string.IsNullOrWhiteSpace(apiKeyHeader) &&
                mappings.Any(mapping => string.Equals(
                    mapping.Key,
                    apiKeyHeader,
                    StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException(
                    "Claims cannot be mapped to the API key header.");
            }

            context.AddRequestTransform(request =>
            {
                var httpContext = request.HttpContext;
                var proxyHeaders = request.ProxyRequest.Headers;

                foreach (var header in untrustedHeaders)
                {
                    proxyHeaders.Remove(header);
                }

                var correlationId = httpContext.Request.Headers[
                    GatewayHeaders.CorrelationId].ToString();

                if (!string.IsNullOrWhiteSpace(correlationId) &&
                    correlationId.Length <= MaxHeaderLength &&
                    correlationId.All(c => c is >= ' ' and <= '~'))
                {
                    proxyHeaders.TryAddWithoutValidation(
                        GatewayHeaders.CorrelationId,
                        correlationId);
                }

                if (httpContext.User.Identity?.IsAuthenticated == true)
                {
                    foreach (var (header, claim) in mappings)
                    {
                        var value = httpContext.User.FindFirst(claim)?.Value;

                        if (string.IsNullOrWhiteSpace(value) ||
                            value.Length > MaxHeaderLength ||
                            !value.All(c => c is >= ' ' and <= '~'))
                        {
                            continue;
                        }

                        proxyHeaders.TryAddWithoutValidation(header, value);
                    }
                }

                return ValueTask.CompletedTask;
            });

            context.AddResponseHeaderRemove(
                "Server",
                ResponseCondition.Always);

            context.AddResponseHeaderRemove(
                "X-Powered-By",
                ResponseCondition.Always);

            context.AddResponseHeaderRemove(
                "X-AspNet-Version",
                ResponseCondition.Always);

            context.AddResponseHeaderRemove(
                "X-AspNetMvc-Version",
                ResponseCondition.Always);

            context.AddResponseHeaderRemove(
                "X-SourceFiles",
                ResponseCondition.Always);
        });
    }
}

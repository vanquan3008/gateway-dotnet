using System.Net.Http.Headers;
using Yarp.ReverseProxy.Transforms;

namespace Core.Gateway.Api.Transformations;

public static class GatewayTransformExtensions
{
    private const int MaxTrustedHeaderLength = 256;

    public static IReverseProxyBuilder AddGatewayTransforms(this IReverseProxyBuilder builder)
    {
        builder.AddTransforms(transformContext =>
        {
            /// Delete add Header Client send
            transformContext.AddRequestTransform(requestContext =>
            {
                var proxyHeaders = requestContext.ProxyRequest.Headers;

                proxyHeaders.Remove(GatewayHeaders.UserId);
                proxyHeaders.Remove(GatewayHeaders.TenantId);
                proxyHeaders.Remove(GatewayHeaders.MerchantId);
                proxyHeaders.Remove(GatewayHeaders.CorrelationId);

                AddTrustedHeader(proxyHeaders, GatewayHeaders.CorrelationId, requestContext.HttpContext.Request.Headers[GatewayHeaders.CorrelationId].ToString());

                return ValueTask.CompletedTask;
            });
            // Add Header verify
            transformContext.AddResponseTransform(responseContext =>
            {
                responseContext.HttpContext.Response.Headers.Remove("Server");
                responseContext.HttpContext.Response.Headers.Remove("X-Powered-By");
                return ValueTask.CompletedTask;
            });
        });

        return builder;
    }

    private static void AddTrustedHeader(HttpRequestHeaders headers, string headerName, string? value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > MaxTrustedHeaderLength ||
            value.Contains('\r', StringComparison.Ordinal) ||
            value.Contains('\n', StringComparison.Ordinal))
        {
            return;
        }

        headers.TryAddWithoutValidation(headerName, value);
    }
}

using System.Threading.RateLimiting;
using Core.Gateway.Api.Authentication;
using Core.Gateway.Api.Authentication.ApiKey;
using Core.Gateway.Api.Authentication.Signature;
using Core.Gateway.Api.RateLimiting.Shared;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace Core.Gateway.Api.RateLimiting.Local;

internal sealed class GatewayAnonymousRateLimitingMiddleware
    : IMiddleware, IDisposable
{
    private static readonly object CountedKey = new();
    private readonly PartitionedRateLimiter<HttpContext>? _limiter;
    private readonly TimeSpan _window;
    private readonly string[] _credentialHeaders = [];

    /// <summary>
    /// Builds the per-IP limiter when Local:Anonymous is configured.
    /// </summary>
    /// <param name="options">The rate limiting options.</param>
    /// <param name="authentication">Authentication switch.</param>
    /// <param name="apiKeys">API key settings.</param>
    /// <param name="signature">Signature settings.</param>
    public GatewayAnonymousRateLimitingMiddleware(
        IOptions<GatewayRateLimitingOptions> options,
        IOptions<GatewayAuthenticationOptions> authentication,
        IOptions<GatewayApiKeyOptions> apiKeys,
        IOptions<GatewaySignatureOptions> signature)
    {
        var settings = options.Value;

        if (!settings.Enabled || settings.Local?.Enabled != true || settings.Local.Anonymous == null)
        {
            _limiter = null;
            return;
        }

        if (authentication.Value.Enabled)
        {
            List<string> headers = [HeaderNames.Authorization];
            if (apiKeys.Value.Enabled)
            {
                headers.Add(apiKeys.Value.HeaderName);
            }

            if (signature.Value.Enabled)
            {
                headers.Add(signature.Value.SignatureHeader);
            }

            _credentialHeaders = [.. headers];
        }

        var anonymousLimit = settings.Local.Anonymous;
        _window = anonymousLimit.Window;
        _limiter = PartitionedRateLimiter.Create<HttpContext, string>(
            context =>
            {
                var partition = GatewayRateLimitPartition.ForAnonymous(
                    context,
                    anonymousLimit);
                return RateLimitPartition.GetSlidingWindowLimiter(
                    partition.Key, _ => anonymousLimit.ToSlidingWindow());
            });
    }

    /// <summary>
    /// Per-IP check before the cluster limiter, for requests without credentials.
    /// </summary>
    /// <param name="context">The current request.</param>
    /// <param name="next">The rest of the pipeline.</param>
    /// <returns>A task that completes when the request was forwarded or rejected with 429.</returns>
    public Task InvokeBeforeAuthenticationAsync(HttpContext context, RequestDelegate next)
    {
        if (_limiter == null || CarriesCredential(context.Request))
        {
            return next(context);
        }

        return AcquireAsync(context, next);
    }

    /// <summary>
    /// Per-IP check after authentication, for unauthenticated requests not counted earlier.
    /// </summary>
    /// <param name="context">The current request.</param>
    /// <param name="next">The rest of the pipeline.</param>
    /// <returns>A task that completes when the request was forwarded or rejected with 429.</returns>
    public Task InvokeAsync(
        HttpContext context,
        RequestDelegate next)
    {
        if (_limiter == null ||
            context.Items.ContainsKey(CountedKey) ||
            context.User.Identity?.IsAuthenticated == true)
        {
            return next(context);
        }

        return AcquireAsync(context, next);
    }

    /// <summary>
    /// Takes one permit for the client IP or answers 429.
    /// </summary>
    private async Task AcquireAsync(HttpContext context, RequestDelegate next)
    {
        if (context.GetEndpoint()?.Metadata.GetMetadata<DisableRateLimitingAttribute>() != null)
        {
            await next(context);
            return;
        }

        context.Items[CountedKey] = true;
        using RateLimitLease lease = _limiter!.AttemptAcquire(context);
        if (lease.IsAcquired)
        {
            await next(context);
            return;
        }
        GatewayRateLimitingMetrics.RecordAnonymousRejected();

        // A sliding window lease carries no RetryAfter, so tell the client to wait one full window.
        var retryAfter = lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfterValue)
            ? retryAfterValue
            : _window;
        await GatewayRateLimitResponse.WriteQuotaExceededAsync(context, retryAfter);
    }

    /// <summary>
    /// Whether the request carries a credential header.
    /// </summary>
    private bool CarriesCredential(HttpRequest request)
    {
        foreach (var header in _credentialHeaders)
        {
            if (request.Headers.ContainsKey(header))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Releases the limiter.
    /// </summary>
    public void Dispose()
    {
        _limiter?.Dispose();
    }
}

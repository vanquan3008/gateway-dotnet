using System.Globalization;
using Core.Gateway.Api.RateLimiting.Distributed.Store;

namespace Core.Gateway.Api.RateLimiting.Shared;

/// <summary>
/// X-RateLimit-Limit / -Remaining / -Reset (seconds) for the shared quota closest to exhaustion.
/// A request can pass several quotas (client, tenant, API key, route); each one reports here and the tightest wins.
/// Reset is the time to the end of the current window, or the Retry-After when the request was rejected.
/// </summary>
internal static class GatewayRateLimitHeaders
{
    public const string Limit = "X-RateLimit-Limit";
    public const string Remaining = "X-RateLimit-Remaining";
    public const string Reset = "X-RateLimit-Reset";

    private static readonly object StateKey = new();

    /// <summary>
    /// The tightest quota seen so far for this request.
    /// </summary>
    /// <param name="Limit">Quota limit.</param>
    /// <param name="Remaining">Permits left.</param>
    /// <param name="ResetSeconds">Seconds until reset.</param>
    private sealed record State(int Limit, int Remaining, int ResetSeconds);

    /// <summary>
    /// Records a shared quota result for the X-RateLimit-* headers, keeping the quota closest to exhaustion.
    /// </summary>
    /// <param name="context">The current request.</param>
    /// <param name="result">What the store answered.</param>
    public static void Observe(HttpContext context, DistributedRateLimitResult result)
    {
        if (result.Limit is not { } limit
            || result.Status == DistributedRateLimitStatus.Unavailable)
        {
            return;
        }

        var observed = new State(
            limit,
            result.Remaining ?? 0,
            Math.Max(1, (int)Math.Ceiling(result.RetryAfter.TotalSeconds)));

        if (context.Items.TryGetValue(StateKey, out var existing))
        {
            if (observed.Remaining < ((State)existing!).Remaining)
            {
                context.Items[StateKey] = observed;
            }

            return;
        }

        context.Items[StateKey] = observed;
        context.Response.OnStarting(static state =>
        {
            Apply((HttpContext)state);
            return Task.CompletedTask;
        }, context);
    }

    /// <summary>
    /// Writes the X-RateLimit-* headers from the recorded state.
    /// </summary>
    /// <param name="context">The current request.</param>
    internal static void Apply(HttpContext context)
    {
        if (!context.Items.TryGetValue(StateKey, out var value)
            || value is not State state)
        {
            return;
        }

        var headers = context.Response.Headers;
        headers[Limit] = state.Limit.ToString(CultureInfo.InvariantCulture);
        headers[Remaining] = state.Remaining.ToString(CultureInfo.InvariantCulture);
        headers[Reset] = state.ResetSeconds.ToString(CultureInfo.InvariantCulture);
    }
}

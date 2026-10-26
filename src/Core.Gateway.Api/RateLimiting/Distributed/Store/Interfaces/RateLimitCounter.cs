namespace Core.Gateway.Api.RateLimiting.Distributed.Store.Interfaces;

/// <summary>
/// Request counts of the current and the previous window.
/// </summary>
/// <param name="Current">Requests in the current window.</param>
/// <param name="Previous">Requests in the previous window.</param>
internal readonly record struct RateLimitCounter(long Current, long Previous);

namespace Core.Gateway.Api.Redis;

/// <summary>
/// Redis could not be used: error, socket failure or timeout.
/// </summary>
public sealed class RedisUnavailableException(Exception innerException)
    : Exception("Redis is unavailable.", innerException)
{
    public string Reason => InnerException!.GetType().Name;
}

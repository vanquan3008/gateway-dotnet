using StackExchange.Redis;

namespace Core.Gateway.Api.Redis;

public interface IRedisConnection
{
    bool IsConfigured { get; }
    ValueTask<T> ExecuteAsync<T>(Func<IDatabase, Task<T>> command, CancellationToken cancellationToken);
}

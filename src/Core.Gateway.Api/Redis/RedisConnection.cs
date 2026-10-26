using System.Net.Sockets;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Core.Gateway.Api.Redis;

/// <summary>
/// Lazy shared Redis connection that fails fast instead of queuing when Redis is down.
/// </summary>
public sealed class RedisConnection(IOptions<RedisOptions> options) : IRedisConnection, IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IConnectionMultiplexer? _connection;
    private int _disposed;
    public bool IsConfigured => options.Value.IsConfigured;

    /// <summary>
    /// Runs a Redis command, connecting on first use. Redis, socket and timeout errors become RedisUnavailableException.
    /// </summary>
    /// <param name="command">The command to run.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The command result.</returns>
    /// <exception cref="RedisUnavailableException">Redis could not be used.</exception>
    public async ValueTask<T> ExecuteAsync<T>(Func<IDatabase, Task<T>> command, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        if (!settings.IsConfigured)
        {
            throw new RedisUnavailableException(new InvalidOperationException("Redis is not configured."));
        }

        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            var connection = Volatile.Read(ref _connection);

            if (connection is null)
            {
                await _gate.WaitAsync(cancellationToken);

                try
                {
                    ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

                    connection = Volatile.Read(ref _connection);

                    if (connection is null)
                    {
                        var configuration = ConfigurationOptions.Parse(settings.ConnectionString!);
                        configuration.AbortOnConnectFail = false;
                        configuration.ConnectTimeout = settings.ConnectTimeoutMilliseconds;
                        configuration.ConnectRetry = 0;
                        configuration.AsyncTimeout = settings.TimeoutMilliseconds;
                        configuration.SyncTimeout = settings.TimeoutMilliseconds;
                        configuration.BacklogPolicy = BacklogPolicy.FailFast;
                        connection = await ConnectionMultiplexer.ConnectAsync(configuration);
                        if (cancellationToken.IsCancellationRequested || Volatile.Read(ref _disposed) != 0)
                        {
                            await connection.DisposeAsync();
                            cancellationToken.ThrowIfCancellationRequested();
                            throw new ObjectDisposedException(nameof(RedisConnection));
                        }

                        Volatile.Write(ref _connection, connection);
                    }
                }
                finally
                {
                    _gate.Release();
                }
            }

            return await command(connection.GetDatabase()).WaitAsync(TimeSpan.FromMilliseconds(settings.TimeoutMilliseconds), cancellationToken);
        }
        catch (Exception exception) when (exception is RedisException or SocketException or TimeoutException)
        {
            throw new RedisUnavailableException(exception);
        }
    }

    /// <summary>
    /// Closes the connection.
    /// </summary>
    /// <returns>A task that completes when it is closed.</returns>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        await _gate.WaitAsync();
        try
        {
            if (Interlocked.Exchange(ref _connection, null) is { } connection)
            {
                await connection.DisposeAsync();
            }
        }
        finally
        {
            _gate.Release();
        }
    }
}

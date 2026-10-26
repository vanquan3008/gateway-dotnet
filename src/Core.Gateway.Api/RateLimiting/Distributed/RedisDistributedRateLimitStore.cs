using System.Net.Sockets;
using Core.Gateway.Api.RateLimiting.Distributed.Interfaces;
using Core.Gateway.Api.RateLimiting.Options;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Core.Gateway.Api.RateLimiting.Distributed;

public sealed class RedisDistributedRateLimitStore(
    IOptions<GatewayRateLimitingOptions> options,
    ILogger<RedisDistributedRateLimitStore> logger)
    : IDistributedRateLimitStore, IHostedService, IAsyncDisposable
{
    private readonly GatewayRateLimitingOptions _settings = options.Value;
    private readonly object _availabilityLock = new();
    private readonly SemaphoreSlim _connectionGate = new(1, 1);
    private IConnectionMultiplexer? _connection;
    private long _retryAtMilliseconds;
    private long _failureVersion;
    private bool _isUnavailable;
    private bool _recoveryProbeActive;
    private int _disposed;

    private bool IsDistributedEnabled =>
        _settings.Enabled &&
        _settings.Distributed.Enabled &&
        _settings.Distributed.IsConfigured;

    private TimeSpan OperationTimeout =>
        TimeSpan.FromMilliseconds(_settings.Distributed.TimeoutMilliseconds);

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_settings.Enabled || !_settings.Distributed.Enabled)
            return;

        if (!_settings.Distributed.IsConfigured)
        {
            logger.LogWarning(
                "Distributed rate limiting is enabled but Redis is not configured; shared quotas are unavailable.");

            return;
        }

        await AcquireAsync("probe:startup", int.MaxValue, TimeSpan.FromSeconds(1), cancellationToken);
    }

    public async ValueTask<DistributedRateLimitResult> AcquireAsync(
        string partitionKey,
        int permitLimit,
        TimeSpan window,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(partitionKey);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(permitLimit);

        if (window <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(window));

        cancellationToken.ThrowIfCancellationRequested();

        if (!IsDistributedEnabled)
            return DistributedRateLimitResult.Unavailable;

        bool isRecoveryProbe;
        long failureVersion;

        lock (_availabilityLock)
        {
            failureVersion = _failureVersion;
            isRecoveryProbe = false;

            if (_isUnavailable)
            {
                if (Environment.TickCount64 < _retryAtMilliseconds || _recoveryProbeActive)
                    return DistributedRateLimitResult.Unavailable;

                _recoveryProbeActive = true;
                isRecoveryProbe = true;
            }
        }

        try
        {
            var connection = await EnsureConnectionAsync(cancellationToken);
            var database = connection.GetDatabase();

            var result = await RedisFixedWindowCounter
                .AcquireAsync(
                    database,
                    _settings.Distributed.KeyPrefix,
                    partitionKey,
                    permitLimit,
                    window)
                .WaitAsync(OperationTimeout, cancellationToken);

            MarkAvailable(failureVersion);

            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsRedisInfrastructureFailure(exception))
        {
            MarkUnavailable(exception.GetType().Name);

            return DistributedRateLimitResult.Unavailable;
        }
        finally
        {
            if (isRecoveryProbe)
            {
                lock (_availabilityLock)
                {
                    _recoveryProbeActive = false;
                }
            }
        }
    }

    private async Task<IConnectionMultiplexer> EnsureConnectionAsync(
        CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(RedisDistributedRateLimitStore));

        var connection = Volatile.Read(ref _connection);

        if (connection is not null)
            return connection;

        await _connectionGate.WaitAsync(cancellationToken);

        try
        {
            if (Volatile.Read(ref _disposed) != 0)
                throw new ObjectDisposedException(nameof(RedisDistributedRateLimitStore));

            connection = Volatile.Read(ref _connection);

            if (connection is not null)
                return connection;

            var settings = _settings.Distributed;

            var configuration =
                ConfigurationOptions.Parse(settings.RedisConnectionString!);

            configuration.AbortOnConnectFail = false;
            configuration.ConnectTimeout = settings.ConnectTimeoutMilliseconds;
            configuration.ConnectRetry = 0;
            configuration.AsyncTimeout = settings.TimeoutMilliseconds;
            configuration.SyncTimeout = settings.TimeoutMilliseconds;
            configuration.BacklogPolicy = BacklogPolicy.FailFast;

            connection = await ConnectionMultiplexer.ConnectAsync(configuration);

            if (cancellationToken.IsCancellationRequested)
            {
                await connection.DisposeAsync();
                cancellationToken.ThrowIfCancellationRequested();
            }

            Volatile.Write(ref _connection, connection);

            return connection;
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    private void MarkAvailable(long failureVersion)
    {
        lock (_availabilityLock)
        {
            if (!_isUnavailable || failureVersion != _failureVersion)
                return;

            _isUnavailable = false;
            _retryAtMilliseconds = 0;
        }

        logger.LogInformation(
            "Redis distributed rate limiting recovered; shared quotas are active.");
    }

    private void MarkUnavailable(string reason)
    {
        bool shouldLog;

        lock (_availabilityLock)
        {
            shouldLog = !_isUnavailable;

            _isUnavailable = true;
            _failureVersion++;

            _retryAtMilliseconds =
                Environment.TickCount64 +
                _settings.Distributed.FailureCooldownSeconds * 1_000L;
        }

        if (!shouldLog)
            return;

        logger.LogWarning(
            "Redis distributed rate limiting unavailable ({Reason}); shared quotas cannot be enforced.", reason);
    }

    private static bool IsRedisInfrastructureFailure(Exception exception) =>
        exception is RedisException or SocketException or TimeoutException;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        var connection = Interlocked.Exchange(ref _connection, null);

        if (connection is not null)
            await connection.DisposeAsync();

        _connectionGate.Dispose();
    }
}

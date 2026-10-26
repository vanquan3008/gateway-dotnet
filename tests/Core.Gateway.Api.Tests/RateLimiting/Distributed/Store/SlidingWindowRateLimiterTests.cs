using Core.Gateway.Api.RateLimiting.Distributed.Store;
using Core.Gateway.Api.RateLimiting.Distributed.Store.Interfaces;
using Xunit;

namespace Core.Gateway.Api.Tests.RateLimiting.Distributed.Store;

public sealed class SlidingWindowRateLimiterTests
{
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(1);
    private const long Start = 1_700_000_000_000;

    private readonly ManualTime _time = new();
    private readonly InMemoryStorage _storage = new();
    private readonly SlidingWindowRateLimiter _limiter;

    public SlidingWindowRateLimiterTests() => _limiter = new SlidingWindowRateLimiter(_storage, _time);

    [Fact]
    public async Task AllowsUpToTheLimitInAWindowAndThenRejects()
    {
        _time.Now = Start + 100;

        Assert.Equal(10, await CountAllowed(10));
        Assert.Equal(DistributedRateLimitStatus.Rejected, (await Acquire(10)).Status);
    }

    [Fact]
    public async Task ReportsTheLimitAndHowManyPermitsAreLeft()
    {
        _time.Now = Start + 100;

        var first = await Acquire(10);
        var second = await Acquire(10);

        Assert.Equal((10, 9), (first.Limit, first.Remaining));
        Assert.Equal((10, 8), (second.Limit, second.Remaining));
        Assert.Equal(TimeSpan.FromMilliseconds(900), first.RetryAfter);

        for (var i = 0; i < 8; i++)
            await Acquire(10);
        var rejected = await Acquire(10);

        Assert.Equal((10, 0), (rejected.Limit, rejected.Remaining));
    }

    [Fact]
    public async Task RejectedRequestsDoNotConsumeTheQuota()
    {
        _time.Now = Start + 100;
        await CountAllowed(10);
        for (var i = 0; i < 50; i++)
            await Acquire(10);

        _time.Now = Start + 1_500;

        Assert.Equal(5, await CountAllowed(10));
    }

    [Fact]
    public async Task FailedRefundStillRejectsTheRequest()
    {
        _time.Now = Start + 100;
        await CountAllowed(10);
        _storage.DecrementSucceeds = false;

        Assert.Equal(DistributedRateLimitStatus.Rejected, (await Acquire(10)).Status);
    }

    [Fact]
    public async Task DoesNotAllowABurstOfTwiceTheLimitAroundAWindowBoundary()
    {
        _time.Now = Start + 900;
        Assert.Equal(10, await CountAllowed(10));

        _time.Now = Start + 1_100;
        Assert.Equal(1, await CountAllowed(10));
    }

    [Fact]
    public async Task StartsFreshAfterBeingIdleForMoreThanAWindow()
    {
        _time.Now = Start + 900;
        await CountAllowed(10);

        _time.Now = Start + 3_500;

        Assert.Equal(10, await CountAllowed(10));
    }

    [Fact]
    public async Task RetryAfterIsExactWhenTheCurrentWindowIsFull()
    {
        _time.Now = Start + 100;
        await CountAllowed(10);
        _time.Now = Start + 200;
        var rejected = await Acquire(10);
        Assert.Equal(DistributedRateLimitStatus.Rejected, rejected.Status);

        _time.Now = Start + 200 + (long)rejected.RetryAfter.TotalMilliseconds - 50;
        Assert.Equal(DistributedRateLimitStatus.Rejected, (await Acquire(10)).Status);

        _time.Now = Start + 200 + (long)rejected.RetryAfter.TotalMilliseconds;
        Assert.Equal(DistributedRateLimitStatus.Allowed, (await Acquire(10)).Status);
    }

    [Fact]
    public async Task RetryAfterIsExactWhenOnlyThePreviousWindowIsInTheWay()
    {
        _time.Now = Start + 900;
        await CountAllowed(10);
        _time.Now = Start + 1_100;
        Assert.Equal(DistributedRateLimitStatus.Allowed, (await Acquire(10)).Status);
        var rejected = await Acquire(10);
        Assert.Equal(DistributedRateLimitStatus.Rejected, rejected.Status);

        _time.Now = Start + 1_100 + (long)rejected.RetryAfter.TotalMilliseconds - 20;
        Assert.Equal(DistributedRateLimitStatus.Rejected, (await Acquire(10)).Status);

        _time.Now = Start + 1_100 + (long)rejected.RetryAfter.TotalMilliseconds;
        Assert.Equal(DistributedRateLimitStatus.Allowed, (await Acquire(10)).Status);
    }

    [Fact]
    public async Task ALimitOfOneAdmitsOneRequestPerSlidingWindow()
    {
        _time.Now = Start + 900;
        Assert.Equal(DistributedRateLimitStatus.Allowed, (await Acquire(1)).Status);

        _time.Now = Start + 1_100;
        var rejected = await Acquire(1);
        Assert.Equal(DistributedRateLimitStatus.Rejected, rejected.Status);

        _time.Now = Start + 1_100 + (long)rejected.RetryAfter.TotalMilliseconds;
        Assert.Equal(DistributedRateLimitStatus.Allowed, (await Acquire(1)).Status);
    }

    [Fact]
    public async Task ConcurrentRequestsAdmitExactlyTheLimit()
    {
        _time.Now = Start + 100;

        var results = await Task.WhenAll(Enumerable.Range(0, 100).Select(_ => Acquire(25).AsTask()));

        Assert.Equal(25, results.Count(result => result.Status == DistributedRateLimitStatus.Allowed));
    }

    [Theory]
    [InlineData(0, 1_000)]
    [InlineData(10, 0)]
    public async Task RejectsInvalidArguments(int limit, int windowMilliseconds) =>
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            _limiter.AcquireAsync("user:a", limit, TimeSpan.FromMilliseconds(windowMilliseconds), default).AsTask());

    private ValueTask<DistributedRateLimitResult> Acquire(int limit) =>
        _limiter.AcquireAsync("user:a", limit, Window, default);

    private async Task<int> CountAllowed(int limit)
    {
        var allowed = 0;
        while (allowed < limit * 3 && (await Acquire(limit)).Status == DistributedRateLimitStatus.Allowed)
            allowed++;
        return allowed;
    }

    private sealed class ManualTime : TimeProvider
    {
        public long Now { get; set; }

        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeMilliseconds(Now);
    }

    private sealed class InMemoryStorage : IRateLimitCounterStorage
    {
        private readonly Dictionary<(string Key, long Window, long Index), long> _counters = [];

        public ValueTask<RateLimitCounter> IncrementAsync(
            string partitionKey, long windowMilliseconds, long windowIndex, CancellationToken cancellationToken)
        {
            lock (_counters)
            {
                var key = (partitionKey, windowMilliseconds, windowIndex);
                var current = _counters.GetValueOrDefault(key) + 1;
                _counters[key] = current;
                return ValueTask.FromResult(new RateLimitCounter(
                    current, _counters.GetValueOrDefault((partitionKey, windowMilliseconds, windowIndex - 1))));
            }
        }

        public bool DecrementSucceeds { get; set; } = true;

        public ValueTask<bool> DecrementAsync(string partitionKey, long windowMilliseconds, long windowIndex)
        {
            if (!DecrementSucceeds)
                return ValueTask.FromResult(false);

            lock (_counters)
                _counters[(partitionKey, windowMilliseconds, windowIndex)]--;
            return ValueTask.FromResult(true);
        }
    }
}

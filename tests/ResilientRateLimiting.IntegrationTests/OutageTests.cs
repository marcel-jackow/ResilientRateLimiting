using RedisRateLimiting;
using StackExchange.Redis;
using System.Threading.RateLimiting;
using Testcontainers.Redis;
using Xunit;

namespace ResilientRateLimiting.IntegrationTests;

public class OutageTests : IAsyncLifetime
{
    private readonly RedisContainer _container = new RedisBuilder("redis:7-alpine").Build();

    public async ValueTask InitializeAsync() => await _container.StartAsync();

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

    [Fact]
    public async Task A_store_that_dies_under_a_live_limiter_degrades_instead_of_throwing()
    {
        const int RequestsDuringOutage = 20;
        const int SharedLimit = 30;
        const int HealthyRequestsBeforeOutage = 1;
        var partitionKey = $"outage-{Guid.NewGuid():N}";
        var cancellationToken = TestContext.Current.CancellationToken;

        var connection = await ConnectionMultiplexer.ConnectAsync(_container.GetConnectionString());

        var options = new ResilientRateLimiterOptions
        {
            FallbackRecoveryTime = TimeSpan.FromMinutes(1),
        };

        var storeOptions = new StoreHealthOptions
        {
            ExpectedReplicaCount = 3,
            StoreTimeout = TimeSpan.FromMilliseconds(200),
            FailuresBeforeOpen = 2,
            BreakDuration = TimeSpan.FromSeconds(5),
            BreakerSamplingDuration = TimeSpan.FromSeconds(10),
        };

        using var limiter = new ResilientRateLimiter(
            primary: new RedisSlidingWindowRateLimiter<string>(partitionKey, new RedisSlidingWindowRateLimiterOptions
            {
                PermitLimit = SharedLimit,
                Window = TimeSpan.FromMinutes(1),
                ConnectionMultiplexerFactory = () => connection,
            }),
            fallback: new FixedWindowRateLimiter(new FixedWindowRateLimiterOptions
            {
                // The shared limit is written once; the per-replica budget follows from it.
                PermitLimit = storeOptions.LocalPermitLimit(SharedLimit),
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            }),
            options: options,
            storeHealth: new StoreHealth(storeOptions));

        using (var healthy = await limiter.AcquireAsync(1, cancellationToken))
        {
            Assert.True(healthy.IsAcquired);
            Assert.True(healthy.TryGetMetadata(ResilientRateLimitLease.SourceMetadata, out var source));
            Assert.Equal(LeaseSource.Distributed, source);
        }

        // Take the store away underneath a limiter that is already running.
        await _container.StopAsync(cancellationToken);

        var degraded = 0;
        var admitted = 0;

        for (var request = 0; request < RequestsDuringOutage; request++)
        {
            using var lease = await limiter.AcquireAsync(1, cancellationToken);

            Assert.True(lease.TryGetMetadata(ResilientRateLimitLease.SourceMetadata, out var source));

            if (source == LeaseSource.LocalFallback)
            {
                degraded++;
            }

            if (lease.IsAcquired)
            {
                admitted++;
            }
        }

        // The claim: every request was answered, and every one of them was degraded.
        Assert.Equal(RequestsDuringOutage, degraded);

        // And protection did not disappear: the local budget still bounded what got through.
        Assert.Equal(storeOptions.LocalPermitLimit(SharedLimit) - HealthyRequestsBeforeOutage, admitted);

        await connection.DisposeAsync();
    }
}

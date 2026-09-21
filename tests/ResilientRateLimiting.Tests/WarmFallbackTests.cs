using Microsoft.Extensions.Time.Testing;
using System.Threading.RateLimiting;
using Xunit;

namespace ResilientRateLimiting.Tests;

public class WarmFallbackTests
{
    private static ResilientRateLimiterOptions Options() => new()
    {
        ExpectedReplicaCount = 3,
        FallbackRecoveryTime = TimeSpan.FromMinutes(1),
    };

    [Fact]
    public async Task An_allowed_request_also_consumes_a_local_permit()
    {
        using var primary = new FakeRateLimiter(permitLimit: 10);
        using var fallback = new FakeRateLimiter(permitLimit: 10);
        using var limiter = new ResilientRateLimiter(primary, fallback, Options(), new FakeTimeProvider());
        var cancellationToken = TestContext.Current.CancellationToken;

        (await limiter.AcquireAsync(1, cancellationToken)).Dispose();
        (await limiter.AcquireAsync(1, cancellationToken)).Dispose();

        Assert.Equal(8, fallback.AvailablePermits);
    }

    [Fact]
    public async Task A_rejected_request_does_not_consume_a_local_permit()
    {
        using var primary = new FakeRateLimiter(permitLimit: 0);
        using var fallback = new FakeRateLimiter(permitLimit: 10);
        using var limiter = new ResilientRateLimiter(primary, fallback, Options(), new FakeTimeProvider());

        using var lease = await limiter.AcquireAsync(1, TestContext.Current.CancellationToken);

        Assert.False(lease.IsAcquired);
        Assert.Equal(10, fallback.AvailablePermits);
    }

    [Fact]
    public async Task The_fallback_starts_the_outage_already_warm()
    {
        var options = Options();
        using var primary = new FakeRateLimiter(permitLimit: 10).FailTimes(0);
        using var fallback = new FakeRateLimiter(permitLimit: 3);
        using var limiter = new ResilientRateLimiter(primary, fallback, options, new FakeTimeProvider());
        var cancellationToken = TestContext.Current.CancellationToken;

        for (var i = 0; i < 3; i++)
        {
            (await limiter.AcquireAsync(1, cancellationToken)).Dispose();
        }

        primary.AlwaysFail(new InvalidDataException("store down"));
        using var duringOutage = await limiter.AcquireAsync(1, cancellationToken);

        Assert.False(duringOutage.IsAcquired);
    }

    [Fact]
    public async Task An_allowed_multi_permit_request_consumes_the_same_count_locally()
    {
        using var primary = new FakeRateLimiter(permitLimit: 10);
        using var fallback = new FakeRateLimiter(permitLimit: 10);
        using var limiter = new ResilientRateLimiter(primary, fallback, Options(), new FakeTimeProvider());

        using var lease = await limiter.AcquireAsync(4, TestContext.Current.CancellationToken);

        Assert.True(lease.IsAcquired);
        Assert.Equal(6, fallback.AvailablePermits);
    }

    [Fact]
    public async Task An_allowed_request_above_the_local_budget_still_returns_the_store_lease()
    {
        using var primary = new FakeRateLimiter(permitLimit: 100);
        using var fallback = new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
        {
            TokenLimit = 34,
            TokensPerPeriod = 34,
            ReplenishmentPeriod = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = false,
        });
        using var limiter = new ResilientRateLimiter(primary, fallback, Options(), new FakeTimeProvider());

        using var lease = await limiter.AcquireAsync(50, TestContext.Current.CancellationToken);

        Assert.True(lease.IsAcquired);
        Assert.Equal(LeaseSource.Distributed, SourceOf(lease));
        Assert.Equal(50, primary.AvailablePermits);
    }

    [Fact]
    public async Task A_throwing_fallback_does_not_fail_a_request_the_store_allowed()
    {
        using var primary = new FakeRateLimiter(permitLimit: 10);
        using var fallback = new FakeRateLimiter(permitLimit: 10)
            .ThrowsOnAttemptAcquire(new InvalidOperationException("mirror broken"));
        using var limiter = new ResilientRateLimiter(primary, fallback, Options(), new FakeTimeProvider());

        using var lease = await limiter.AcquireAsync(1, TestContext.Current.CancellationToken);

        Assert.True(lease.IsAcquired);
        Assert.Equal(LeaseSource.Distributed, SourceOf(lease));
    }

    private static LeaseSource SourceOf(RateLimitLease lease)
    {
        Assert.True(lease.TryGetMetadata(ResilientRateLimitLease.SourceMetadata, out var source));
        return source;
    }
}

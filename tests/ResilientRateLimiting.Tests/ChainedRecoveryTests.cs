using Microsoft.Extensions.Time.Testing;
using System.Threading.RateLimiting;
using Xunit;

namespace ResilientRateLimiting.Tests;

public class ChainedRecoveryTests
{
    private static ResilientRateLimiterOptions Options(
        StoreFailureBehavior behavior = StoreFailureBehavior.LocalFallback) => new()
    {
        ExpectedReplicaCount = 3,
        FallbackRecoveryTime = TimeSpan.FromMinutes(1),
        FailuresBeforeOpen = 2,
        BreakDuration = TimeSpan.FromSeconds(5),
        BreakerSamplingDuration = TimeSpan.FromSeconds(10),
        FailureBehavior = behavior,
    };

    private static LeaseSource SourceOf(RateLimitLease lease)
    {
        Assert.True(lease.TryGetMetadata(ResilientRateLimitLease.SourceMetadata, out var source));
        return Assert.IsType<LeaseSource>(source);
    }

    [Fact]
    public async Task After_an_outage_an_exhausted_local_counter_rejects_without_calling_the_store()
    {
        var clock = new FakeTimeProvider();
        var options = Options();
        using var primary = new FakeRateLimiter(permitLimit: 1000).FailTimes(2, new InvalidDataException("blip"));
        using var fallback = new FakeRateLimiter(permitLimit: 1);
        using var limiter = new ResilientRateLimiter(primary, fallback, options, new StoreHealth(options, clock), clock);
        var cancellationToken = TestContext.Current.CancellationToken;

        // The outage consumes the single local permit, then the breaker opens.
        (await limiter.AcquireAsync(1, cancellationToken)).Dispose();
        (await limiter.AcquireAsync(1, cancellationToken)).Dispose();

        // The store comes back.
        clock.Advance(TimeSpan.FromSeconds(6));
        using var probe = await limiter.AcquireAsync(1, cancellationToken);
        Assert.Equal(LeaseSource.Distributed, SourceOf(probe));

        var attemptsAfterRecovery = primary.AcquireAttempts;

        using var suppressed = await limiter.AcquireAsync(1, cancellationToken);

        Assert.False(suppressed.IsAcquired);
        Assert.Equal(LeaseSource.LocalFallback, SourceOf(suppressed));
        Assert.Equal(attemptsAfterRecovery, primary.AcquireAttempts);
    }

    [Fact]
    public async Task Recovery_mode_ends_after_one_recovery_time()
    {
        var clock = new FakeTimeProvider();
        var options = Options();
        using var primary = new FakeRateLimiter(permitLimit: 1000).FailTimes(2, new InvalidDataException("blip"));
        using var fallback = new FakeRateLimiter(permitLimit: 1);
        using var limiter = new ResilientRateLimiter(primary, fallback, options, new StoreHealth(options, clock), clock);
        var cancellationToken = TestContext.Current.CancellationToken;

        (await limiter.AcquireAsync(1, cancellationToken)).Dispose();
        (await limiter.AcquireAsync(1, cancellationToken)).Dispose();

        clock.Advance(TimeSpan.FromSeconds(6));
        (await limiter.AcquireAsync(1, cancellationToken)).Dispose();

        clock.Advance(TimeSpan.FromMinutes(1));
        using var afterRecovery = await limiter.AcquireAsync(1, cancellationToken);

        Assert.True(afterRecovery.IsAcquired);
        Assert.Equal(LeaseSource.Distributed, SourceOf(afterRecovery));
    }

    [Fact]
    public async Task A_healthy_limiter_never_enters_recovery_mode()
    {
        var clock = new FakeTimeProvider();
        var options = Options();
        using var primary = new FakeRateLimiter(permitLimit: 1000);
        using var fallback = new FakeRateLimiter(permitLimit: 1);
        using var limiter = new ResilientRateLimiter(primary, fallback, options, new StoreHealth(options, clock), clock);
        var cancellationToken = TestContext.Current.CancellationToken;

        // More requests than the local budget: with recovery mode off, the store decides.
        for (var i = 0; i < 5; i++)
        {
            using var lease = await limiter.AcquireAsync(1, cancellationToken);
            Assert.True(lease.IsAcquired);
            Assert.Equal(LeaseSource.Distributed, SourceOf(lease));
        }
    }

    [Fact]
    public async Task A_fail_open_limiter_never_enters_recovery_mode_even_with_a_fallback()
    {
        var clock = new FakeTimeProvider();
        var options = Options(StoreFailureBehavior.FailOpen);
        using var primary = new FakeRateLimiter(permitLimit: 1000).FailTimes(2, new InvalidDataException("blip"));
        using var fallback = new FakeRateLimiter(permitLimit: 0);
        using var limiter = new ResilientRateLimiter(primary, fallback, options, new StoreHealth(options, clock), clock);
        var cancellationToken = TestContext.Current.CancellationToken;

        (await limiter.AcquireAsync(1, cancellationToken)).Dispose();
        (await limiter.AcquireAsync(1, cancellationToken)).Dispose();

        clock.Advance(TimeSpan.FromSeconds(6));
        (await limiter.AcquireAsync(1, cancellationToken)).Dispose();

        var attemptsBefore = primary.AcquireAttempts;
        using var next = await limiter.AcquireAsync(1, cancellationToken);

        Assert.True(next.IsAcquired);
        Assert.Equal(LeaseSource.Distributed, SourceOf(next));
        Assert.Equal(attemptsBefore + 1, primary.AcquireAttempts);
    }

    [Fact]
    public async Task Recovery_rejects_when_the_local_counter_cannot_cover_the_permit_count()
    {
        var clock = new FakeTimeProvider();
        var options = Options();
        using var primary = new FakeRateLimiter(permitLimit: 1000).FailTimes(2, new InvalidDataException("blip"));
        using var fallback = new FakeRateLimiter(permitLimit: 5);
        using var limiter = new ResilientRateLimiter(primary, fallback, options, new StoreHealth(options, clock), clock);
        var cancellationToken = TestContext.Current.CancellationToken;

        // Two outage requests take one permit each; the probe takes one more by warming.
        (await limiter.AcquireAsync(1, cancellationToken)).Dispose();
        (await limiter.AcquireAsync(1, cancellationToken)).Dispose();

        clock.Advance(TimeSpan.FromSeconds(6));
        (await limiter.AcquireAsync(1, cancellationToken)).Dispose();

        Assert.Equal(2, fallback.AvailablePermits);

        var attemptsBefore = primary.AcquireAttempts;
        using var tooLarge = await limiter.AcquireAsync(3, cancellationToken);

        Assert.False(tooLarge.IsAcquired);
        Assert.Equal(LeaseSource.LocalFallback, SourceOf(tooLarge));
        Assert.Equal(attemptsBefore, primary.AcquireAttempts);
        Assert.Equal(2, fallback.AvailablePermits);
    }

    [Fact]
    public async Task A_throwing_local_counter_during_recovery_falls_through_to_the_store()
    {
        var clock = new FakeTimeProvider();
        var options = Options();
        using var primary = new FakeRateLimiter(permitLimit: 1000).FailTimes(2, new InvalidDataException("blip"));
        using var fallback = new FakeRateLimiter(permitLimit: 1);
        using var limiter = new ResilientRateLimiter(primary, fallback, options, new StoreHealth(options, clock), clock);
        var cancellationToken = TestContext.Current.CancellationToken;

        (await limiter.AcquireAsync(1, cancellationToken)).Dispose();
        (await limiter.AcquireAsync(1, cancellationToken)).Dispose();

        clock.Advance(TimeSpan.FromSeconds(6));
        (await limiter.AcquireAsync(1, cancellationToken)).Dispose();

        fallback.ThrowsOnAttemptAcquire(new InvalidOperationException("the local counter is gone"));

        using var served = await limiter.AcquireAsync(1, cancellationToken);

        Assert.True(served.IsAcquired);
        Assert.Equal(LeaseSource.Distributed, SourceOf(served));
    }

    [Fact]
    public async Task Recovery_does_not_charge_the_local_counter_twice()
    {
        var clock = new FakeTimeProvider();
        var options = Options();
        using var primary = new FakeRateLimiter(permitLimit: 1000).FailTimes(2, new InvalidDataException("blip"));
        using var fallback = new FakeRateLimiter(permitLimit: 5);
        using var limiter = new ResilientRateLimiter(primary, fallback, options, new StoreHealth(options, clock), clock);
        var cancellationToken = TestContext.Current.CancellationToken;

        // Two outage requests take one permit each; the probe takes one more by warming.
        (await limiter.AcquireAsync(1, cancellationToken)).Dispose();
        (await limiter.AcquireAsync(1, cancellationToken)).Dispose();

        clock.Advance(TimeSpan.FromSeconds(6));
        (await limiter.AcquireAsync(1, cancellationToken)).Dispose();

        Assert.Equal(2, fallback.AvailablePermits);

        using var inRecovery = await limiter.AcquireAsync(1, cancellationToken);

        Assert.True(inRecovery.IsAcquired);
        Assert.Equal(LeaseSource.Distributed, SourceOf(inRecovery));
        Assert.Equal(1, fallback.AvailablePermits);
    }
}

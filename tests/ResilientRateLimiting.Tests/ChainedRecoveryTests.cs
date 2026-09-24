using Microsoft.Extensions.Time.Testing;
using System.Threading.RateLimiting;
using Xunit;

namespace ResilientRateLimiting.Tests;

public class ChainedRecoveryTests
{
    /// <summary>MaxAddedRetryDelay defaults to 60s (the record default) — not set explicitly here.</summary>
    private static ResilientRateLimiterOptions Options(
        StoreFailureBehavior behavior = StoreFailureBehavior.LocalFallback) => new()
    {
        FallbackRecoveryTime = TimeSpan.FromMinutes(1),
        FailureBehavior = behavior,
    };

    private static StoreHealthOptions StoreOptions() => new()
    {
        FailuresBeforeOpen = 2,
        BreakDuration = TimeSpan.FromSeconds(5),
        BreakerSamplingDuration = TimeSpan.FromSeconds(10),
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
        using var limiter = new ResilientRateLimiter(primary, fallback, options, new StoreHealth(StoreOptions(), clock), clock);
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
        Assert.Equal(LeaseSource.Recovery, SourceOf(suppressed));
        Assert.Equal(attemptsAfterRecovery, primary.AcquireAttempts);
    }

    [Fact]
    public async Task Recovery_mode_ends_after_one_recovery_time()
    {
        var clock = new FakeTimeProvider();
        var options = Options();
        using var primary = new FakeRateLimiter(permitLimit: 1000).FailTimes(2, new InvalidDataException("blip"));
        using var fallback = new FakeRateLimiter(permitLimit: 1);
        using var limiter = new ResilientRateLimiter(primary, fallback, options, new StoreHealth(StoreOptions(), clock), clock);
        var cancellationToken = TestContext.Current.CancellationToken;

        (await limiter.AcquireAsync(1, cancellationToken)).Dispose();
        (await limiter.AcquireAsync(1, cancellationToken)).Dispose();

        clock.Advance(TimeSpan.FromSeconds(6));
        (await limiter.AcquireAsync(1, cancellationToken)).Dispose();

        // Half way into the window the exhausted local counter still suppresses.
        clock.Advance(TimeSpan.FromSeconds(30));

        using (var suppressed = await limiter.AcquireAsync(1, cancellationToken))
        {
            Assert.False(suppressed.IsAcquired);
            Assert.Equal(LeaseSource.Recovery, SourceOf(suppressed));
        }

        clock.Advance(TimeSpan.FromSeconds(31));
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
        using var limiter = new ResilientRateLimiter(primary, fallback, options, new StoreHealth(StoreOptions(), clock), clock);
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
        using var limiter = new ResilientRateLimiter(primary, fallback, options, new StoreHealth(StoreOptions(), clock), clock);
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
        using var limiter = new ResilientRateLimiter(primary, fallback, options, new StoreHealth(StoreOptions(), clock), clock);
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
        Assert.Equal(LeaseSource.Recovery, SourceOf(tooLarge));
        Assert.Equal(attemptsBefore, primary.AcquireAttempts);
        Assert.Equal(2, fallback.AvailablePermits);
    }

    [Fact]
    public async Task Recovery_lets_the_store_answer_a_count_the_local_counter_can_never_grant()
    {
        var clock = new FakeTimeProvider();
        var options = Options();
        using var primary = new FakeRateLimiter(permitLimit: 1000).FailTimes(2, new InvalidDataException("blip"));
        using var fallback = new FixedWindowRateLimiter(new FixedWindowRateLimiterOptions
        {
            PermitLimit = 4,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = false,
        });
        using var limiter = new ResilientRateLimiter(primary, fallback, options, new StoreHealth(StoreOptions(), clock), clock);
        var cancellationToken = TestContext.Current.CancellationToken;

        (await limiter.AcquireAsync(1, cancellationToken)).Dispose();
        (await limiter.AcquireAsync(1, cancellationToken)).Dispose();

        clock.Advance(TimeSpan.FromSeconds(6));
        (await limiter.AcquireAsync(1, cancellationToken)).Dispose();

        var attemptsBefore = primary.AcquireAttempts;

        // The gate can never grant five permits out of a budget of four. The store still can, and
        // unlike the fallback path it is reachable here, so it decides.
        using var tooLarge = await limiter.AcquireAsync(5, cancellationToken);

        Assert.True(tooLarge.IsAcquired);
        Assert.Equal(LeaseSource.Distributed, SourceOf(tooLarge));
        Assert.Equal(attemptsBefore + 1, primary.AcquireAttempts);
    }

    [Fact]
    public async Task A_throwing_local_counter_during_recovery_falls_through_to_the_store()
    {
        var clock = new FakeTimeProvider();
        var options = Options();
        using var primary = new FakeRateLimiter(permitLimit: 1000).FailTimes(2, new InvalidDataException("blip"));
        using var fallback = new FakeRateLimiter(permitLimit: 1);
        using var limiter = new ResilientRateLimiter(primary, fallback, options, new StoreHealth(StoreOptions(), clock), clock);
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
        using var limiter = new ResilientRateLimiter(primary, fallback, options, new StoreHealth(StoreOptions(), clock), clock);
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

    [Fact]
    public async Task A_request_that_falls_back_during_recovery_is_charged_once()
    {
        var clock = new FakeTimeProvider();
        var options = Options();
        using var primary = new FakeRateLimiter(permitLimit: 1000).FailTimes(2, new InvalidDataException("blip"));
        using var fallback = new FakeRateLimiter(permitLimit: 5);
        using var limiter = new ResilientRateLimiter(primary, fallback, options, new StoreHealth(StoreOptions(), clock), clock);
        var cancellationToken = TestContext.Current.CancellationToken;

        (await limiter.AcquireAsync(1, cancellationToken)).Dispose();
        (await limiter.AcquireAsync(1, cancellationToken)).Dispose();

        clock.Advance(TimeSpan.FromSeconds(6));
        (await limiter.AcquireAsync(1, cancellationToken)).Dispose();

        Assert.Equal(2, fallback.AvailablePermits);

        // The store fails again while this limiter is in recovery: the gate already charged the permit.
        primary.AlwaysFail(new InvalidDataException("down again"));
        using var served = await limiter.AcquireAsync(1, cancellationToken);

        Assert.True(served.IsAcquired);
        Assert.Equal(LeaseSource.LocalFallback, SourceOf(served));
        Assert.Equal(1, fallback.AvailablePermits);
    }

    [Fact]
    public async Task A_fallback_older_than_one_recovery_time_does_not_arm_recovery()
    {
        var clock = new FakeTimeProvider();
        var options = Options();
        using var primary = new FakeRateLimiter(permitLimit: 1000).FailTimes(2, new InvalidDataException("blip"));
        using var fallback = new FakeRateLimiter(permitLimit: 1);
        using var limiter = new ResilientRateLimiter(primary, fallback, options, new StoreHealth(StoreOptions(), clock), clock);
        var cancellationToken = TestContext.Current.CancellationToken;

        (await limiter.AcquireAsync(1, cancellationToken)).Dispose();
        (await limiter.AcquireAsync(1, cancellationToken)).Dispose();

        // Nothing happens for far longer than the recovery time, then the store answers again.
        clock.Advance(TimeSpan.FromMinutes(5));
        (await limiter.AcquireAsync(1, cancellationToken)).Dispose();

        using var next = await limiter.AcquireAsync(1, cancellationToken);

        Assert.True(next.IsAcquired);
        Assert.Equal(LeaseSource.Distributed, SourceOf(next));
    }

    [Fact]
    public async Task A_recovery_rejection_carries_the_local_limiters_retry_after()
    {
        var clock = new FakeTimeProvider();
        var options = Options();
        using var primary = new FakeRateLimiter(permitLimit: 1000).FailTimes(2, new InvalidDataException("blip"));
        using var fallback = new FakeRateLimiter(permitLimit: 1) { RetryAfter = TimeSpan.FromSeconds(30) };
        using var limiter = new ResilientRateLimiter(primary, fallback, options, new StoreHealth(StoreOptions(), clock), clock);
        var cancellationToken = TestContext.Current.CancellationToken;

        (await limiter.AcquireAsync(1, cancellationToken)).Dispose();
        (await limiter.AcquireAsync(1, cancellationToken)).Dispose();

        clock.Advance(TimeSpan.FromSeconds(6));
        (await limiter.AcquireAsync(1, cancellationToken)).Dispose();

        using var suppressed = await limiter.AcquireAsync(1, cancellationToken);

        Assert.False(suppressed.IsAcquired);
        Assert.True(suppressed.TryGetMetadata(MetadataName.RetryAfter.Name, out var retryAfter));

        // 30 s local value, degraded, default 60 s cap: 30 + 30 doubling + 6..12 jitter.
        Assert.InRange((TimeSpan)retryAfter!, TimeSpan.FromSeconds(66), TimeSpan.FromSeconds(72));
    }

    [Fact]
    public async Task Concurrent_requests_during_recovery_admit_exactly_the_local_budget()
    {
        var clock = new FakeTimeProvider();
        var options = Options();
        using var primary = new FakeRateLimiter(permitLimit: 1000).FailTimes(2, new InvalidDataException("blip"));
        using var fallback = new FixedWindowRateLimiter(new FixedWindowRateLimiterOptions
        {
            PermitLimit = 10,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = false,
        });
        using var limiter = new ResilientRateLimiter(primary, fallback, options, new StoreHealth(StoreOptions(), clock), clock);
        var cancellationToken = TestContext.Current.CancellationToken;

        // The outage spends two permits, the probe warms a third: seven are left for the window.
        (await limiter.AcquireAsync(1, cancellationToken)).Dispose();
        (await limiter.AcquireAsync(1, cancellationToken)).Dispose();

        clock.Advance(TimeSpan.FromSeconds(6));
        (await limiter.AcquireAsync(1, cancellationToken)).Dispose();

        primary.AlwaysFail(new InvalidDataException("down again"));

        var leases = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => Task.Run(async () => await limiter.AcquireAsync(1, cancellationToken), cancellationToken)));

        var admitted = leases.Count(lease => lease.IsAcquired);

        foreach (var lease in leases)
        {
            Assert.Equal(lease.IsAcquired ? LeaseSource.LocalFallback : LeaseSource.Recovery, SourceOf(lease));
            lease.Dispose();
        }

        Assert.Equal(7, admitted);
    }
}

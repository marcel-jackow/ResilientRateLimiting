using Microsoft.Extensions.Time.Testing;
using System.Threading.RateLimiting;
using Xunit;

namespace ResilientRateLimiting.Tests;

/// <summary>One test per path through <see cref="ResilientRateLimiter"/>, asserting the added-delay band rather than an exact value: the decorator uses the default sampler.</summary>
public class RetryAfterWiringTests
{
    /// <summary>MaxAddedRetryDelay defaults to 60s (the record default) — not set explicitly here.</summary>
    private static ResilientRateLimiterOptions Options(
        StoreFailureBehavior behavior = StoreFailureBehavior.LocalFallback) => new()
    {
        FallbackRecoveryTime = TimeSpan.FromSeconds(60),
        FailureBehavior = behavior,
    };

    private static StoreHealthOptions StoreOptions() => new()
    {
        FailuresBeforeOpen = 2,
        BreakDuration = TimeSpan.FromSeconds(5),
        BreakerSamplingDuration = TimeSpan.FromSeconds(10),
    };

    private static TimeSpan? RetryAfterOf(RateLimitLease lease) =>
        lease.TryGetMetadata(MetadataName.RetryAfter.Name, out var value) ? (TimeSpan)value! : null;

    [Fact]
    public async Task An_acquired_distributed_lease_carries_no_retry_after()
    {
        var clock = new FakeTimeProvider();
        using var primary = new FakeRateLimiter(permitLimit: 1);
        using var fallback = new FakeRateLimiter(permitLimit: 1);
        using var limiter = new ResilientRateLimiter(primary, fallback, Options(), new StoreHealth(StoreOptions(), clock), clock);

        using var lease = await limiter.AcquireAsync(1, TestContext.Current.CancellationToken);

        Assert.True(lease.IsAcquired);
        Assert.Null(RetryAfterOf(lease));
    }

    [Fact]
    public async Task An_acquired_local_fallback_lease_carries_no_retry_after()
    {
        var clock = new FakeTimeProvider();
        using var primary = new FakeRateLimiter().AlwaysFail(new InvalidDataException("store down"));
        using var fallback = new FakeRateLimiter(permitLimit: 1);
        using var limiter = new ResilientRateLimiter(primary, fallback, Options(), new StoreHealth(StoreOptions(), clock), clock);

        using var lease = await limiter.AcquireAsync(1, TestContext.Current.CancellationToken);

        Assert.True(lease.IsAcquired);
        Assert.Null(RetryAfterOf(lease));
    }

    [Fact]
    public async Task A_distributed_rejection_gets_the_jittered_inner_value()
    {
        var clock = new FakeTimeProvider();
        using var primary = new FakeRateLimiter(permitLimit: 0) { RetryAfter = TimeSpan.FromSeconds(10) };
        using var fallback = new FakeRateLimiter(permitLimit: 1);
        using var limiter = new ResilientRateLimiter(primary, fallback, Options(), new StoreHealth(StoreOptions(), clock), clock);

        using var lease = await limiter.AcquireAsync(1, TestContext.Current.CancellationToken);

        Assert.False(lease.IsAcquired);
        // 10 s inner value, store up: a 1..3 s spread, no doubling. The sampler is random, so 11..13 s.
        Assert.InRange(RetryAfterOf(lease)!.Value, TimeSpan.FromSeconds(11), TimeSpan.FromSeconds(13));
    }

    [Fact]
    public async Task A_local_fallback_rejection_gets_the_degraded_jittered_value()
    {
        var clock = new FakeTimeProvider();
        using var primary = new FakeRateLimiter().AlwaysFail(new InvalidDataException("store down"));
        using var fallback = new FakeRateLimiter(permitLimit: 0) { RetryAfter = TimeSpan.FromSeconds(10) };
        using var limiter = new ResilientRateLimiter(primary, fallback, Options(), new StoreHealth(StoreOptions(), clock), clock);

        using var lease = await limiter.AcquireAsync(1, TestContext.Current.CancellationToken);

        Assert.False(lease.IsAcquired);
        // 10 s inner value, store down: a 2..6 s spread plus 10 s doubling. The sampler is random, so 22..26 s.
        Assert.InRange(RetryAfterOf(lease)!.Value, TimeSpan.FromSeconds(22), TimeSpan.FromSeconds(26));
    }

    [Fact]
    public async Task An_over_limit_local_fallback_rejection_gets_the_degraded_estimate()
    {
        // A real limiter throws above its whole limit and the rejection carries no inner value; the fake only refuses.
        var clock = new FakeTimeProvider();
        using var primary = new FakeRateLimiter().AlwaysFail(new InvalidDataException("store down"));
        using var fallback = new FixedWindowRateLimiter(new FixedWindowRateLimiterOptions
        {
            PermitLimit = 4,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = false,
        });
        using var limiter = new ResilientRateLimiter(primary, fallback, Options(), new StoreHealth(StoreOptions(), clock), clock);

        using var lease = await limiter.AcquireAsync(5, TestContext.Current.CancellationToken);

        Assert.False(lease.IsAcquired);
        Assert.True(lease.TryGetMetadata(ResilientRateLimitLease.SourceMetadata, out var source));
        Assert.Equal(LeaseSource.LocalFallback, source);
        // No inner value, so the base is FallbackRecoveryTime (60 s); store down: the spread (12..24 s) takes up to 24 s
        // of the 60 s cap, so the doubling gets the other 36 s. The sampler is random, so 60 + 36 + 12..24 = 108..120 s.
        Assert.InRange(RetryAfterOf(lease)!.Value, TimeSpan.FromSeconds(108), TimeSpan.FromSeconds(120));
    }

    [Fact]
    public async Task A_fail_closed_rejection_estimates_from_the_break_duration_when_recovery_time_is_unset()
    {
        // A fail-closed limiter need not set FallbackRecoveryTime, so the estimate falls back to BreakDuration.
        var clock = new FakeTimeProvider();
        var options = new ResilientRateLimiterOptions { FailureBehavior = StoreFailureBehavior.FailClosed };
        using var primary = new FakeRateLimiter().AlwaysFail(new InvalidDataException("store down"));
        using var limiter = new ResilientRateLimiter(primary, fallback: null, options, new StoreHealth(StoreOptions(), clock), clock);

        using var lease = await limiter.AcquireAsync(1, TestContext.Current.CancellationToken);

        Assert.False(lease.IsAcquired);
        // No inner value, so the base is BreakDuration (5 s); store down: a 2..6 s spread plus 5 s doubling. The sampler is random, so 12..16 s.
        Assert.InRange(RetryAfterOf(lease)!.Value, TimeSpan.FromSeconds(12), TimeSpan.FromSeconds(16));
    }

    [Fact]
    public async Task A_recovery_refusal_gets_the_degraded_jittered_local_value()
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
        // 30 s from the local limiter, store down: a 6..12 s spread plus 30 s doubling. The sampler is random, so 66..72 s.
        Assert.InRange(RetryAfterOf(suppressed)!.Value, TimeSpan.FromSeconds(66), TimeSpan.FromSeconds(72));
    }
}

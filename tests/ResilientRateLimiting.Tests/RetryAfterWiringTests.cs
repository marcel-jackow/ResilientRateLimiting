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
        // Not degraded: 10s plus extra in [max(10%,1s), max(20%,3s)] = [1s, 3s]: [11s, 13s].
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
        // Degraded, b=10s: lo=max(2,2)=2, hi=max(4,6)=6; hi'=min(6,60)=6, lo'=min(2,3)=2, jitter in [2s,6s].
        // doubling=min(10, 60-6)=10. 10 + 10 + [2,6] = [22s, 26s].
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
        // No inner value, so b = FallbackRecoveryTime 60s, degraded: lo=max(12,2)=12, hi=max(24,6)=24;
        // hi'=min(24,60)=24, lo'=min(12,12)=12, jitter in [12s,24s]. doubling=min(60, 60-24)=36
        // (the base is bigger than the room left over). 60 + 36 + [12,24] = [108s, 120s].
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
        // No inner value, so b = BreakDuration 5s, degraded: lo=max(1,2)=2, hi=max(2,6)=6; hi'=min(6,60)=6,
        // lo'=min(2,3)=2, jitter in [2s,6s]. doubling=min(5, 60-6)=5. 5 + 5 + [2,6] = [12s, 16s].
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
        // Degraded, b=30s (the local limiter's Retry-After): lo=max(6,2)=6, hi=max(12,6)=12; hi'=min(12,60)=12,
        // lo'=min(6,6)=6, jitter in [6s,12s]. doubling=min(30, 60-12)=30. 30 + 30 + [6,12] = [66s, 72s].
        Assert.InRange(RetryAfterOf(suppressed)!.Value, TimeSpan.FromSeconds(66), TimeSpan.FromSeconds(72));
    }
}

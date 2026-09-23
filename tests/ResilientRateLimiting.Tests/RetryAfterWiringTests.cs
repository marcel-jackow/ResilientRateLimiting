using Microsoft.Extensions.Time.Testing;
using System.Threading.RateLimiting;
using Xunit;

namespace ResilientRateLimiting.Tests;

/// <summary>One test per path through <see cref="ResilientRateLimiter"/>, asserting the jitter band rather than an exact value: the decorator uses the default sampler.</summary>
public class RetryAfterWiringTests
{
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
        // Not degraded: 10s with the normal jitter band (10-20%): [11s, 12s].
        Assert.InRange(RetryAfterOf(lease)!.Value, TimeSpan.FromSeconds(11), TimeSpan.FromSeconds(12));
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
        // Degraded: 10s doubles to 20s, then the degraded band (20-40%): [24s, 28s].
        Assert.InRange(RetryAfterOf(lease)!.Value, TimeSpan.FromSeconds(24), TimeSpan.FromSeconds(28));
    }

    [Fact]
    public async Task An_over_limit_local_fallback_rejection_gets_the_degraded_estimate()
    {
        // D90/D100: a permit count above the fallback's whole limit throws ArgumentOutOfRangeException
        // inside LocalMirror, which is served as StaticLease.Rejected — carrying no inner value. Must
        // drive a real limiter (ruling 7): FakeRateLimiter never throws above its limit.
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
        // No inner value, so the ordinary degraded estimate: FallbackRecoveryTime 60s doubles to
        // 120s, then the degraded band (20-40%): [144s, 168s].
        Assert.InRange(RetryAfterOf(lease)!.Value, TimeSpan.FromSeconds(144), TimeSpan.FromSeconds(168));
    }

    [Fact]
    public async Task A_fail_closed_rejection_estimates_from_the_break_duration_when_recovery_time_is_unset()
    {
        // D101: a fail-closed limiter need not set FallbackRecoveryTime, so the estimate must fall
        // back to StoreHealthOptions.BreakDuration rather than a bare "1 second" from zero.
        var clock = new FakeTimeProvider();
        var options = new ResilientRateLimiterOptions { FailureBehavior = StoreFailureBehavior.FailClosed };
        using var primary = new FakeRateLimiter().AlwaysFail(new InvalidDataException("store down"));
        using var limiter = new ResilientRateLimiter(primary, fallback: null, options, new StoreHealth(StoreOptions(), clock), clock);

        using var lease = await limiter.AcquireAsync(1, TestContext.Current.CancellationToken);

        Assert.False(lease.IsAcquired);
        // BreakDuration 5s doubles to 10s, then the degraded band (20-40%): [12s, 14s].
        Assert.InRange(RetryAfterOf(lease)!.Value, TimeSpan.FromSeconds(12), TimeSpan.FromSeconds(14));
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
        // Degraded: the local limiter's 30s doubles to 60s, then the degraded band (20-40%): [72s, 84s].
        Assert.InRange(RetryAfterOf(suppressed)!.Value, TimeSpan.FromSeconds(72), TimeSpan.FromSeconds(84));
    }
}

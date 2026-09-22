using Microsoft.Extensions.Time.Testing;
using System.Threading.RateLimiting;
using Xunit;

namespace ResilientRateLimiting.Tests;

public class StoreHealthTests
{
    private static ResilientRateLimiterOptions Options() => new()
    {
        FallbackRecoveryTime = TimeSpan.FromMinutes(1),
    };

    private static StoreHealthOptions StoreOptions() => new()
    {
        ExpectedReplicaCount = 3,
        FailuresBeforeOpen = 2,
        BreakerSamplingDuration = TimeSpan.FromSeconds(10),
        BreakDuration = TimeSpan.FromSeconds(5),
    };

    private static LeaseSource SourceOf(RateLimitLease lease)
    {
        Assert.True(lease.TryGetMetadata(ResilientRateLimitLease.SourceMetadata, out var source));
        return source;
    }

    [Fact]
    public async Task A_predicate_that_excuses_the_exception_keeps_the_breaker_closed()
    {
        var clock = new FakeTimeProvider();
        var health = new StoreHealth(
            new StoreHealthOptions
            {
                ExpectedReplicaCount = 3,
                FailuresBeforeOpen = 2,
                BreakerSamplingDuration = TimeSpan.FromSeconds(10),
                BreakDuration = TimeSpan.FromSeconds(5),
                ShouldHandle = _ => false,
            },
            clock);

        using var primary = new FakeRateLimiter().AlwaysFail(new InvalidDataException("store down"));
        using var fallback = new FakeRateLimiter(permitLimit: 100);
        using var limiter = new ResilientRateLimiter(primary, fallback, Options(), health, clock);

        // Nothing is a store failure, so the breaker scores no failures and never opens: the store
        // is asked every time, and the exception reaches the caller every time.
        for (var i = 0; i < 4; i++)
        {
            await Assert.ThrowsAsync<InvalidDataException>(
                async () => await limiter.AcquireAsync(1, TestContext.Current.CancellationToken));
        }

        Assert.Equal(4, primary.AcquireAttempts);
    }

    [Fact]
    public async Task Stops_calling_the_store_once_the_breaker_opens()
    {
        var clock = new FakeTimeProvider();
        var options = Options();
        var health = new StoreHealth(StoreOptions(), clock);
        using var primary = new FakeRateLimiter().AlwaysFail(new InvalidDataException("store down"));
        using var fallback = new FakeRateLimiter(permitLimit: 100);
        using var limiter = new ResilientRateLimiter(primary, fallback, options, health, clock);

        for (var i = 0; i < 2; i++)
        {
            (await limiter.AcquireAsync(1, TestContext.Current.CancellationToken)).Dispose();
        }

        var attemptsWhenOpened = primary.AcquireAttempts;
        using var afterBreak = await limiter.AcquireAsync(1, TestContext.Current.CancellationToken);

        Assert.Equal(LeaseSource.LocalFallback, SourceOf(afterBreak));
        Assert.Equal(attemptsWhenOpened, primary.AcquireAttempts);
    }

    [Fact]
    public async Task Failures_on_one_partition_open_the_breaker_for_another()
    {
        var clock = new FakeTimeProvider();
        var options = Options();
        var health = new StoreHealth(StoreOptions(), clock);

        using var failingPrimary = new FakeRateLimiter().AlwaysFail(new InvalidDataException("store down"));
        using var healthyPrimary = new FakeRateLimiter(permitLimit: 100);
        using var fallbackA = new FakeRateLimiter(permitLimit: 100);
        using var fallbackB = new FakeRateLimiter(permitLimit: 100);

        using var partitionA = new ResilientRateLimiter(failingPrimary, fallbackA, options, health, clock);
        using var partitionB = new ResilientRateLimiter(healthyPrimary, fallbackB, options, health, clock);

        for (var i = 0; i < 2; i++)
        {
            (await partitionA.AcquireAsync(1, TestContext.Current.CancellationToken)).Dispose();
        }

        using var lease = await partitionB.AcquireAsync(1, TestContext.Current.CancellationToken);

        Assert.Equal(LeaseSource.LocalFallback, SourceOf(lease));
        Assert.Equal(0, healthyPrimary.AcquireAttempts);
    }

    [Fact]
    public async Task Probes_the_store_again_after_the_break_duration()
    {
        var clock = new FakeTimeProvider();
        var options = Options();
        var health = new StoreHealth(StoreOptions(), clock);
        using var primary = new FakeRateLimiter(permitLimit: 100).FailTimes(2, new InvalidDataException("blip"));
        using var fallback = new FakeRateLimiter(permitLimit: 100);
        using var limiter = new ResilientRateLimiter(primary, fallback, options, health, clock);

        for (var i = 0; i < 2; i++)
        {
            (await limiter.AcquireAsync(1, TestContext.Current.CancellationToken)).Dispose();
        }

        var attemptsWhenOpened = primary.AcquireAttempts;

        using (var whileOpen = await limiter.AcquireAsync(1, TestContext.Current.CancellationToken))
        {
            Assert.Equal(LeaseSource.LocalFallback, SourceOf(whileOpen));
        }

        Assert.Equal(attemptsWhenOpened, primary.AcquireAttempts);

        clock.Advance(TimeSpan.FromSeconds(6));
        using var probe = await limiter.AcquireAsync(1, TestContext.Current.CancellationToken);

        Assert.Equal(LeaseSource.Distributed, SourceOf(probe));
        Assert.Equal(attemptsWhenOpened + 1, primary.AcquireAttempts);
    }

    [Fact]
    public async Task Keeps_the_breaker_closed_when_one_success_dilutes_the_default_ratio()
    {
        var clock = new FakeTimeProvider();
        var options = Options();
        var storeOptions = StoreOptions();
        var health = new StoreHealth(storeOptions, clock);
        using var primary = new FakeRateLimiter(permitLimit: 100);
        using var fallback = new FakeRateLimiter(permitLimit: 100);
        using var limiter = new ResilientRateLimiter(primary, fallback, options, health, clock);

        (await limiter.AcquireAsync(1, TestContext.Current.CancellationToken)).Dispose();
        primary.AlwaysFail(new InvalidDataException("store down"));

        for (var i = 0; i < storeOptions.FailuresBeforeOpen; i++)
        {
            (await limiter.AcquireAsync(1, TestContext.Current.CancellationToken)).Dispose();
        }

        var attemptsAfterFailures = primary.AcquireAttempts;
        (await limiter.AcquireAsync(1, TestContext.Current.CancellationToken)).Dispose();

        Assert.Equal(attemptsAfterFailures + 1, primary.AcquireAttempts);
    }

    [Fact]
    public async Task Opens_on_the_same_mixed_sequence_when_the_failure_ratio_is_lowered()
    {
        var clock = new FakeTimeProvider();
        var options = Options();
        var storeOptions = new StoreHealthOptions
        {
            ExpectedReplicaCount = 3,
            FailuresBeforeOpen = 2,
            BreakerSamplingDuration = TimeSpan.FromSeconds(10),
            BreakDuration = TimeSpan.FromSeconds(5),
            FailureRatio = 0.5,
        };
        var health = new StoreHealth(storeOptions, clock);
        using var primary = new FakeRateLimiter(permitLimit: 100);
        using var fallback = new FakeRateLimiter(permitLimit: 100);
        using var limiter = new ResilientRateLimiter(primary, fallback, options, health, clock);

        (await limiter.AcquireAsync(1, TestContext.Current.CancellationToken)).Dispose();
        primary.AlwaysFail(new InvalidDataException("store down"));

        for (var i = 0; i < storeOptions.FailuresBeforeOpen; i++)
        {
            (await limiter.AcquireAsync(1, TestContext.Current.CancellationToken)).Dispose();
        }

        var attemptsWhenOpened = primary.AcquireAttempts;
        using var afterBreak = await limiter.AcquireAsync(1, TestContext.Current.CancellationToken);

        Assert.Equal(LeaseSource.LocalFallback, SourceOf(afterBreak));
        Assert.Equal(attemptsWhenOpened, primary.AcquireAttempts);
    }
}

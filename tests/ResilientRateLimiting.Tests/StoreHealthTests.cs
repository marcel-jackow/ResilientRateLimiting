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

    [Fact]
    public async Task Same_type_failing_with_gaps_shorter_than_the_window_is_reported_once()
    {
        var clock = new FakeTimeProvider();
        var options = Options();
        var reported = new List<Exception>();
        var storeOptions = new StoreHealthOptions
        {
            FailuresBeforeOpen = 100, // never opens: isolates the quiet/closed rule from the breaker
            BreakerSamplingDuration = TimeSpan.FromSeconds(10),
            BreakDuration = TimeSpan.FromSeconds(5),
            OnStoreFailure = reported.Add,
        };
        var health = new StoreHealth(storeOptions, clock);
        using var primary = new FakeRateLimiter(permitLimit: 100).AlwaysFail(new InvalidDataException("store down"));
        using var fallback = new FakeRateLimiter(permitLimit: 100);
        using var limiter = new ResilientRateLimiter(primary, fallback, options, health, clock);

        for (var i = 0; i < 4; i++)
        {
            (await limiter.AcquireAsync(1, TestContext.Current.CancellationToken)).Dispose();
            clock.Advance(TimeSpan.FromSeconds(1));
        }

        Assert.Single(reported);
    }

    [Fact]
    public async Task Same_type_is_reported_again_once_quiet_for_the_window_but_not_just_below_it()
    {
        var clock = new FakeTimeProvider();
        var options = Options();
        var reported = new List<Exception>();
        var storeOptions = new StoreHealthOptions
        {
            FailuresBeforeOpen = 100,
            BreakerSamplingDuration = TimeSpan.FromSeconds(10),
            BreakDuration = TimeSpan.FromSeconds(5),
            OnStoreFailure = reported.Add,
        };
        var health = new StoreHealth(storeOptions, clock);
        using var primary = new FakeRateLimiter(permitLimit: 100).AlwaysFail(new InvalidDataException("store down"));
        using var fallback = new FakeRateLimiter(permitLimit: 100);
        using var limiter = new ResilientRateLimiter(primary, fallback, options, health, clock);

        (await limiter.AcquireAsync(1, TestContext.Current.CancellationToken)).Dispose();
        Assert.Single(reported);

        // Just below the window since the last failure: still not reported.
        clock.Advance(TimeSpan.FromSeconds(9));
        (await limiter.AcquireAsync(1, TestContext.Current.CancellationToken)).Dispose();
        Assert.Single(reported);

        // At or past the window since THAT failure: reported again.
        clock.Advance(TimeSpan.FromSeconds(10));
        (await limiter.AcquireAsync(1, TestContext.Current.CancellationToken)).Dispose();
        Assert.Equal(2, reported.Count);
    }

    [Fact]
    public async Task A_breaker_close_reports_the_same_type_again_even_within_the_window()
    {
        var clock = new FakeTimeProvider();
        var options = Options();
        var reported = new List<Exception>();
        var storeOptions = new StoreHealthOptions
        {
            FailuresBeforeOpen = 2,
            BreakerSamplingDuration = TimeSpan.FromSeconds(10),
            BreakDuration = TimeSpan.FromSeconds(5),
            OnStoreFailure = reported.Add,
        };
        var health = new StoreHealth(storeOptions, clock);
        using var primary = new FakeRateLimiter(permitLimit: 100).AlwaysFail(new InvalidDataException("store down"));
        using var fallback = new FakeRateLimiter(permitLimit: 100);
        using var limiter = new ResilientRateLimiter(primary, fallback, options, health, clock);

        for (var i = 0; i < 2; i++)
        {
            (await limiter.AcquireAsync(1, TestContext.Current.CancellationToken)).Dispose();
        }

        Assert.Single(reported);

        // Past BreakDuration (5s) but still well inside BreakerSamplingDuration (10s) since the
        // last failure: a successful probe closes the breaker.
        clock.Advance(TimeSpan.FromSeconds(6));
        primary.FailTimes(0);
        using var probe = await limiter.AcquireAsync(1, TestContext.Current.CancellationToken);
        Assert.Equal(LeaseSource.Distributed, SourceOf(probe));
        Assert.Single(reported);

        // The same type fails again, only 6s after its last failure (< 10s window): reported
        // again anyway, because the breaker closed since then.
        primary.AlwaysFail(new InvalidDataException("store down again"));
        (await limiter.AcquireAsync(1, TestContext.Current.CancellationToken)).Dispose();

        Assert.Equal(2, reported.Count);
    }

    [Fact]
    public async Task Intermittent_failures_separated_by_more_than_the_window_are_reported_each_time()
    {
        var clock = new FakeTimeProvider();
        var options = Options();
        var reported = new List<Exception>();
        var storeOptions = StoreOptions() with { OnStoreFailure = reported.Add };
        var health = new StoreHealth(storeOptions, clock);
        using var primary = new FakeRateLimiter(permitLimit: 100).AlwaysFail(new InvalidDataException("blip"));
        using var fallback = new FakeRateLimiter(permitLimit: 100);
        using var limiter = new ResilientRateLimiter(primary, fallback, options, health, clock);

        // Each failure is alone in the breaker's own sampling window, so it never opens
        // (MinimumThroughput is never reached within one window) - yet each is reported.
        for (var i = 0; i < 3; i++)
        {
            (await limiter.AcquireAsync(1, TestContext.Current.CancellationToken)).Dispose();
            clock.Advance(TimeSpan.FromSeconds(11));
        }

        Assert.Equal(3, reported.Count);
    }

    [Fact]
    public async Task Different_exception_types_are_tracked_independently()
    {
        var clock = new FakeTimeProvider();
        var options = Options();
        var reported = new List<Exception>();
        var storeOptions = new StoreHealthOptions
        {
            FailuresBeforeOpen = 100,
            BreakerSamplingDuration = TimeSpan.FromSeconds(10),
            BreakDuration = TimeSpan.FromSeconds(5),
            OnStoreFailure = reported.Add,
        };
        var health = new StoreHealth(storeOptions, clock);
        using var primary = new FakeRateLimiter(permitLimit: 100).AlwaysFail(new InvalidDataException("store down"));
        using var fallback = new FakeRateLimiter(permitLimit: 100);
        using var limiter = new ResilientRateLimiter(primary, fallback, options, health, clock);

        (await limiter.AcquireAsync(1, TestContext.Current.CancellationToken)).Dispose();

        primary.AlwaysFail(new IOException("socket closed"));
        (await limiter.AcquireAsync(1, TestContext.Current.CancellationToken)).Dispose();

        primary.AlwaysFail(new InvalidDataException("store down again"));
        (await limiter.AcquireAsync(1, TestContext.Current.CancellationToken)).Dispose();

        primary.AlwaysFail(new IOException("socket closed again"));
        (await limiter.AcquireAsync(1, TestContext.Current.CancellationToken)).Dispose();

        Assert.Equal(2, reported.Count);
        Assert.Single(reported, exception => exception is InvalidDataException);
        Assert.Single(reported, exception => exception is IOException);
    }

    [Fact]
    public void Concurrent_failures_of_a_new_type_are_reported_exactly_once()
    {
        var clock = new FakeTimeProvider();
        var reportCount = 0;
        var health = new StoreHealth(
            new StoreHealthOptions { OnStoreFailure = _ => Interlocked.Increment(ref reportCount) },
            clock);

        const int concurrency = 16;
        using var barrier = new Barrier(concurrency);

        // Dedicated threads, not Task.Run: the pool must stay free on small CI agents.
        var threads = Enumerable.Range(0, concurrency).Select(_ => new Thread(() =>
        {
            barrier.SignalAndWait();
            health.ReportFirstOccurrence(new InvalidDataException("race"));
        })).ToArray();

        foreach (var thread in threads)
        {
            thread.Start();
        }

        JoinAll(threads);

        Assert.Equal(1, reportCount);
    }

    [Fact]
    public void Concurrent_failures_of_an_already_quiet_type_are_reported_exactly_once()
    {
        var clock = new FakeTimeProvider();
        var reportCount = 0;
        var health = new StoreHealth(
            new StoreHealthOptions
            {
                BreakerSamplingDuration = TimeSpan.FromSeconds(10),
                OnStoreFailure = _ => Interlocked.Increment(ref reportCount),
            },
            clock);

        // Seed one first sighting (reports once), then go quiet past the window: any further
        // failure of this type is due to be reported again, so N threads race the SAME
        // decide-and-store step for that single due report.
        health.ReportFirstOccurrence(new InvalidDataException("seed"));
        clock.Advance(TimeSpan.FromSeconds(11));

        const int concurrency = 16;
        using var barrier = new Barrier(concurrency);

        // Dedicated threads, not Task.Run: the pool must stay free on small CI agents.
        var threads = Enumerable.Range(0, concurrency).Select(_ => new Thread(() =>
        {
            barrier.SignalAndWait();
            health.ReportFirstOccurrence(new InvalidDataException("race"));
        })).ToArray();

        foreach (var thread in threads)
        {
            thread.Start();
        }

        JoinAll(threads);

        Assert.Equal(2, reportCount);
    }

    /// <summary>Joins every participant, failing (not hanging) if one does not finish in time.</summary>
    private static void JoinAll(Thread[] threads)
    {
        foreach (var thread in threads)
        {
            Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "a participant thread did not finish in time");
        }
    }
}

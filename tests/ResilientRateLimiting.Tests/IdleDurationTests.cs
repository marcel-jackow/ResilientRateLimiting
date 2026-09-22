using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace ResilientRateLimiting.Tests;

public class IdleDurationTests
{
    private readonly FakeTimeProvider _clock = new();

    private static ResilientRateLimiterOptions Options() => new()
    {
        ExpectedReplicaCount = 3,
        FallbackRecoveryTime = TimeSpan.FromMinutes(1),
        MaxWarmRetention = TimeSpan.FromMinutes(2),
    };

    [Fact]
    public void Reports_its_own_time_since_construction_before_any_request_is_served()
    {
        using var primary = new FakeRateLimiter(permitLimit: 10) { ReportedIdleDuration = TimeSpan.FromMinutes(5) };
        using var fallback = new FakeRateLimiter(permitLimit: 10);
        using var limiter = new ResilientRateLimiter(primary, fallback, Options(), new StoreHealth(Options(), _clock), _clock);

        _clock.Advance(TimeSpan.FromSeconds(11));

        Assert.Equal(TimeSpan.FromSeconds(11), limiter.IdleDuration);
    }

    [Fact]
    public async Task Reports_its_own_elapsed_time_when_the_first_request_throws_and_the_primary_getter_is_broken()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        using var primary = new FakeRateLimiter(permitLimit: 10)
            .ThrowsOnIdleDuration(new InvalidOperationException("primary idle clock is unusable"));
        using var fallback = new FakeRateLimiter(permitLimit: 10);
        using var limiter = new ResilientRateLimiter(primary, fallback, Options(), new StoreHealth(Options(), _clock), _clock);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await limiter.AcquireAsync(1, cancelled.Token));

        _clock.Advance(TimeSpan.FromSeconds(11));

        Assert.Equal(TimeSpan.FromSeconds(11), limiter.IdleDuration);
        Assert.Equal(0, primary.IdleDurationReads);
    }

    [Fact]
    public async Task Reports_not_idle_while_local_state_is_still_held()
    {
        var clock = new FakeTimeProvider();
        using var primary = new FakeRateLimiter(permitLimit: 10);
        using var fallback = new FakeRateLimiter(permitLimit: 10);
        using var limiter = new ResilientRateLimiter(primary, fallback, Options(), new StoreHealth(Options(), clock), clock);

        (await limiter.AcquireAsync(1, TestContext.Current.CancellationToken)).Dispose();
        clock.Advance(TimeSpan.FromSeconds(30));

        Assert.Null(limiter.IdleDuration);
    }

    [Fact]
    public async Task Reports_its_own_elapsed_time_once_the_local_state_has_decayed()
    {
        var clock = new FakeTimeProvider();
        using var primary = new FakeRateLimiter(permitLimit: 10);
        using var fallback = new FakeRateLimiter(permitLimit: 10);
        using var limiter = new ResilientRateLimiter(primary, fallback, Options(), new StoreHealth(Options(), clock), clock);

        (await limiter.AcquireAsync(1, TestContext.Current.CancellationToken)).Dispose();
        clock.Advance(TimeSpan.FromSeconds(61));

        Assert.Equal(TimeSpan.FromSeconds(61), limiter.IdleDuration);
    }

    [Fact]
    public async Task Reports_its_own_elapsed_time_exactly_at_the_retention_boundary()
    {
        var clock = new FakeTimeProvider();
        using var primary = new FakeRateLimiter(permitLimit: 10);
        using var fallback = new FakeRateLimiter(permitLimit: 10);
        using var limiter = new ResilientRateLimiter(primary, fallback, Options(), new StoreHealth(Options(), clock), clock);

        (await limiter.AcquireAsync(1, TestContext.Current.CancellationToken)).Dispose();
        clock.Advance(TimeSpan.FromMinutes(1));

        Assert.Equal(TimeSpan.FromMinutes(1), limiter.IdleDuration);
    }

    [Fact]
    public async Task Reports_not_idle_while_serving_from_the_fallback_during_an_outage()
    {
        var clock = new FakeTimeProvider();
        using var primary = new FakeRateLimiter().AlwaysFail(new InvalidDataException("store down"));
        using var fallback = new FakeRateLimiter(permitLimit: 10);
        using var limiter = new ResilientRateLimiter(primary, fallback, Options(), new StoreHealth(Options(), clock), clock);

        (await limiter.AcquireAsync(1, TestContext.Current.CancellationToken)).Dispose();
        clock.Advance(TimeSpan.FromSeconds(30));

        Assert.Null(limiter.IdleDuration);
    }

    [Fact]
    public async Task Caps_retention_at_the_configured_maximum()
    {
        var clock = new FakeTimeProvider();
        var options = Options();
        options.FallbackRecoveryTime = TimeSpan.FromHours(24);
        options.MaxWarmRetention = TimeSpan.FromMinutes(2);

        using var primary = new FakeRateLimiter(permitLimit: 10);
        using var fallback = new FakeRateLimiter(permitLimit: 10);
        using var limiter = new ResilientRateLimiter(primary, fallback, options, new StoreHealth(options, clock), clock);

        (await limiter.AcquireAsync(1, TestContext.Current.CancellationToken)).Dispose();

        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Null(limiter.IdleDuration);

        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(TimeSpan.FromMinutes(3), limiter.IdleDuration);
    }

    [Fact]
    public async Task Reports_not_idle_while_a_request_is_still_in_flight()
    {
        var clock = new FakeTimeProvider();
        var options = Options();
        options.MaxWarmRetention = TimeSpan.FromSeconds(1);

        using var primary = new FakeRateLimiter(permitLimit: 10);
        using var fallback = new FakeRateLimiter(permitLimit: 10);
        using var limiter = new ResilientRateLimiter(primary, fallback, options, new StoreHealth(options, clock), clock);

        (await limiter.AcquireAsync(1, TestContext.Current.CancellationToken)).Dispose();
        clock.Advance(TimeSpan.FromSeconds(11));

        Assert.Equal(TimeSpan.FromSeconds(11), limiter.IdleDuration);

        primary.HangUntilReleased();
        var pending = limiter.AcquireAsync(1, TestContext.Current.CancellationToken).AsTask();

        Assert.Null(limiter.IdleDuration);

        primary.Release();
        (await pending).Dispose();
    }

    [Fact]
    public async Task Reports_its_own_activity_while_an_open_breaker_leaves_the_primary_untouched()
    {
        var clock = new FakeTimeProvider();
        var options = Options();
        options.FailuresBeforeOpen = 2;
        options.BreakDuration = TimeSpan.FromMinutes(10);
        options.MaxWarmPartitions = 1;
        var health = new StoreHealth(options, clock);

        using var primary = new FakeRateLimiter { ReportedIdleDuration = TimeSpan.FromMinutes(5) }
            .AlwaysFail(new InvalidDataException("store down"));
        using var fallback = new FakeRateLimiter(permitLimit: 100);
        using var limiter = new ResilientRateLimiter(primary, fallback, options, health, clock);

        using var other = new ResilientRateLimiter(
            new FakeRateLimiter(permitLimit: 10), new FakeRateLimiter(permitLimit: 10), options, health, clock);

        for (var i = 0; i < 2; i++)
        {
            (await limiter.AcquireAsync(1, TestContext.Current.CancellationToken)).Dispose();
        }

        var attemptsWhenOpened = primary.AcquireAttempts;
        clock.Advance(TimeSpan.FromSeconds(30));
        (await limiter.AcquireAsync(1, TestContext.Current.CancellationToken)).Dispose();

        Assert.Equal(attemptsWhenOpened, primary.AcquireAttempts);
        Assert.Equal(TimeSpan.Zero, limiter.IdleDuration);
    }
}

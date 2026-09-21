using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace ResilientRateLimiting.Tests;

public class IdleDurationTests
{
    private static ResilientRateLimiterOptions Options() => new()
    {
        ExpectedReplicaCount = 3,
        FallbackRecoveryTime = TimeSpan.FromMinutes(1),
        MaxWarmRetention = TimeSpan.FromMinutes(2),
    };

    [Fact]
    public void Forwards_the_inner_value_before_any_local_state_exists()
    {
        using var primary = new FakeRateLimiter(permitLimit: 10);
        using var fallback = new FakeRateLimiter(permitLimit: 10);
        using var limiter = new ResilientRateLimiter(primary, fallback, Options(), new FakeTimeProvider());

        Assert.Equal(primary.IdleDuration, limiter.IdleDuration);
    }

    [Fact]
    public async Task Reports_not_idle_while_local_state_is_still_held()
    {
        var clock = new FakeTimeProvider();
        using var primary = new FakeRateLimiter(permitLimit: 10);
        using var fallback = new FakeRateLimiter(permitLimit: 10);
        using var limiter = new ResilientRateLimiter(primary, fallback, Options(), clock);

        (await limiter.AcquireAsync(1, TestContext.Current.CancellationToken)).Dispose();
        clock.Advance(TimeSpan.FromSeconds(30));

        Assert.Null(limiter.IdleDuration);
    }

    [Fact]
    public async Task Reports_honestly_once_the_local_state_has_decayed()
    {
        var clock = new FakeTimeProvider();
        using var primary = new FakeRateLimiter(permitLimit: 10);
        using var fallback = new FakeRateLimiter(permitLimit: 10);
        using var limiter = new ResilientRateLimiter(primary, fallback, Options(), clock);

        (await limiter.AcquireAsync(1, TestContext.Current.CancellationToken)).Dispose();
        clock.Advance(TimeSpan.FromSeconds(61));

        Assert.Equal(primary.IdleDuration, limiter.IdleDuration);
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
        using var limiter = new ResilientRateLimiter(primary, fallback, options, clock);

        (await limiter.AcquireAsync(1, TestContext.Current.CancellationToken)).Dispose();

        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Null(limiter.IdleDuration);

        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(primary.IdleDuration, limiter.IdleDuration);
    }
}

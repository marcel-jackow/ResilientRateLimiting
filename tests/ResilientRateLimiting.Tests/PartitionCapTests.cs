using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace ResilientRateLimiting.Tests;

public class PartitionCapTests
{
    private static ResilientRateLimiterOptions Options(int maxWarmPartitions) => new()
    {
        ExpectedReplicaCount = 3,
        FallbackRecoveryTime = TimeSpan.FromMinutes(1),
        MaxWarmPartitions = maxWarmPartitions,
    };

    [Fact]
    public async Task Answers_honestly_once_the_partition_cap_is_passed()
    {
        var clock = new FakeTimeProvider();
        var options = Options(maxWarmPartitions: 2);
        using var health = new StoreHealth(options, clock);

        var limiters = new List<ResilientRateLimiter>();

        for (var i = 0; i < 3; i++)
        {
            var primary = new FakeRateLimiter(permitLimit: 10);
            var fallback = new FakeRateLimiter(permitLimit: 10);
            limiters.Add(new ResilientRateLimiter(primary, fallback, options, clock, health));
        }

        foreach (var limiter in limiters)
        {
            (await limiter.AcquireAsync(1, TestContext.Current.CancellationToken)).Dispose();
        }

        Assert.All(limiters, limiter => Assert.NotNull(limiter.IdleDuration));

        limiters[2].Dispose();

        Assert.Null(limiters[0].IdleDuration);

        foreach (var limiter in limiters.Take(2))
        {
            limiter.Dispose();
        }
    }

    [Fact]
    public async Task Releases_the_partition_when_disposed_asynchronously()
    {
        var clock = new FakeTimeProvider();
        var options = Options(maxWarmPartitions: 1);
        using var health = new StoreHealth(options, clock);

        var primaryOne = new FakeRateLimiter(permitLimit: 10);
        var fallbackOne = new FakeRateLimiter(permitLimit: 10);
        var limiterOne = new ResilientRateLimiter(primaryOne, fallbackOne, options, clock, health);

        var primaryTwo = new FakeRateLimiter(permitLimit: 10);
        var fallbackTwo = new FakeRateLimiter(permitLimit: 10);
        var limiterTwo = new ResilientRateLimiter(primaryTwo, fallbackTwo, options, clock, health);

        (await limiterOne.AcquireAsync(1, TestContext.Current.CancellationToken)).Dispose();
        (await limiterTwo.AcquireAsync(1, TestContext.Current.CancellationToken)).Dispose();

        Assert.NotNull(limiterOne.IdleDuration);

        await limiterTwo.DisposeAsync();

        Assert.Null(limiterOne.IdleDuration);

        limiterOne.Dispose();
    }

    [Fact]
    public void Releasing_a_disposed_limiter_twice_decrements_only_once()
    {
        var clock = new FakeTimeProvider();
        var options = Options(maxWarmPartitions: 1);
        using var health = new StoreHealth(options, clock);

        var primaryOne = new FakeRateLimiter(permitLimit: 10);
        var fallbackOne = new FakeRateLimiter(permitLimit: 10);
        using var limiterOne = new ResilientRateLimiter(primaryOne, fallbackOne, options, clock, health);

        var primaryTwo = new FakeRateLimiter(permitLimit: 10);
        var fallbackTwo = new FakeRateLimiter(permitLimit: 10);
        var limiterTwo = new ResilientRateLimiter(primaryTwo, fallbackTwo, options, clock, health);

        limiterTwo.Dispose();
        limiterTwo.Dispose();

        Assert.Equal(1, health.LivePartitions);
    }
}

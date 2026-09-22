using Microsoft.Extensions.Time.Testing;
using System.Threading.RateLimiting;
using Xunit;

namespace ResilientRateLimiting.Tests;

public class PartitionEvictionTests
{
    // The framework's idle sweep runs on its own real-time heartbeat, so this test waits on the wall clock.
    private static readonly TimeSpan HeartbeatWindow = TimeSpan.FromMilliseconds(500);

    [Fact]
    public async Task Keeps_the_partition_alive_while_a_request_is_in_flight()
    {
        var clock = new FakeTimeProvider();
        var options = new ResilientRateLimiterOptions
        {
            FallbackRecoveryTime = TimeSpan.FromSeconds(1),
            MaxWarmRetention = TimeSpan.FromSeconds(1),
        };

        using var primary = new FakeRateLimiter(permitLimit: 10);
        using var fallback = new FakeRateLimiter(permitLimit: 10);

        using var partitioned = PartitionedRateLimiter.Create<string, string>(
            _ => RateLimitPartition.Get("only", _ => new ResilientRateLimiter(primary, fallback, options, new StoreHealth(new StoreHealthOptions { ExpectedReplicaCount = 3 }, clock), clock)));

        (await partitioned.AcquireAsync("resource", 1, TestContext.Current.CancellationToken)).Dispose();

        clock.Advance(TimeSpan.FromSeconds(11));

        primary.HangUntilReleased();
        var pending = partitioned.AcquireAsync("resource", 1, TestContext.Current.CancellationToken).AsTask();

        await Task.Delay(HeartbeatWindow, TestContext.Current.CancellationToken);

        Assert.Equal(0, primary.DisposeCount);
        Assert.Equal(0, fallback.DisposeCount);
        Assert.False(pending.IsCompleted);

        primary.Release();
        (await pending).Dispose();
    }
}

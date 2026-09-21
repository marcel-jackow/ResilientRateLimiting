using System.Threading.RateLimiting;
using Xunit;

#pragma warning disable xUnit1051

namespace ResilientRateLimiting.Tests;

public class FakeRateLimiterTests
{
    [Fact]
    public async Task Counts_permits_down_and_rejects_past_the_limit()
    {
        using var limiter = new FakeRateLimiter(permitLimit: 2);

        Assert.True((await limiter.AcquireAsync(1)).IsAcquired);
        Assert.True((await limiter.AcquireAsync(1)).IsAcquired);
        Assert.False((await limiter.AcquireAsync(1)).IsAcquired);
        Assert.Equal(0, limiter.AvailablePermits);
        Assert.Equal(3, limiter.AcquireAttempts);
    }

    [Fact]
    public async Task Replenish_restores_the_full_budget()
    {
        using var limiter = new FakeRateLimiter(permitLimit: 1);
        await limiter.AcquireAsync(1);

        limiter.Replenish();

        Assert.True((await limiter.AcquireAsync(1)).IsAcquired);
    }

    [Fact]
    public async Task Fails_the_scripted_number_of_calls_then_recovers()
    {
        using var limiter = new FakeRateLimiter().FailTimes(2, new InvalidDataException("store down"));

        await Assert.ThrowsAsync<InvalidDataException>(async () => await limiter.AcquireAsync(1));
        await Assert.ThrowsAsync<InvalidDataException>(async () => await limiter.AcquireAsync(1));
        Assert.True((await limiter.AcquireAsync(1)).IsAcquired);
    }

    [Fact]
    public async Task Hangs_until_released()
    {
        using var limiter = new FakeRateLimiter().HangUntilReleased();

        var pending = limiter.AcquireAsync(1).AsTask();
        Assert.False(pending.IsCompleted);

        limiter.Release();

        Assert.True((await pending).IsAcquired);
    }

    [Fact]
    public async Task Hanging_call_observes_cancellation()
    {
        using var limiter = new FakeRateLimiter().HangUntilReleased();
        using var cts = new CancellationTokenSource();

        var pending = limiter.AcquireAsync(1, cts.Token).AsTask();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    [Fact]
    public async Task Serves_a_retry_after_on_a_rejected_lease()
    {
        using var limiter = new FakeRateLimiter(permitLimit: 0) { RetryAfter = TimeSpan.FromSeconds(4) };

        var lease = await limiter.AcquireAsync(1);

        Assert.False(lease.IsAcquired);
        Assert.True(lease.TryGetMetadata(MetadataName.RetryAfter.Name, out var value));
        Assert.Equal(TimeSpan.FromSeconds(4), value);
    }
}

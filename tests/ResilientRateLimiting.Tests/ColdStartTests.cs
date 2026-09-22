using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace ResilientRateLimiting.Tests;

public class ColdStartTests
{
    private readonly FakeTimeProvider _clock = new();

    private static ResilientRateLimiterOptions Options(double factor) => new()
    {
        ExpectedReplicaCount = 3,
        FallbackRecoveryTime = TimeSpan.FromMinutes(1),
        ColdStartFallbackFactor = factor,
    };

    [Fact]
    public async Task A_process_that_never_reached_the_store_gets_a_reduced_budget()
    {
        var options = Options(0.5);
        using var primary = new FakeRateLimiter().AlwaysFail(new InvalidDataException("store down"));
        using var fallback = new FakeRateLimiter(permitLimit: 4);
        using var limiter = new ResilientRateLimiter(primary, fallback, options, new StoreHealth(options, _clock), _clock);

        Assert.Equal(2, await AdmittedAsync(limiter, requests: 4));
    }

    [Fact]
    public async Task One_successful_store_call_restores_the_full_budget()
    {
        var options = Options(0.5);
        using var primary = new FakeRateLimiter(permitLimit: 1000);
        using var fallback = new FakeRateLimiter(permitLimit: 4);
        using var limiter = new ResilientRateLimiter(primary, fallback, options, new StoreHealth(options, _clock), _clock);

        // One healthy call proves the store is reachable. It also warms one local permit.
        (await limiter.AcquireAsync(1, TestContext.Current.CancellationToken)).Dispose();

        primary.AlwaysFail(new InvalidDataException("store down"));

        Assert.Equal(3, await AdmittedAsync(limiter, requests: 4));
    }

    [Fact]
    public async Task The_default_factor_changes_nothing()
    {
        var options = Options(1.0);
        using var primary = new FakeRateLimiter().AlwaysFail(new InvalidDataException("store down"));
        using var fallback = new FakeRateLimiter(permitLimit: 4);
        using var limiter = new ResilientRateLimiter(primary, fallback, options, new StoreHealth(options, _clock), _clock);

        Assert.Equal(4, await AdmittedAsync(limiter, requests: 4));
    }

    [Fact]
    public async Task The_factor_scales_the_whole_permit_count()
    {
        var options = Options(0.5);
        using var primary = new FakeRateLimiter().AlwaysFail(new InvalidDataException("store down"));
        using var fallback = new FakeRateLimiter(permitLimit: 4);
        using var limiter = new ResilientRateLimiter(primary, fallback, options, new StoreHealth(options, _clock), _clock);
        var cancellationToken = TestContext.Current.CancellationToken;

        using var first = await limiter.AcquireAsync(2, cancellationToken);
        using var second = await limiter.AcquireAsync(2, cancellationToken);

        Assert.True(first.IsAcquired);
        Assert.False(second.IsAcquired);
        Assert.Equal(0, fallback.AvailablePermits);
    }

    [Fact]
    public async Task A_rejected_store_answer_still_counts_as_reaching_the_store()
    {
        var options = Options(0.5);
        using var primary = new FakeRateLimiter(permitLimit: 0);
        using var fallback = new FakeRateLimiter(permitLimit: 4);
        using var limiter = new ResilientRateLimiter(primary, fallback, options, new StoreHealth(options, _clock), _clock);

        // The store answered, even though it said no.
        (await limiter.AcquireAsync(1, TestContext.Current.CancellationToken)).Dispose();

        primary.AlwaysFail(new InvalidDataException("store down"));

        Assert.Equal(4, await AdmittedAsync(limiter, requests: 4));
    }

    private static async Task<int> AdmittedAsync(ResilientRateLimiter limiter, int requests)
    {
        var admitted = 0;

        for (var i = 0; i < requests; i++)
        {
            using var lease = await limiter.AcquireAsync(1, TestContext.Current.CancellationToken);

            if (lease.IsAcquired)
            {
                admitted++;
            }
        }

        return admitted;
    }
}

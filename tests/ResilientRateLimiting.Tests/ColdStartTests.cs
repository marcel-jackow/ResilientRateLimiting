using Microsoft.Extensions.Time.Testing;
using System.Threading.RateLimiting;
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

    [Fact]
    public async Task A_scaled_charge_above_the_local_limit_still_serves_the_request()
    {
        var options = Options(0.5);
        using var primary = new FakeRateLimiter().AlwaysFail(new InvalidDataException("store down"));
        using var fallback = new FixedWindowRateLimiter(new FixedWindowRateLimiterOptions
        {
            PermitLimit = 4,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = false,
        });
        using var limiter = new ResilientRateLimiter(primary, fallback, options, new StoreHealth(options, _clock), _clock);

        // The scaled charge is ceil(3 / 0.5) = 6, above the fallback's limit of 4. A real
        // RateLimiter throws ArgumentOutOfRangeException for that; the caller must still be served.
        using var lease = await limiter.AcquireAsync(3, TestContext.Current.CancellationToken);

        Assert.True(lease.IsAcquired);
        Assert.True(lease.TryGetMetadata(ResilientRateLimitLease.SourceMetadata, out var source));
        Assert.Equal(LeaseSource.LocalFallback, source);
    }

    [Fact]
    public async Task A_permit_count_above_the_local_limit_is_rejected_rather_than_thrown()
    {
        var options = Options(0.5);
        using var primary = new FakeRateLimiter().AlwaysFail(new InvalidDataException("store down"));
        using var fallback = new FixedWindowRateLimiter(new FixedWindowRateLimiterOptions
        {
            PermitLimit = 4,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = false,
        });
        using var limiter = new ResilientRateLimiter(primary, fallback, options, new StoreHealth(options, _clock), _clock);

        // Neither the scaled charge (10) nor the plain one (5) can ever be granted by a limiter
        // whose whole budget is 4. A real RateLimiter throws for both.
        using var lease = await limiter.AcquireAsync(5, TestContext.Current.CancellationToken);

        Assert.False(lease.IsAcquired);
        Assert.True(lease.TryGetMetadata(ResilientRateLimitLease.SourceMetadata, out var source));
        Assert.Equal(LeaseSource.LocalFallback, source);
        Assert.Equal(4, fallback.GetStatistics()!.CurrentAvailablePermits);
    }

    [Fact]
    public async Task A_rejection_the_local_limiter_never_saw_holds_no_warm_state()
    {
        var options = Options(1.0);
        using var primary = new FakeRateLimiter().AlwaysFail(new InvalidDataException("store down"));
        using var fallback = new FixedWindowRateLimiter(new FixedWindowRateLimiterOptions
        {
            PermitLimit = 4,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = false,
        });
        using var limiter = new ResilientRateLimiter(primary, fallback, options, new StoreHealth(options, _clock), _clock);

        (await limiter.AcquireAsync(5, TestContext.Current.CancellationToken)).Dispose();

        // Nothing was charged and nothing is held, so the partition must be sweepable.
        Assert.NotNull(limiter.IdleDuration);
    }

    [Fact]
    public async Task A_negative_permit_count_is_rejected_by_the_framework_before_this_limiter_sees_it()
    {
        var options = Options(0.5);
        using var primary = new FakeRateLimiter().AlwaysFail(new InvalidDataException("store down"));
        using var fallback = new FakeRateLimiter(permitLimit: 4);
        using var limiter = new ResilientRateLimiter(primary, fallback, options, new StoreHealth(options, _clock), _clock);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            async () => await limiter.AcquireAsync(-1, TestContext.Current.CancellationToken));

        Assert.Equal(0, primary.AcquireAttempts);
    }

    [Fact]
    public async Task Cold_start_ends_for_every_limiter_sharing_the_store()
    {
        var options = Options(0.5);
        var health = new StoreHealth(options, _clock);
        using var healthyPrimary = new FakeRateLimiter(permitLimit: 1000);
        using var healthyFallback = new FakeRateLimiter(permitLimit: 4);
        using var reachesTheStore = new ResilientRateLimiter(healthyPrimary, healthyFallback, options, health, _clock);
        using var brokenPrimary = new FakeRateLimiter().AlwaysFail(new InvalidDataException("store down"));
        using var fallback = new FakeRateLimiter(permitLimit: 4);
        using var neverReachedItself = new ResilientRateLimiter(brokenPrimary, fallback, options, health, _clock);

        // One partition proves the store is reachable for the whole process.
        (await reachesTheStore.AcquireAsync(1, TestContext.Current.CancellationToken)).Dispose();

        Assert.Equal(4, await AdmittedAsync(neverReachedItself, requests: 4));
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

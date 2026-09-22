using Microsoft.Extensions.Time.Testing;
using System.Threading.RateLimiting;
using Xunit;

namespace ResilientRateLimiting.Tests;

public class ConcurrencyTests
{
    private const int Requests = 64;

    private readonly FakeTimeProvider _clock = new();

    private static ResilientRateLimiterOptions Options() => new()
    {
        FallbackRecoveryTime = TimeSpan.FromMinutes(1),
    };

    private static StoreHealthOptions StoreOptions() => new() { ExpectedReplicaCount = 3 };

    private static FixedWindowRateLimiter LocalCounter(int permitLimit) =>
        new(new FixedWindowRateLimiterOptions
        {
            PermitLimit = permitLimit,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = false,
        });

    [Fact]
    public async Task Parallel_requests_charge_the_local_counter_exactly_once_each()
    {
        var options = Options();
        using var primary = new FakeRateLimiter(permitLimit: 1000);
        using var fallback = LocalCounter(Requests * 2);
        using var limiter = new ResilientRateLimiter(primary, fallback, options, new StoreHealth(StoreOptions(), _clock), _clock);
        var cancellationToken = TestContext.Current.CancellationToken;

        var leases = await Task.WhenAll(Enumerable.Range(0, Requests)
            .Select(_ => Task.Run(async () => await limiter.AcquireAsync(1, cancellationToken), cancellationToken)));

        foreach (var lease in leases)
        {
            Assert.True(lease.IsAcquired);
            lease.Dispose();
        }

        // Twice the budget the requests need, so both failures are visible: a lost update leaves
        // more than half, a double charge leaves less.
        Assert.Equal(Requests, fallback.GetStatistics()!.CurrentAvailablePermits);
    }

    [Fact]
    public async Task Parallel_requests_during_an_outage_admit_exactly_the_local_budget()
    {
        var options = Options();
        using var primary = new FakeRateLimiter().AlwaysFail(new InvalidDataException("store down"));
        using var fallback = LocalCounter(Requests / 4);
        using var limiter = new ResilientRateLimiter(primary, fallback, options, new StoreHealth(StoreOptions(), _clock), _clock);
        var cancellationToken = TestContext.Current.CancellationToken;

        var leases = await Task.WhenAll(Enumerable.Range(0, Requests)
            .Select(_ => Task.Run(async () => await limiter.AcquireAsync(1, cancellationToken), cancellationToken)));

        var admitted = leases.Count(lease => lease.IsAcquired);

        foreach (var lease in leases)
        {
            lease.Dispose();
        }

        Assert.Equal(Requests / 4, admitted);
    }

    [Fact]
    public async Task Idle_duration_never_reports_idle_while_requests_are_in_flight()
    {
        var options = Options();
        using var primary = new FakeRateLimiter(permitLimit: 1000).HangUntilReleased();
        using var fallback = LocalCounter(Requests);
        using var limiter = new ResilientRateLimiter(primary, fallback, options, new StoreHealth(StoreOptions(), _clock), _clock);
        var cancellationToken = TestContext.Current.CancellationToken;

        var pending = Enumerable.Range(0, Requests)
            .Select(_ => Task.Run(async () => await limiter.AcquireAsync(1, cancellationToken), cancellationToken))
            .ToArray();

        while (primary.AcquireAttempts < Requests)
        {
            await Task.Yield();
        }

        // The framework's sweep reads this property from another thread while requests are running.
        // Answering honestly here deletes warm state mid-request, which returned HTTP 500 once.
        for (var i = 0; i < 1_000; i++)
        {
            Assert.Null(limiter.IdleDuration);
        }

        primary.Release();

        foreach (var lease in await Task.WhenAll(pending))
        {
            lease.Dispose();
        }
    }
}

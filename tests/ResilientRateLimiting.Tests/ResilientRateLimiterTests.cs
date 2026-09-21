using Microsoft.Extensions.Time.Testing;
using System.Threading.RateLimiting;
using Xunit;

namespace ResilientRateLimiting.Tests;

public class ResilientRateLimiterTests
{
    private static ResilientRateLimiterOptions Options(StoreFailureBehavior behavior = StoreFailureBehavior.LocalFallback) => new()
    {
        FailureBehavior = behavior,
        ExpectedReplicaCount = 3,
        FallbackRecoveryTime = TimeSpan.FromMinutes(1),
        StoreTimeout = TimeSpan.FromMilliseconds(20),
    };

    private static LeaseSource SourceOf(RateLimitLease lease)
    {
        Assert.True(lease.TryGetMetadata(ResilientRateLimitLease.SourceMetadata, out var source));
        return source;
    }

    [Fact]
    public async Task Passes_the_store_answer_through_when_the_store_is_healthy()
    {
        using var primary = new FakeRateLimiter(permitLimit: 1);
        using var fallback = new FakeRateLimiter(permitLimit: 1);
        using var limiter = new ResilientRateLimiter(primary, fallback, Options(), new FakeTimeProvider());

        using var lease = await limiter.AcquireAsync(1, TestContext.Current.CancellationToken);

        Assert.True(lease.IsAcquired);
        Assert.Equal(LeaseSource.Distributed, SourceOf(lease));
        Assert.Equal(1, primary.AcquireAttempts);
        Assert.Equal(0, fallback.AcquireAttempts);
    }

    [Fact]
    public async Task Falls_back_locally_when_the_store_throws()
    {
        using var primary = new FakeRateLimiter().AlwaysFail(new InvalidDataException("store down"));
        using var fallback = new FakeRateLimiter(permitLimit: 1);
        using var limiter = new ResilientRateLimiter(primary, fallback, Options(), new FakeTimeProvider());

        using var lease = await limiter.AcquireAsync(1, TestContext.Current.CancellationToken);

        Assert.True(lease.IsAcquired);
        Assert.Equal(LeaseSource.LocalFallback, SourceOf(lease));
        Assert.Equal(1, fallback.AcquireAttempts);
    }

    [Fact]
    public async Task The_local_fallback_enforces_its_own_budget()
    {
        using var primary = new FakeRateLimiter().AlwaysFail(new InvalidDataException("store down"));
        using var fallback = new FakeRateLimiter(permitLimit: 1);
        using var limiter = new ResilientRateLimiter(primary, fallback, Options(), new FakeTimeProvider());

        using var first = await limiter.AcquireAsync(1, TestContext.Current.CancellationToken);
        using var second = await limiter.AcquireAsync(1, TestContext.Current.CancellationToken);

        Assert.True(first.IsAcquired);
        Assert.False(second.IsAcquired);
        Assert.Equal(LeaseSource.LocalFallback, SourceOf(second));
    }

    [Fact]
    public async Task Falls_back_when_the_store_is_slower_than_the_timeout()
    {
        var clock = new FakeTimeProvider();
        using var primary = new FakeRateLimiter().HangUntilReleased();
        using var fallback = new FakeRateLimiter(permitLimit: 1);
        using var limiter = new ResilientRateLimiter(primary, fallback, Options(), clock);

        var pending = limiter.AcquireAsync(1, TestContext.Current.CancellationToken).AsTask();
        Assert.False(pending.IsCompleted);

        clock.Advance(TimeSpan.FromMilliseconds(25));

        using var lease = await pending;
        Assert.True(lease.IsAcquired);
        Assert.Equal(LeaseSource.LocalFallback, SourceOf(lease));
    }

    [Fact]
    public async Task Falls_back_when_the_store_ignores_cancellation_entirely()
    {
        var clock = new FakeTimeProvider();
        using var primary = new FakeRateLimiter().HangIgnoringCancellation();
        using var fallback = new FakeRateLimiter(permitLimit: 1);
        using var limiter = new ResilientRateLimiter(primary, fallback, Options(), clock);

        var pending = limiter.AcquireAsync(1, TestContext.Current.CancellationToken).AsTask();
        Assert.False(pending.IsCompleted);

        clock.Advance(TimeSpan.FromMilliseconds(25));

        using var lease = await pending;

        Assert.True(lease.IsAcquired);
        Assert.Equal(LeaseSource.LocalFallback, SourceOf(lease));

        primary.Release();
    }

    [Fact]
    public async Task A_store_answer_arriving_after_the_cutoff_is_released()
    {
        var clock = new FakeTimeProvider();
        using var primary = new FakeRateLimiter(permitLimit: 1).HangIgnoringCancellation();
        using var fallback = new FakeRateLimiter(permitLimit: 1);
        using var limiter = new ResilientRateLimiter(primary, fallback, Options(), clock);

        var pending = limiter.AcquireAsync(1, TestContext.Current.CancellationToken).AsTask();
        clock.Advance(TimeSpan.FromMilliseconds(25));

        using (var lease = await pending)
        {
            Assert.Equal(LeaseSource.LocalFallback, SourceOf(lease));
        }

        Assert.Equal(0, primary.LeasesDisposed);

        primary.Release();

        var deadline = DateTime.UtcNow.AddSeconds(5);

        while (primary.LeasesDisposed == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.Equal(1, primary.LeasesDisposed);
    }

    [Fact]
    public async Task Caller_cancellation_propagates_even_when_the_store_ignores_it()
    {
        using var primary = new FakeRateLimiter().HangIgnoringCancellation();
        using var fallback = new FakeRateLimiter(permitLimit: 1);
        using var limiter = new ResilientRateLimiter(primary, fallback, Options(), new FakeTimeProvider());
        using var caller = new CancellationTokenSource();

        var pending = limiter.AcquireAsync(1, caller.Token).AsTask();
        await caller.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(0, fallback.AcquireAttempts);

        primary.Release();
    }

    [Fact]
    public async Task An_abandoned_store_call_keeps_its_cancellation_source_until_it_finishes()
    {
        using var primary = new FakeRateLimiter(permitLimit: 1).HangIgnoringCancellation();
        using var fallback = new FakeRateLimiter(permitLimit: 1);
        using var limiter = new ResilientRateLimiter(primary, fallback, Options(), new FakeTimeProvider());
        using var caller = new CancellationTokenSource();

        var pending = limiter.AcquireAsync(1, caller.Token).AsTask();
        await caller.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);

        primary.Release();

        var deadline = DateTime.UtcNow.AddSeconds(5);

        while (primary.LeasesDisposed == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.Equal(1, primary.LeasesDisposed);
    }

    [Fact]
    public async Task Admits_the_request_when_configured_to_fail_open()
    {
        using var primary = new FakeRateLimiter().AlwaysFail(new InvalidDataException("store down"));
        using var limiter = new ResilientRateLimiter(primary, fallback: null, Options(StoreFailureBehavior.FailOpen), new FakeTimeProvider());

        using var lease = await limiter.AcquireAsync(1, TestContext.Current.CancellationToken);

        Assert.True(lease.IsAcquired);
        Assert.Equal(LeaseSource.FailOpen, SourceOf(lease));
    }

    [Fact]
    public async Task Does_not_warm_a_fallback_a_fail_open_limiter_will_never_consult()
    {
        using var primary = new FakeRateLimiter(permitLimit: 10);
        using var fallback = new FakeRateLimiter(permitLimit: 10);
        using var limiter = new ResilientRateLimiter(
            primary, fallback, Options(StoreFailureBehavior.FailOpen), new FakeTimeProvider());

        using var lease = await limiter.AcquireAsync(1, TestContext.Current.CancellationToken);

        Assert.Equal(LeaseSource.Distributed, SourceOf(lease));
        Assert.Equal(10, fallback.AvailablePermits);
        Assert.Equal(TimeSpan.Zero, limiter.IdleDuration);
    }

    [Fact]
    public async Task Rejects_the_request_when_configured_to_fail_closed()
    {
        using var primary = new FakeRateLimiter().AlwaysFail(new InvalidDataException("store down"));
        using var limiter = new ResilientRateLimiter(primary, fallback: null, Options(StoreFailureBehavior.FailClosed), new FakeTimeProvider());

        using var lease = await limiter.AcquireAsync(1, TestContext.Current.CancellationToken);

        Assert.False(lease.IsAcquired);
        Assert.Equal(LeaseSource.FailClosed, SourceOf(lease));
    }

    [Fact]
    public async Task Caller_cancellation_reaches_the_caller_and_does_not_consume_the_fallback()
    {
        using var primary = new FakeRateLimiter().HangUntilReleased();
        using var fallback = new FakeRateLimiter(permitLimit: 1);
        using var limiter = new ResilientRateLimiter(primary, fallback, Options(), new FakeTimeProvider());
        using var cts = new CancellationTokenSource();

        var pending = limiter.AcquireAsync(1, cts.Token).AsTask();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(0, fallback.AcquireAttempts);
    }

    [Fact]
    public async Task A_programming_error_from_the_store_reaches_the_caller()
    {
        using var primary = new FakeRateLimiter().AlwaysFail(new ObjectDisposedException("multiplexer"));
        using var fallback = new FakeRateLimiter(permitLimit: 1);
        using var limiter = new ResilientRateLimiter(primary, fallback, Options(), new FakeTimeProvider());

        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await limiter.AcquireAsync(1, TestContext.Current.CancellationToken));
        Assert.Equal(0, fallback.AcquireAttempts);
    }

    [Fact]
    public async Task A_failing_fallback_reaches_the_caller()
    {
        using var primary = new FakeRateLimiter().AlwaysFail(new InvalidDataException("store down"));
        using var fallback = new FakeRateLimiter().AlwaysFail(new InvalidDataException("fallback broken"));
        using var limiter = new ResilientRateLimiter(primary, fallback, Options(), new FakeTimeProvider());

        await Assert.ThrowsAsync<InvalidDataException>(async () => await limiter.AcquireAsync(1, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task The_fallback_is_charged_the_permits_the_caller_asked_for()
    {
        using var primary = new FakeRateLimiter().AlwaysFail(new InvalidDataException("store down"));
        using var fallback = new FakeRateLimiter(permitLimit: 10);
        using var limiter = new ResilientRateLimiter(primary, fallback, Options(), new FakeTimeProvider());

        using var lease = await limiter.AcquireAsync(4, TestContext.Current.CancellationToken);

        Assert.True(lease.IsAcquired);
        Assert.Equal(6, fallback.AvailablePermits);
    }

    [Fact]
    public void The_sync_path_always_defers_to_the_async_path()
    {
        using var primary = new FakeRateLimiter(permitLimit: 10);
        using var fallback = new FakeRateLimiter(permitLimit: 10);
        using var limiter = new ResilientRateLimiter(primary, fallback, Options(), new FakeTimeProvider());

        using var lease = limiter.AttemptAcquire(1);

        Assert.False(lease.IsAcquired);
        Assert.False(lease.TryGetMetadata(ResilientRateLimitLease.SourceMetadata, out _));
        Assert.Equal(10, primary.AvailablePermits);
        Assert.Equal(10, fallback.AvailablePermits);
    }

    [Fact]
    public async Task Ignores_a_failure_behavior_mutated_after_construction()
    {
        var options = Options(StoreFailureBehavior.FailOpen);
        using var primary = new FakeRateLimiter().AlwaysFail(new InvalidDataException("store down"));
        using var limiter = new ResilientRateLimiter(primary, fallback: null, options, new FakeTimeProvider());

        options.FailureBehavior = StoreFailureBehavior.LocalFallback;

        using var lease = await limiter.AcquireAsync(1, TestContext.Current.CancellationToken);

        Assert.True(lease.IsAcquired);
        Assert.Equal(LeaseSource.FailOpen, SourceOf(lease));
    }

    [Fact]
    public async Task Ignores_a_should_handle_predicate_mutated_after_construction()
    {
        var options = Options(StoreFailureBehavior.FailOpen);
        options.ShouldHandle = _ => false;
        using var primary = new FakeRateLimiter().AlwaysFail(new InvalidDataException("store down"));
        using var limiter = new ResilientRateLimiter(primary, fallback: null, options, new FakeTimeProvider());

        options.ShouldHandle = _ => true;

        await Assert.ThrowsAsync<InvalidDataException>(
            async () => await limiter.AcquireAsync(1, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Reports_no_statistics()
    {
        using var primary = new FakeRateLimiter();
        using var fallback = new FakeRateLimiter();
        using var limiter = new ResilientRateLimiter(primary, fallback, Options(), new FakeTimeProvider());

        Assert.Null(limiter.GetStatistics());
    }

    [Fact]
    public void Disposing_disposes_both_limiters()
    {
        var primary = new FakeRateLimiter(permitLimit: 1);
        var fallback = new FakeRateLimiter(permitLimit: 1);
        var limiter = new ResilientRateLimiter(primary, fallback, Options(), new FakeTimeProvider());

        limiter.Dispose();

        Assert.Equal(1, primary.DisposeCount);
        Assert.Equal(1, fallback.DisposeCount);
    }

    [Fact]
    public async Task Disposing_asynchronously_disposes_both_limiters()
    {
        var primary = new FakeRateLimiter(permitLimit: 1);
        var fallback = new FakeRateLimiter(permitLimit: 1);
        var limiter = new ResilientRateLimiter(primary, fallback, Options(), new FakeTimeProvider());

        await limiter.DisposeAsync();

        Assert.Equal(1, primary.DisposeCount);
        Assert.Equal(1, fallback.DisposeCount);
    }

    [Fact]
    public void Refuses_to_be_built_without_a_fallback_when_one_is_required()
    {
        using var primary = new FakeRateLimiter();

        Assert.Throws<ArgumentNullException>(() =>
            new ResilientRateLimiter(primary, fallback: null, Options(), new FakeTimeProvider()));
    }

    [Fact]
    public void Refuses_an_invalid_configuration()
    {
        using var primary = new FakeRateLimiter();
        using var fallback = new FakeRateLimiter();

        Assert.Throws<InvalidOperationException>(() =>
            new ResilientRateLimiter(primary, fallback, new ResilientRateLimiterOptions(), new FakeTimeProvider()));
    }
}

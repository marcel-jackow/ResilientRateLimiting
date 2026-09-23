using System.Threading.RateLimiting;
using Xunit;

namespace ResilientRateLimiting.Tests;

public class RetryAfterCalculatorTests
{
    private static readonly TimeSpan BreakDuration = TimeSpan.FromSeconds(5);

    private static ResilientRateLimiterOptions Options() => new()
    {
        FallbackRecoveryTime = TimeSpan.FromSeconds(60),
    };

    private static RateLimitLease Rejected(TimeSpan? retryAfter = null)
    {
        var limiter = new FakeRateLimiter(permitLimit: 0) { RetryAfter = retryAfter };
        return limiter.AttemptAcquire(1);
    }

    private static RateLimitLease Acquired()
    {
        var limiter = new FakeRateLimiter(permitLimit: 1);
        return limiter.AttemptAcquire(1);
    }

    /// <summary>A hand-written double whose metadata read throws, as ruling 4 requires.</summary>
    private sealed class ThrowingMetadataLease : RateLimitLease
    {
        public override bool IsAcquired => false;

        public override IEnumerable<string> MetadataNames => [MetadataName.RetryAfter.Name];

        public override bool TryGetMetadata(string metadataName, out object? metadata) =>
            throw new InvalidOperationException("the inner limiter is a caller's, and it is broken");
    }

    [Fact]
    public void An_acquired_lease_gets_no_value()
    {
        var calculator = new RetryAfterCalculator(Options(), BreakDuration, sampler: () => 0);

        Assert.Null(calculator.Compute(Acquired(), LeaseSource.Distributed));
    }

    [Fact]
    public void Prefers_the_inner_value_and_jitters_it()
    {
        var calculator = new RetryAfterCalculator(Options(), BreakDuration, sampler: () => 0);

        var value = calculator.Compute(Rejected(TimeSpan.FromSeconds(10)), LeaseSource.Distributed);

        // sampler 0 means the lowest jitter: +10%.
        Assert.Equal(TimeSpan.FromSeconds(11), value);
    }

    [Fact]
    public void Uses_the_widest_jitter_at_the_top_of_the_sample_range()
    {
        var calculator = new RetryAfterCalculator(Options(), BreakDuration, sampler: () => 1);

        var value = calculator.Compute(Rejected(TimeSpan.FromSeconds(10)), LeaseSource.Distributed);

        Assert.Equal(TimeSpan.FromSeconds(12), value);
    }

    [Fact]
    public void Estimates_from_the_fallback_recovery_time_when_the_inner_lease_has_none()
    {
        var calculator = new RetryAfterCalculator(Options(), BreakDuration, sampler: () => 0);

        var value = calculator.Compute(Rejected(), LeaseSource.Distributed);

        Assert.Equal(TimeSpan.FromSeconds(66), value);
    }

    [Fact]
    public void Estimates_from_the_break_duration_when_the_fallback_recovery_time_is_unset()
    {
        // D101: a fail-open/fail-closed limiter need not set FallbackRecoveryTime.
        var options = new ResilientRateLimiterOptions();
        var calculator = new RetryAfterCalculator(options, BreakDuration, sampler: () => 0);

        var value = calculator.Compute(Rejected(), LeaseSource.FailClosed);

        // BreakDuration 5s doubled (degraded) to 10s, then the degraded jitter floor of +20%.
        Assert.Equal(TimeSpan.FromSeconds(12), value);
    }

    [Fact]
    public void Is_longer_and_more_spread_out_while_degraded()
    {
        var calculator = new RetryAfterCalculator(Options(), BreakDuration, sampler: () => 0);

        var value = calculator.Compute(Rejected(TimeSpan.FromSeconds(10)), LeaseSource.LocalFallback);

        // Doubled to 20 s, then the degraded jitter floor of +20%.
        Assert.Equal(TimeSpan.FromSeconds(24), value);
    }

    [Fact]
    public void Never_goes_below_one_second()
    {
        var options = new ResilientRateLimiterOptions { FallbackRecoveryTime = TimeSpan.FromMilliseconds(100) };
        var calculator = new RetryAfterCalculator(options, BreakDuration, sampler: () => 0);

        var value = calculator.Compute(Rejected(), LeaseSource.Distributed);

        Assert.Equal(TimeSpan.FromSeconds(1), value);
    }

    [Fact]
    public void Stays_inside_its_band_across_the_whole_sample_range()
    {
        var calculator = new RetryAfterCalculator(Options(), BreakDuration, sampler: () => Random.Shared.NextDouble());

        for (var i = 0; i < 1_000; i++)
        {
            var value = calculator.Compute(Rejected(TimeSpan.FromSeconds(10)), LeaseSource.Distributed);

            Assert.NotNull(value);
            Assert.InRange(value!.Value, TimeSpan.FromSeconds(11), TimeSpan.FromSeconds(12));
        }
    }

    [Fact]
    public void A_throwing_inner_metadata_read_counts_as_no_inner_value()
    {
        var calculator = new RetryAfterCalculator(Options(), BreakDuration, sampler: () => 0);

        var value = calculator.Compute(new ThrowingMetadataLease(), LeaseSource.Distributed);

        // Falls back to the FallbackRecoveryTime estimate (60s) with the lowest jitter (+10%).
        Assert.Equal(TimeSpan.FromSeconds(66), value);
    }

    [Fact]
    public void Saturates_at_max_value_instead_of_overflowing()
    {
        // FallbackRecoveryTime may legitimately be TimeSpan.MaxValue; doubling and jittering it
        // must not throw OverflowException (ruling 4).
        var options = new ResilientRateLimiterOptions { FallbackRecoveryTime = TimeSpan.MaxValue };
        var calculator = new RetryAfterCalculator(options, BreakDuration, sampler: () => 1);

        var value = calculator.Compute(Rejected(), LeaseSource.LocalFallback);

        Assert.Equal(TimeSpan.MaxValue, value);
    }

    [Fact]
    public void The_directly_supplied_overload_serves_the_recovery_gate_which_holds_no_lease()
    {
        // The recovery gate disposes its own lease before this is called, so it hands over the
        // already-extracted value directly rather than a lease to read metadata from.
        var calculator = new RetryAfterCalculator(Options(), BreakDuration, sampler: () => 0);

        var value = calculator.Compute((TimeSpan?)TimeSpan.FromSeconds(10), LeaseSource.Recovery);

        // Recovery counts as degraded: doubled to 20s, then the degraded jitter floor of +20%.
        Assert.Equal(TimeSpan.FromSeconds(24), value);
    }
}

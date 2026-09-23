using System.Threading.RateLimiting;
using Xunit;

namespace ResilientRateLimiting.Tests;

public class RetryAfterCalculatorTests
{
    private static readonly TimeSpan BreakDuration = TimeSpan.FromSeconds(5);

    /// <summary>FallbackRecoveryTime 60s; MaxAddedRetryDelay defaults to 60s (the record default).</summary>
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

    /// <summary>A hand-written double whose metadata read throws.</summary>
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
    public void Prefers_the_inner_value_and_adds_the_low_end_of_the_band()
    {
        var calculator = new RetryAfterCalculator(Options(), BreakDuration, sampler: () => 0);

        var value = calculator.Compute(Rejected(TimeSpan.FromSeconds(10)), LeaseSource.Distributed);

        // 10 s, store up: a 1..3 s spread, no doubling. The sampler picks the low end: 10 + 1 = 11 s.
        Assert.Equal(TimeSpan.FromSeconds(11), value);
    }

    [Fact]
    public void Adds_the_high_end_of_the_band_at_the_top_of_the_sample_range()
    {
        var calculator = new RetryAfterCalculator(Options(), BreakDuration, sampler: () => 1);

        var value = calculator.Compute(Rejected(TimeSpan.FromSeconds(10)), LeaseSource.Distributed);

        // 10 s, store up: a 1..3 s spread, no doubling. The sampler picks the high end: 10 + 3 = 13 s.
        Assert.Equal(TimeSpan.FromSeconds(13), value);
    }

    [Fact]
    public void Is_longer_and_more_spread_out_while_degraded()
    {
        var calculator = new RetryAfterCalculator(Options(), BreakDuration, sampler: () => 0);

        var value = calculator.Compute(Rejected(TimeSpan.FromSeconds(10)), LeaseSource.LocalFallback);

        // 10 s, store down: a 2..6 s spread plus 10 s doubling. The sampler picks the low end: 10 + 10 + 2 = 22 s.
        Assert.Equal(TimeSpan.FromSeconds(22), value);
    }

    [Fact]
    public void Adds_the_widest_degraded_spread_at_the_top_of_the_sample_range()
    {
        var calculator = new RetryAfterCalculator(Options(), BreakDuration, sampler: () => 1);

        var value = calculator.Compute(Rejected(TimeSpan.FromSeconds(10)), LeaseSource.LocalFallback);

        // 10 s, store down: a 2..6 s spread plus 10 s doubling. The sampler picks the high end: 10 + 10 + 6 = 26 s.
        Assert.Equal(TimeSpan.FromSeconds(26), value);
    }

    [Fact]
    public void Estimates_from_the_fallback_recovery_time_when_the_inner_lease_has_none()
    {
        var calculator = new RetryAfterCalculator(Options(), BreakDuration, sampler: () => 0);

        var value = calculator.Compute(Rejected(), LeaseSource.Distributed);

        // 60 s, store up: the spread is 10..20 %, so 6..12 s. The sampler picks the low end: 60 + 6 = 66 s.
        Assert.Equal(TimeSpan.FromSeconds(66), value);
    }

    [Fact]
    public void Adds_the_high_end_of_the_band_to_the_estimate()
    {
        var calculator = new RetryAfterCalculator(Options(), BreakDuration, sampler: () => 1);

        var value = calculator.Compute(Rejected(), LeaseSource.Distributed);

        // 60 s, store up: a 6..12 s spread. The sampler picks the high end: 60 + 12 = 72 s.
        Assert.Equal(TimeSpan.FromSeconds(72), value);
    }

    [Fact]
    public void Estimates_from_the_break_duration_when_the_fallback_recovery_time_is_unset()
    {
        // A fail-open or fail-closed limiter need not set FallbackRecoveryTime.
        var options = new ResilientRateLimiterOptions();
        var calculator = new RetryAfterCalculator(options, BreakDuration, sampler: () => 0);

        var value = calculator.Compute(Rejected(), LeaseSource.FailClosed);

        // No FallbackRecoveryTime, so the base is BreakDuration (5 s); store down: 2..6 s spread plus 5 s doubling. The sampler picks the low end: 5 + 5 + 2 = 12 s.
        Assert.Equal(TimeSpan.FromSeconds(12), value);
    }

    [Fact]
    public void Adds_the_widened_band_to_a_large_normal_base_when_the_cap_has_room_to_spare()
    {
        var calculator = new RetryAfterCalculator(Options(), BreakDuration, sampler: () => 0);

        var value = calculator.Compute(Rejected(TimeSpan.FromMinutes(60)), LeaseSource.Distributed);

        // 60 min: 10..20 % would be 6..12 min, but the 60 s cap limits the spread to 30..60 s. The sampler picks the low end: 3600 + 30 = 3630 s.
        Assert.Equal(TimeSpan.FromSeconds(3630), value);
    }

    [Fact]
    public void Adds_at_most_the_cap_to_a_large_normal_base()
    {
        var calculator = new RetryAfterCalculator(Options(), BreakDuration, sampler: () => 1);

        var value = calculator.Compute(Rejected(TimeSpan.FromMinutes(60)), LeaseSource.Distributed);

        // 60 min: the 60 s cap limits the spread to 30..60 s. The sampler picks the high end: 3600 + 60 = 3660 s.
        Assert.Equal(TimeSpan.FromSeconds(3660), value);
    }

    [Fact]
    public void A_large_degraded_base_gets_no_doubling_once_the_band_exhausts_the_cap()
    {
        var calculator = new RetryAfterCalculator(Options(), BreakDuration, sampler: () => 0);

        var value = calculator.Compute(Rejected(TimeSpan.FromMinutes(60)), LeaseSource.LocalFallback);

        // 60 min, store down: the spread uses the whole 60 s cap, so nothing is left for the doubling. The sampler picks the low end: 3600 + 30 = 3630 s.
        Assert.Equal(TimeSpan.FromSeconds(3630), value);
    }

    [Fact]
    public void A_large_degraded_base_still_adds_at_most_the_cap_at_the_top_of_the_range()
    {
        var calculator = new RetryAfterCalculator(Options(), BreakDuration, sampler: () => 1);

        var value = calculator.Compute(Rejected(TimeSpan.FromMinutes(60)), LeaseSource.LocalFallback);

        // 60 min, store down: the spread uses the whole 60 s cap, no doubling. The sampler picks the high end: 3600 + 60 = 3660 s.
        Assert.Equal(TimeSpan.FromSeconds(3660), value);
    }

    [Fact]
    public void Adds_the_low_end_of_the_band_to_a_small_normal_base()
    {
        var calculator = new RetryAfterCalculator(Options(), BreakDuration, sampler: () => 0);

        var value = calculator.Compute(Rejected(TimeSpan.FromSeconds(1)), LeaseSource.Distributed);

        // 1 s: the whole-second minimums give a 1..3 s spread. The sampler picks the low end: 1 + 1 = 2 s.
        Assert.Equal(TimeSpan.FromSeconds(2), value);
    }

    [Fact]
    public void Adds_the_high_end_of_the_band_to_a_small_normal_base()
    {
        var calculator = new RetryAfterCalculator(Options(), BreakDuration, sampler: () => 1);

        var value = calculator.Compute(Rejected(TimeSpan.FromSeconds(1)), LeaseSource.Distributed);

        // 1 s: a 1..3 s spread. The sampler picks the high end: 1 + 3 = 4 s.
        Assert.Equal(TimeSpan.FromSeconds(4), value);
    }

    [Fact]
    public void A_five_second_cap_leaves_no_room_for_doubling()
    {
        var options = Options() with { MaxAddedRetryDelay = TimeSpan.FromSeconds(5) };
        var calculator = new RetryAfterCalculator(options, BreakDuration, sampler: () => 0);

        var value = calculator.Compute(Rejected(TimeSpan.FromSeconds(10)), LeaseSource.LocalFallback);

        // 5 s cap, store down: the spread takes all of it (2..5 s), nothing is left for the doubling. The sampler picks the low end: 10 + 2 = 12 s.
        Assert.Equal(TimeSpan.FromSeconds(12), value);
    }

    [Fact]
    public void A_five_second_cap_still_caps_the_top_of_the_range()
    {
        var options = Options() with { MaxAddedRetryDelay = TimeSpan.FromSeconds(5) };
        var calculator = new RetryAfterCalculator(options, BreakDuration, sampler: () => 1);

        var value = calculator.Compute(Rejected(TimeSpan.FromSeconds(10)), LeaseSource.LocalFallback);

        // 5 s cap, store down: a 2..5 s spread, no doubling. The sampler picks the high end: 10 + 5 = 15 s.
        Assert.Equal(TimeSpan.FromSeconds(15), value);
    }

    [Fact]
    public void A_two_second_cap_shrinks_the_normal_band()
    {
        var options = Options() with { MaxAddedRetryDelay = TimeSpan.FromSeconds(2) };
        var calculator = new RetryAfterCalculator(options, BreakDuration, sampler: () => 0);

        var value = calculator.Compute(Rejected(TimeSpan.FromSeconds(10)), LeaseSource.Distributed);

        // 2 s cap, store up: the spread is squeezed to 1..2 s. The sampler picks the low end: 10 + 1 = 11 s.
        Assert.Equal(TimeSpan.FromSeconds(11), value);
    }

    [Fact]
    public void A_two_second_cap_still_caps_the_top_of_the_normal_band()
    {
        var options = Options() with { MaxAddedRetryDelay = TimeSpan.FromSeconds(2) };
        var calculator = new RetryAfterCalculator(options, BreakDuration, sampler: () => 1);

        var value = calculator.Compute(Rejected(TimeSpan.FromSeconds(10)), LeaseSource.Distributed);

        // 2 s cap, store up: a 1..2 s spread. The sampler picks the high end: 10 + 2 = 12 s.
        Assert.Equal(TimeSpan.FromSeconds(12), value);
    }

    [Fact]
    public void A_zero_cap_passes_the_inner_value_through_unchanged()
    {
        var options = Options() with { MaxAddedRetryDelay = TimeSpan.Zero };
        var calculator = new RetryAfterCalculator(options, BreakDuration, sampler: () => 0);

        var value = calculator.Compute(Rejected(TimeSpan.FromSeconds(10)), LeaseSource.Distributed);

        Assert.Equal(TimeSpan.FromSeconds(10), value);
    }

    [Fact]
    public void A_zero_cap_adds_nothing_even_while_degraded()
    {
        var options = Options() with { MaxAddedRetryDelay = TimeSpan.Zero };
        var calculator = new RetryAfterCalculator(options, BreakDuration, sampler: () => 0);

        var value = calculator.Compute(Rejected(TimeSpan.FromSeconds(10)), LeaseSource.LocalFallback);

        Assert.Equal(TimeSpan.FromSeconds(10), value);
    }

    [Fact]
    public void A_zero_cap_still_floors_to_one_second()
    {
        var options = new ResilientRateLimiterOptions
        {
            FallbackRecoveryTime = TimeSpan.FromMilliseconds(100),
            MaxAddedRetryDelay = TimeSpan.Zero,
        };
        var calculator = new RetryAfterCalculator(options, BreakDuration, sampler: () => 0);

        var value = calculator.Compute(Rejected(), LeaseSource.Distributed);

        Assert.Equal(TimeSpan.FromSeconds(1), value);
    }

    [Fact]
    public void A_zero_cap_never_calls_the_sampler()
    {
        var options = Options() with { MaxAddedRetryDelay = TimeSpan.Zero };
        var calls = 0;
        double CountingSampler()
        {
            calls++;
            return 0;
        }

        var calculator = new RetryAfterCalculator(options, BreakDuration, sampler: CountingSampler);

        calculator.Compute(Rejected(TimeSpan.FromSeconds(10)), LeaseSource.LocalFallback);

        Assert.Equal(0, calls);
    }

    [Fact]
    public void A_positive_cap_still_floors_to_one_second_when_the_cap_itself_is_tiny()
    {
        var options = new ResilientRateLimiterOptions
        {
            FallbackRecoveryTime = TimeSpan.FromMilliseconds(100),
            MaxAddedRetryDelay = TimeSpan.FromMilliseconds(1),
        };
        var calculator = new RetryAfterCalculator(options, BreakDuration, sampler: () => 0);

        var value = calculator.Compute(Rejected(), LeaseSource.Distributed);

        // 1 ms cap: the spread stays under a millisecond, so the 1 s floor decides.
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
            Assert.InRange(value!.Value, TimeSpan.FromSeconds(11), TimeSpan.FromSeconds(13));
        }
    }

    [Fact]
    public void Never_adds_more_than_the_cap()
    {
        (TimeSpan baseValue, LeaseSource source, TimeSpan cap)[] cases =
        [
            (TimeSpan.FromSeconds(10), LeaseSource.Distributed, TimeSpan.FromSeconds(60)),
            (TimeSpan.FromSeconds(10), LeaseSource.LocalFallback, TimeSpan.FromSeconds(60)),
            (TimeSpan.FromMinutes(60), LeaseSource.Distributed, TimeSpan.FromSeconds(60)),
            (TimeSpan.FromMinutes(60), LeaseSource.LocalFallback, TimeSpan.FromSeconds(60)),
            (TimeSpan.FromSeconds(10), LeaseSource.LocalFallback, TimeSpan.FromSeconds(5)),
            (TimeSpan.FromSeconds(10), LeaseSource.Distributed, TimeSpan.FromSeconds(2)),
            (TimeSpan.FromSeconds(1), LeaseSource.Distributed, TimeSpan.FromSeconds(60)),
        ];

        foreach (var (baseValue, source, cap) in cases)
        {
            var options = new ResilientRateLimiterOptions { FallbackRecoveryTime = TimeSpan.FromMinutes(1), MaxAddedRetryDelay = cap };

            foreach (var sampler in new Func<double>[] { () => 0, () => 1, Random.Shared.NextDouble })
            {
                var calculator = new RetryAfterCalculator(options, BreakDuration, sampler);

                var value = calculator.Compute(baseValue, source);

                Assert.True(value!.Value - baseValue <= cap, $"baseValue={baseValue} source={source} cap={cap} produced {value} (added {value - baseValue})");
            }
        }
    }

    [Fact]
    public void A_throwing_inner_metadata_read_counts_as_no_inner_value()
    {
        var calculator = new RetryAfterCalculator(Options(), BreakDuration, sampler: () => 0);

        var value = calculator.Compute(new ThrowingMetadataLease(), LeaseSource.Distributed);

        // No inner value: the estimate is FallbackRecoveryTime (60 s).
        Assert.Equal(TimeSpan.FromSeconds(66), value);
    }

    [Fact]
    public void Saturates_at_max_value_instead_of_overflowing()
    {
        // FallbackRecoveryTime and MaxAddedRetryDelay may legitimately be TimeSpan.MaxValue; the
        // band, the doubling and the final sums must not throw OverflowException.
        var options = new ResilientRateLimiterOptions
        {
            FallbackRecoveryTime = TimeSpan.MaxValue,
            MaxAddedRetryDelay = TimeSpan.MaxValue,
        };
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

        // A recovery refusal counts as store down.
        Assert.Equal(TimeSpan.FromSeconds(22), value);
    }
}

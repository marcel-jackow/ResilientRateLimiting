using Xunit;

namespace ResilientRateLimiting.Tests;

public class ResilientRateLimiterOptionsTests
{
    private static ResilientRateLimiterOptions Valid() => new()
    {
        ExpectedReplicaCount = 3,
        FallbackRecoveryTime = TimeSpan.FromMinutes(1),
    };

    [Fact]
    public void Accepts_a_fully_specified_configuration()
    {
        Valid().Validate();
    }

    [Fact]
    public void Rejects_an_unset_replica_count()
    {
        var options = Valid();
        options.ExpectedReplicaCount = 0;

        var error = Assert.Throws<InvalidOperationException>(options.Validate);
        Assert.Contains(nameof(ResilientRateLimiterOptions.ExpectedReplicaCount), error.Message);
    }

    [Fact]
    public void Rejects_an_unset_fallback_recovery_time()
    {
        var options = Valid();
        options.FallbackRecoveryTime = TimeSpan.Zero;

        var error = Assert.Throws<InvalidOperationException>(options.Validate);
        Assert.Contains(nameof(ResilientRateLimiterOptions.FallbackRecoveryTime), error.Message);
    }

    [Fact]
    public void Does_not_require_fallback_settings_when_the_fallback_is_not_used()
    {
        new ResilientRateLimiterOptions { FailureBehavior = StoreFailureBehavior.FailOpen }.Validate();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1.5)]
    public void Rejects_a_cold_start_factor_outside_zero_to_one(double factor)
    {
        var options = Valid();
        options.ColdStartFallbackFactor = factor;

        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Fact]
    public void Rejects_a_breaker_sampling_duration_below_the_polly_minimum()
    {
        var options = Valid();
        options.BreakerSamplingDuration = TimeSpan.FromMilliseconds(400);

        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Rejects_a_failure_threshold_the_breaker_cannot_express(int failures)
    {
        var options = Valid();
        options.FailuresBeforeOpen = failures;

        var error = Assert.Throws<InvalidOperationException>(options.Validate);
        Assert.Contains(nameof(ResilientRateLimiterOptions.FailuresBeforeOpen), error.Message);
    }

    [Fact]
    public void Rejects_a_non_positive_store_timeout()
    {
        var options = Valid();
        options.StoreTimeout = TimeSpan.Zero;

        var error = Assert.Throws<InvalidOperationException>(options.Validate);
        Assert.Contains(nameof(ResilientRateLimiterOptions.StoreTimeout), error.Message);
    }

    [Fact]
    public void Rejects_a_non_positive_break_duration()
    {
        var options = Valid();
        options.BreakDuration = TimeSpan.Zero;

        var error = Assert.Throws<InvalidOperationException>(options.Validate);
        Assert.Contains(nameof(ResilientRateLimiterOptions.BreakDuration), error.Message);
    }

    [Fact]
    public void Rejects_a_partition_cap_below_one()
    {
        var options = Valid();
        options.MaxWarmPartitions = 0;

        var error = Assert.Throws<InvalidOperationException>(options.Validate);
        Assert.Contains(nameof(ResilientRateLimiterOptions.MaxWarmPartitions), error.Message);
    }

    [Theory]
    [InlineData(100, 3, 34)]
    [InlineData(100, 1, 100)]
    [InlineData(10, 4, 3)]
    public void Divides_the_shared_limit_by_the_replica_count_rounding_up(int shared, int replicas, int expected)
    {
        var options = Valid();
        options.ExpectedReplicaCount = replicas;

        Assert.Equal(expected, options.LocalPermitLimit(shared));
    }

    [Fact]
    public void Refuses_a_local_permit_limit_without_a_replica_count()
    {
        var options = Valid();
        options.ExpectedReplicaCount = 0;

        Assert.Throws<InvalidOperationException>(() => options.LocalPermitLimit(100));
    }
}

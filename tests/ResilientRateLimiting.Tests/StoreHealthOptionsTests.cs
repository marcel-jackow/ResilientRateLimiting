using Xunit;

namespace ResilientRateLimiting.Tests;

public class StoreHealthOptionsTests
{
    private static StoreHealthOptions Valid() => new();

    [Fact]
    public void Accepts_a_fully_specified_configuration()
    {
        Valid().Validate();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1.5)]
    public void Rejects_a_cold_start_factor_outside_zero_to_one(double factor)
    {
        var options = new StoreHealthOptions { ColdStartFallbackFactor = factor };

        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Fact]
    public void Rejects_a_breaker_sampling_duration_below_the_polly_minimum()
    {
        var options = new StoreHealthOptions
        {
            BreakerSamplingDuration = TimeSpan.FromMilliseconds(400),
        };

        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Rejects_a_failure_threshold_the_breaker_cannot_express(int failures)
    {
        var options = new StoreHealthOptions { FailuresBeforeOpen = failures };

        var error = Assert.Throws<InvalidOperationException>(options.Validate);
        Assert.Contains(nameof(StoreHealthOptions.FailuresBeforeOpen), error.Message);
    }

    [Fact]
    public void Rejects_a_non_positive_store_timeout()
    {
        var options = new StoreHealthOptions { StoreTimeout = TimeSpan.Zero };

        var error = Assert.Throws<InvalidOperationException>(options.Validate);
        Assert.Contains(nameof(StoreHealthOptions.StoreTimeout), error.Message);
    }

    [Fact]
    public void Rejects_a_non_positive_break_duration()
    {
        var options = new StoreHealthOptions { BreakDuration = TimeSpan.Zero };

        var error = Assert.Throws<InvalidOperationException>(options.Validate);
        Assert.Contains(nameof(StoreHealthOptions.BreakDuration), error.Message);
    }

    [Fact]
    public void Rejects_a_partition_cap_below_one()
    {
        var options = new StoreHealthOptions { MaxWarmPartitions = 0 };

        var error = Assert.Throws<InvalidOperationException>(options.Validate);
        Assert.Contains(nameof(StoreHealthOptions.MaxWarmPartitions), error.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-0.5)]
    [InlineData(1.5)]
    public void Rejects_a_failure_ratio_outside_the_unit_range(double ratio)
    {
        var options = new StoreHealthOptions { FailureRatio = ratio };

        var error = Assert.Throws<InvalidOperationException>(options.Validate);
        Assert.Contains(nameof(StoreHealthOptions.FailureRatio), error.Message);
    }

    [Fact]
    public void Reports_every_violated_rule_at_once()
    {
        var options = new StoreHealthOptions { StoreTimeout = TimeSpan.Zero, MaxWarmPartitions = 0 };

        var error = Assert.Throws<InvalidOperationException>(options.Validate);
        Assert.Contains(nameof(StoreHealthOptions.StoreTimeout), error.Message);
        Assert.Contains(nameof(StoreHealthOptions.MaxWarmPartitions), error.Message);
    }
}

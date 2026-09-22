using Xunit;

namespace ResilientRateLimiting.Tests;

public class StoreHealthOptionsTests
{
    private const int Replicas = 3;

    private static StoreHealthOptions Valid() => new() { ExpectedReplicaCount = Replicas };

    [Fact]
    public void Accepts_a_fully_specified_configuration()
    {
        Valid().Validate();
    }

    [Fact]
    public void Requires_a_replica_count_whatever_the_limiters_on_the_store_do()
    {
        // Store-scoped, so its necessity no longer depends on any limiter's failure behaviour.
        var options = new StoreHealthOptions { ExpectedReplicaCount = 0 };

        var error = Assert.Throws<InvalidOperationException>(options.Validate);
        Assert.Contains(nameof(StoreHealthOptions.ExpectedReplicaCount), error.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1.5)]
    public void Rejects_a_cold_start_factor_outside_zero_to_one(double factor)
    {
        var options = new StoreHealthOptions { ExpectedReplicaCount = Replicas, ColdStartFallbackFactor = factor };

        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Fact]
    public void Rejects_a_breaker_sampling_duration_below_the_polly_minimum()
    {
        var options = new StoreHealthOptions
        {
            ExpectedReplicaCount = Replicas,
            BreakerSamplingDuration = TimeSpan.FromMilliseconds(400),
        };

        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Rejects_a_failure_threshold_the_breaker_cannot_express(int failures)
    {
        var options = new StoreHealthOptions { ExpectedReplicaCount = Replicas, FailuresBeforeOpen = failures };

        var error = Assert.Throws<InvalidOperationException>(options.Validate);
        Assert.Contains(nameof(StoreHealthOptions.FailuresBeforeOpen), error.Message);
    }

    [Fact]
    public void Rejects_a_non_positive_store_timeout()
    {
        var options = new StoreHealthOptions { ExpectedReplicaCount = Replicas, StoreTimeout = TimeSpan.Zero };

        var error = Assert.Throws<InvalidOperationException>(options.Validate);
        Assert.Contains(nameof(StoreHealthOptions.StoreTimeout), error.Message);
    }

    [Fact]
    public void Rejects_a_non_positive_break_duration()
    {
        var options = new StoreHealthOptions { ExpectedReplicaCount = Replicas, BreakDuration = TimeSpan.Zero };

        var error = Assert.Throws<InvalidOperationException>(options.Validate);
        Assert.Contains(nameof(StoreHealthOptions.BreakDuration), error.Message);
    }

    [Fact]
    public void Rejects_a_partition_cap_below_one()
    {
        var options = new StoreHealthOptions { ExpectedReplicaCount = Replicas, MaxWarmPartitions = 0 };

        var error = Assert.Throws<InvalidOperationException>(options.Validate);
        Assert.Contains(nameof(StoreHealthOptions.MaxWarmPartitions), error.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-0.5)]
    [InlineData(1.5)]
    public void Rejects_a_failure_ratio_outside_the_unit_range(double ratio)
    {
        var options = new StoreHealthOptions { ExpectedReplicaCount = Replicas, FailureRatio = ratio };

        var error = Assert.Throws<InvalidOperationException>(options.Validate);
        Assert.Contains(nameof(StoreHealthOptions.FailureRatio), error.Message);
    }

    [Theory]
    [InlineData(100, 3, 34)]
    [InlineData(100, 1, 100)]
    [InlineData(10, 4, 3)]
    public void Divides_the_shared_limit_by_the_replica_count_rounding_up(int shared, int replicas, int expected)
    {
        var options = new StoreHealthOptions { ExpectedReplicaCount = replicas };

        Assert.Equal(expected, options.LocalPermitLimit(shared));
    }

    [Fact]
    public void Refuses_a_local_permit_limit_without_a_replica_count()
    {
        var options = new StoreHealthOptions { ExpectedReplicaCount = 0 };

        Assert.Throws<InvalidOperationException>(() => options.LocalPermitLimit(100));
    }
}

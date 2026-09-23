using Xunit;

namespace ResilientRateLimiting.Tests;

public class LocalBudgetTests
{
    [Fact]
    public void Splits_a_limit_that_divides_evenly()
    {
        Assert.Equal(25, LocalBudget.ForReplicas(sharedPermitLimit: 100, replicaCount: 4));
    }

    [Theory]
    [InlineData(100, 3, 34)]
    [InlineData(100, 1, 100)]
    [InlineData(10, 4, 3)]
    public void Divides_the_shared_limit_by_the_replica_count_rounding_up(int shared, int replicas, int expected)
    {
        Assert.Equal(expected, LocalBudget.ForReplicas(shared, replicas));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Refuses_a_replica_count_below_one(int replicas)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => LocalBudget.ForReplicas(100, replicas));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Refuses_a_shared_limit_below_one(int shared)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => LocalBudget.ForReplicas(shared, 3));
    }
}

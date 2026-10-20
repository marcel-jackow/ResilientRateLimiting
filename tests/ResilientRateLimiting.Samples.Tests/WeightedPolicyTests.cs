using Microsoft.AspNetCore.Http;
using ResilientRateLimiting.Samples.Gateway;
using Xunit;
using Yarp.ReverseProxy.Model;

namespace ResilientRateLimiting.Samples.Tests;

public class WeightedPolicyTests
{
    [Fact]
    public void The_largest_share_goes_to_the_first_destination_by_id_whatever_the_order()
    {
        var policy = new WeightedPolicy(WeightedSequence.Parse("60/30/10", 3));
        var shuffled = new[] { new DestinationState("api-c"), new DestinationState("api-a"), new DestinationState("api-b") };
        var counts = new Dictionary<string, int>();

        for (var i = 0; i < 10; i++)
        {
            var picked = policy.PickDestination(new DefaultHttpContext(), new ClusterState("apis"), shuffled)!;
            counts[picked.DestinationId] = counts.GetValueOrDefault(picked.DestinationId) + 1;
        }

        Assert.Equal(6, counts["api-a"]);
        Assert.Equal(3, counts["api-b"]);
        Assert.Equal(1, counts["api-c"]);
    }

    [Fact]
    public void No_destination_gives_no_pick()
    {
        var policy = new WeightedPolicy(WeightedSequence.Parse("even", 3));

        Assert.Null(policy.PickDestination(new DefaultHttpContext(), new ClusterState("apis"), []));
    }
}

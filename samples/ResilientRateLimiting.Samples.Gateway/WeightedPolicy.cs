using Yarp.ReverseProxy.LoadBalancing;
using Yarp.ReverseProxy.Model;

namespace ResilientRateLimiting.Samples.Gateway;

/// <summary>Sends traffic to the destinations in fixed shares, ordered by destination id so the shares never move.</summary>
public sealed class WeightedPolicy(WeightedSequence sequence) : ILoadBalancingPolicy
{
    public string Name => "Weighted";

    public DestinationState? PickDestination(HttpContext context, ClusterState cluster, IReadOnlyList<DestinationState> availableDestinations)
    {
        if (availableDestinations.Count == 0)
        {
            return null;
        }

        var ordered = availableDestinations.OrderBy(destination => destination.DestinationId, StringComparer.Ordinal).ToArray();
        return ordered[sequence.Next() % ordered.Length];
    }
}
